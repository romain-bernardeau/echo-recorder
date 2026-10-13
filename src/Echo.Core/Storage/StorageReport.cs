namespace Echo.Core.Storage;

/// <summary>
/// What the recordings folder actually weighs, broken down the way a user thinks about it:
/// the raw tracks (the heavy, disposable part), the per-track MP3 archives, the mixdown kept
/// for listening, and everything else (transcripts, CR, notes, screenshots — negligible).
/// Feeds the Storage page so the decision to clean up is made on figures rather than on a
/// vague feeling that "AppData is getting big".
/// </summary>
public static class StorageReport
{
    public sealed record SessionUse(string Name, string Path, long Bytes, long AudioBytes, bool HasTranscript);

    public sealed record Report(
        long TotalBytes,
        long RawTrackBytes,
        long TrackArchiveBytes,
        long MixdownBytes,
        long OtherBytes,
        int SessionCount,
        int UntranscribedCount,
        long UntranscribedBytes,
        IReadOnlyList<SessionUse> Heaviest);

    private static readonly string[] Tags = { "mic", "system" };

    public static Report Build(string recordingsDir, int topCount = 8)
    {
        long raw = 0, archive = 0, mixdown = 0, other = 0, untrBytes = 0;
        int sessions = 0, untranscribed = 0;
        var uses = new List<SessionUse>();

        if (!Directory.Exists(recordingsDir))
            return new Report(0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<SessionUse>());

        foreach (var dir in SafeDirs(recordingsDir))
        {
            long sRaw = 0, sArchive = 0, sMix = 0, sOther = 0;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    long len;
                    try { len = new FileInfo(f).Length; } catch { continue; }
                    var name = Path.GetFileName(f);
                    if (IsTrack(name, ".wav")) sRaw += len;
                    else if (IsTrack(name, ".mp3")) sArchive += len;
                    else if (name.EndsWith(" - Audio.mp3", StringComparison.OrdinalIgnoreCase)) sMix += len;
                    else sOther += len;
                }
            }
            catch { continue; }

            long total = sRaw + sArchive + sMix + sOther;
            if (total == 0) continue;

            sessions++;
            raw += sRaw; archive += sArchive; mixdown += sMix; other += sOther;
            bool hasTr = RetentionSweeper.HasTranscript(dir);
            if (!hasTr) { untranscribed++; untrBytes += total; }
            uses.Add(new SessionUse(Path.GetFileName(dir.TrimEnd('\\', '/')), dir, total, sRaw + sArchive + sMix, hasTr));
        }

        var heaviest = uses.OrderByDescending(u => u.Bytes).Take(Math.Max(0, topCount)).ToList();
        return new Report(raw + archive + mixdown + other, raw, archive, mixdown, other,
                          sessions, untranscribed, untrBytes, heaviest);
    }

    /// <summary>"mic.wav"/"system.wav" and their _16k prep leftovers — everything that the
    /// retention rules may reclaim, as opposed to the mixdown kept for listening.</summary>
    private static bool IsTrack(string name, string ext)
    {
        if (!name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return false;
        var stem = name[..^ext.Length];
        foreach (var t in Tags)
            if (stem.Equals(t, StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith(t + "_16k", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static IEnumerable<string> SafeDirs(string root)
    {
        try { return Directory.EnumerateDirectories(root).ToList(); }
        catch { return Array.Empty<string>(); }
    }
}
