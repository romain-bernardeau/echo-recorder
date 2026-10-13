using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Echo.Core.Storage;

/// <summary>
/// Disk housekeeping over the recordings root (run at startup and from the Storage page).
/// Three independent passes, from the safest to the most aggressive:
///  1. ALWAYS, in every session: delete the regenerable <c>*_16k(.trim).wav</c> prep files and
///     the <c>*_16k[_trim]_cN.wav/.mp3</c> upload pieces (only while the track source is still
///     there) and 0-byte track WAVs. These are pure leftovers — a failed transcription can
///     strand hundreds of MB of them.
///  2. <c>audio_retention_days</c> &gt; 0: in TRANSCRIBED sessions older than that, delete the
///     per-track audio (wav + mp3 archives).
///  3. <c>failed_audio_retention_days</c> &gt; 0: same, in sessions with NO transcript that
///     really FAILED (<c>.echo_status</c> = error, or a capture rebuilt by OrphanRecovery), on a
///     deliberately longer delay. Skipping those unconditionally is what let 15 GB of dead
///     recordings pile up (2026-09-18), but their audio is the only copy, so the delay is long
///     and a session still queued or being processed is never touched. A session that was
///     simply never transcribed (auto_transcribe off) is NOT a failure and is left alone.
/// The transcripts, the CR, the notes and the mixdown "… - Audio.mp3" are NEVER touched: the
/// meeting stays readable and listenable whatever the sweep does.
/// File ops mirror AudioArchive but live here so Echo.Core stays UI/audio-free.
/// </summary>
public static class RetentionSweeper
{
    public sealed record Result(int SessionsSwept, long BytesFreed);

    private static readonly string[] Tags = { "mic", "system" };
    private static readonly string[] IntermediateSuffixes = { "_16k.wav", "_16k_trim.wav", "_16k.mp3", "_16k_trim.mp3" };
    // Upload pieces of a chunked transcription (AudioPrep.SplitWav "{stem}_c{i}.wav" and their
    // upload MP3): deleted after the run, but a crash or a kill mid-upload leaves them behind.
    private static readonly Regex ChunkPiece = new(@"^(?<tag>mic|system)_16k(_trim)?_c\d+\.(wav|mp3)$",
                                                   RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static Result Sweep(string recordingsDir, int retentionDays, DateTime nowLocal, Action<string>? log = null)
        => Sweep(recordingsDir, retentionDays, failedRetentionDays: 0, nowLocal, log);

    /// <param name="dryRun">Measure only: report what WOULD be freed without deleting anything
    /// (drives the "you will recover N GB" line before the user confirms).</param>
    public static Result Sweep(string recordingsDir, int retentionDays, int failedRetentionDays,
                               DateTime nowLocal, Action<string>? log = null, bool dryRun = false)
    {
        int sessions = 0;
        long freed = 0;
        if (!Directory.Exists(recordingsDir)) return new(0, 0);

        foreach (var dir in SafeDirs(recordingsDir))
        {
            try
            {
                // "running" (being recorded or processed) and "waiting" (queued for a retry)
                // are live sessions — never touch their audio. A plain "error" is fair game
                // once it is old enough; that is exactly what pass 3 is for.
                if (IsLive(dir)) continue;

                bool transcribed = HasTranscript(dir);
                // Pass 3 only for a real failure: an untranscribed session that nobody tried to
                // transcribe (auto_transcribe off) keeps its only copy of the audio.
                bool failed = !transcribed && IsFailed(dir);
                // In dry-run mode nothing is deleted, so a file the intermediates pass would
                // have removed is still there when the retention pass looks at it. Remember
                // what has already been counted or the estimate over-states what it frees.
                var counted = dryRun ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : null;
                long f = SweepIntermediates(dir, dryRun, counted);

                int days = transcribed ? retentionDays : failed ? failedRetentionDays : 0;
                if (days > 0 && SessionDate(dir) is { } d && (nowLocal.Date - d.Date).TotalDays >= days)
                {
                    foreach (var tag in Tags)
                    {
                        f += Remove(Path.Combine(dir, tag + ".wav"), dryRun, counted);
                        f += Remove(Path.Combine(dir, tag + ".mp3"), dryRun, counted);
                        foreach (var s in IntermediateSuffixes) f += Remove(Path.Combine(dir, tag + s), dryRun, counted);
                    }
                    foreach (var piece in ChunkPieces(dir, tag: null)) f += Remove(piece, dryRun, counted);
                }

                if (f > 0)
                {
                    sessions++;
                    freed += f;
                    if (!dryRun)
                        log?.Invoke($"[retention] {Path.GetFileName(dir)} : {f / (1024 * 1024)} Mo libérés"
                                    + (transcribed ? "" : " (réunion sans transcription)"));
                }
            }
            catch { /* one broken session must not stop the sweep */ }
        }

        // One line per sweep, even when nothing was freed: "did the cleanup run?" must be
        // answerable from the log.
        if (!dryRun)
            log?.Invoke($"[retention] terminé : {sessions} session(s), {freed / (1024 * 1024)} Mo libérés au total.");
        return new(sessions, freed);
    }

    /// <summary>A session THIS Echo is processing or holding for a retry. Same reading as the
    /// list badge (<see cref="SessionStatus.Effective"/>): a "running" left by a dead process or
    /// older than 3 h is not live, so a session frozen by a crash is cleaned like any other.</summary>
    internal static bool IsLive(string dir)
    {
        try
        {
            return SessionStatus.IsLive(SessionStatus.Read(dir), Environment.ProcessId,
                                        DateTimeOffset.Now.ToUnixTimeSeconds());
        }
        catch { return true; }         // unreadable sentinel → assume live, never delete
    }

    /// <summary>Processing really failed: an "error" sentinel, or a capture that only exists
    /// because OrphanRecovery rebuilt it (session.json "recovered": true).</summary>
    internal static bool IsFailed(string dir)
    {
        try
        {
            if (SessionStatus.Read(dir) is { State: "error" }) return true;
        }
        catch { /* unreadable: fall through to session.json */ }
        try
        {
            var sj = Path.Combine(dir, "session.json");
            if (!File.Exists(sj)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(sj));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("recovered", out var r) && r.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }

    private static IEnumerable<string> ChunkPieces(string dir, string? tag)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*_c*.*")
                .Where(f =>
                {
                    var m = ChunkPiece.Match(Path.GetFileName(f));
                    return m.Success && (tag is null || string.Equals(m.Groups["tag"].Value, tag, StringComparison.OrdinalIgnoreCase));
                })
                .ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Regenerable intermediates (source still present) + 0-byte track WAVs.</summary>
    private static long SweepIntermediates(string dir, bool dryRun = false, HashSet<string>? counted = null)
    {
        long f = 0;
        foreach (var tag in Tags)
        {
            bool hasSource = HasTrackSource(dir, tag);
            if (hasSource)
            {
                foreach (var s in IntermediateSuffixes) f += Remove(Path.Combine(dir, tag + s), dryRun, counted);
                foreach (var piece in ChunkPieces(dir, tag)) f += Remove(piece, dryRun, counted);
            }

            var wav = Path.Combine(dir, tag + ".wav");
            try { if (File.Exists(wav) && new FileInfo(wav).Length == 0) f += Remove(wav, dryRun, counted); }
            catch { /* ignore */ }
        }
        return f;
    }

    private static bool HasTrackSource(string dir, string tag)
    {
        foreach (var ext in new[] { ".wav", ".mp3" })
        {
            try
            {
                var p = Path.Combine(dir, tag + ext);
                if (File.Exists(p) && new FileInfo(p).Length > 200) return true;
            }
            catch { /* ignore */ }
        }
        return false;
    }

    internal static bool HasTranscript(string dir) => SessionFiles.HasTranscript(dir);

    /// <summary>Session day: folder-name prefix "yyyyMMdd - …", else meeting.json
    /// "started" (unix epoch), else the folder's creation time.</summary>
    internal static DateTime? SessionDate(string dir)
    {
        var name = Path.GetFileName(dir.TrimEnd('\\', '/'));
        if (name.Length >= 8 && DateTime.TryParseExact(name[..8], "yyyyMMdd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;

        try
        {
            var mj = Path.Combine(dir, "meeting.json");
            if (File.Exists(mj))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(mj));
                if (doc.RootElement.TryGetProperty("started", out var s)
                    && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var epoch))
                    return DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime().DateTime;
            }
        }
        catch { /* fall through */ }

        try { return Directory.GetCreationTime(dir); } catch { return null; }
    }

    private static IEnumerable<string> SafeDirs(string root)
    {
        try { return Directory.EnumerateDirectories(root).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Delete and return the bytes reclaimed; in <paramref name="dryRun"/> mode only
    /// measure them, so the same walk answers "how much would this free?".</summary>
    /// <param name="counted">Dry-run only: paths already measured, so nothing is billed twice.</param>
    private static long Remove(string path, bool dryRun, HashSet<string>? counted = null)
    {
        try
        {
            if (!File.Exists(path)) return 0;
            if (counted is not null && !counted.Add(path)) return 0;
            long size = new FileInfo(path).Length;
            if (!dryRun) File.Delete(path);
            return size;
        }
        catch { return 0; }
    }
}
