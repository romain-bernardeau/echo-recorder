using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Echo.Core.Storage;

namespace Echo.Ai;

/// <summary>
/// Post-transcription disk housekeeping (v2 addition — the Python app kept every
/// intermediate forever, ~1.4 GB per 1 h 30 meeting):
///  - <see cref="CleanupIntermediates"/> deletes the regenerable <c>*_16k.wav</c> /
///    <c>*_16k_trim.wav</c> prep files (recreated on demand by AudioPrep.PrepareTrack)
///    and 0-byte track WAVs (a capture that never delivered a frame);
///  - <see cref="ArchiveTrackToMp3"/> converts a raw track WAV to a 16 kHz mono MP3
///    (~55 MB/h instead of ~700 MB/h) and deletes the WAV — but ONLY after decoding
///    the MP3 back with ffmpeg and checking its duration against the WAV header
///    (never trade the only copy for a corrupt file).
/// Re-transcription keeps working from the MP3: PresentTracks resolves
/// <c>{tag}.mp3</c> via <see cref="FindTrackSource"/> and ffmpeg reads it natively.
/// ffmpeg-only by design: without ffmpeg the WAV is simply kept (logged, no error).
/// </summary>
public static class AudioArchive
{
    /// <summary>Archive bitrate: speech at 16 kHz mono is transparent enough at 64 kbps
    /// for both re-transcription and human listening.</summary>
    public const string Mp3Bitrate = "64k";

    /// <summary>Max |mp3 - wav| duration drift accepted before we refuse to delete the WAV.</summary>
    public const double MaxDurationDriftSeconds = 2.0;

    private static readonly string[] IntermediateSuffixes = { "_16k.wav", "_16k_trim.wav", "_16k.mp3", "_16k_trim.mp3" };
    private static readonly string[] Tags = { "mic", "system" };

    /// <summary>The session's source file for a track: <c>{tag}.wav</c>, else the
    /// <c>{tag}.mp3</c> archive, else null. Files under 200 bytes are ignored
    /// (empty/dead track), mirroring Transcriber.PresentTracks.</summary>
    public static string? FindTrackSource(string sessionDir, string tag)
    {
        foreach (var ext in new[] { ".wav", ".mp3" })
        {
            var p = SessionLayout.Find(sessionDir, tag + ext);
            try { if (File.Exists(p) && new FileInfo(p).Length > 200) return p; }
            catch { /* racing delete */ }
        }
        return null;
    }

    /// <summary>Delete the regenerable resample/trim intermediates of both tracks —
    /// safe whenever the track SOURCE (wav or mp3 archive) is still present, since
    /// PrepareTrack rebuilds them on the next (re)transcription. Also drops 0-byte
    /// track WAVs. Returns bytes freed.</summary>
    public static long CleanupIntermediates(string sessionDir, Action<string>? log = null)
    {
        long freed = 0;
        foreach (var tag in Tags)
        {
            bool hasSource = FindTrackSource(sessionDir, tag) is not null;
            foreach (var td in SessionLayout.TrackDirs(sessionDir))
            {
                foreach (var suffix in IntermediateSuffixes)
                {
                    // Without a source the intermediate may be the only remaining audio —
                    // keep it (retention will age it out like any other track audio).
                    if (hasSource) freed += TryDelete(Path.Combine(td, tag + suffix));
                }

                var wav = Path.Combine(td, tag + ".wav");
                try { if (File.Exists(wav) && new FileInfo(wav).Length == 0) freed += TryDelete(wav); }
                catch { /* ignore */ }
            }
        }
        if (freed > 0) log?.Invoke($"  [archive] intermédiaires supprimés ({freed / (1024 * 1024)} Mo).");
        return freed;
    }

    /// <summary>
    /// Convert one raw track WAV to <c>{track}.mp3</c> (16 kHz mono, 64 kbps) and
    /// delete the WAV once the MP3 has been decode-verified. Returns the MP3 path,
    /// or null when skipped (no ffmpeg, unreadable wav, failed verification).
    /// </summary>
    public static string? ArchiveTrackToMp3(string wavPath, Action<string>? log = null) =>
        ArchiveTrackToMp3(wavPath, log, CancellationToken.None);

    /// <summary>Same as the main overload; only a cancellation of <paramref name="ct"/> throws
    /// (the WAV is then kept and the partial MP3 removed).</summary>
    public static string? ArchiveTrackToMp3(string wavPath, Action<string>? log, CancellationToken ct)
    {
        if (!wavPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || !File.Exists(wavPath)) return null;
        if (!FfmpegUsable())
        {
            log?.Invoke("  [archive] ffmpeg introuvable, WAV original conservé.");
            return null;
        }

        double wavDur;
        try { wavDur = WavHeaderDuration(wavPath); }
        catch (Exception ex)
        {
            log?.Invoke($"  [archive] WAV illisible ({Path.GetFileName(wavPath)}: {ex.Message}), conservé.");
            return null;
        }

        var mp3Path = Path.ChangeExtension(wavPath, ".mp3");
        try
        {
            var (code, err) = Ffmpeg.Run(new[] { "-y", "-i", wavPath, "-ac", "1", "-ar", "16000",
                                                 "-c:a", "libmp3lame", "-b:a", Mp3Bitrate, mp3Path },
                                         Ffmpeg.TimeoutFor(wavDur), ct);
            if (code != 0) throw new InvalidOperationException($"ffmpeg exit {code}: {Tail(err, 300)}");

            // Integrity gate: fully decode the MP3 (validates the stream) and compare
            // its decoded duration to the WAV header duration.
            double mp3Dur = DecodedDuration(mp3Path, Ffmpeg.TimeoutFor(wavDur), ct);
            if (Math.Abs(mp3Dur - wavDur) > MaxDurationDriftSeconds)
                throw new InvalidOperationException($"durée mp3 {mp3Dur:0.0}s ≠ wav {wavDur:0.0}s");

            File.Delete(wavPath);
            log?.Invoke($"  [archive] {Path.GetFileName(wavPath)} → {Path.GetFileName(mp3Path)} "
                        + $"({new FileInfo(mp3Path).Length / (1024 * 1024)} Mo).");
            return mp3Path;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try { if (File.Exists(mp3Path)) File.Delete(mp3Path); } catch { /* best effort */ }
            throw;
        }
        catch (Exception ex)
        {
            log?.Invoke($"  [archive] compression échouée ({Path.GetFileName(wavPath)}: {ex.Message}), WAV conservé.");
            try { if (File.Exists(mp3Path)) File.Delete(mp3Path); } catch { /* best effort */ }
            return null;
        }
    }

    /// <summary>
    /// Encode a prepared 16 kHz mono WAV into a compact MP3 for the API UPLOAD only
    /// (~4× fewer bytes: a 2 h track drops from ~230 MB of PCM to ~58 MB — fewer
    /// network timeouts, much cheaper 429/5xx retries). The WAV is NOT deleted: it
    /// stays the fallback if the server rejects the MP3. Returns null (caller uploads
    /// the WAV as before) when ffmpeg is unusable or the encode fails; never throws.
    /// </summary>
    public static string? EncodeForUpload(string wavPath, Action<string>? log = null) =>
        EncodeForUpload(wavPath, log, CancellationToken.None);

    /// <summary>Same as the main overload; only a cancellation of <paramref name="ct"/> throws.</summary>
    public static string? EncodeForUpload(string wavPath, Action<string>? log, CancellationToken ct)
    {
        if (!wavPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || !File.Exists(wavPath)) return null;
        if (!FfmpegUsable()) return null;
        var mp3Path = Path.ChangeExtension(wavPath, ".mp3");   // {tag}_16k[_trim].mp3 — never collides with the {tag}.mp3 archive
        try
        {
            var (code, err) = Ffmpeg.Run(new[] { "-y", "-i", wavPath, "-ac", "1", "-ar", "16000",
                                                 "-c:a", "libmp3lame", "-b:a", Mp3Bitrate, mp3Path },
                                         Ffmpeg.TimeoutFor(wavPath), ct);
            if (code != 0) throw new InvalidOperationException($"ffmpeg exit {code}: {Tail(err, 300)}");
            // Sanity floor only (even 1 s of speech is ~8 KB at 64 kbps): a corrupt
            // upload just fails the API call and the caller falls back to the WAV.
            if (new FileInfo(mp3Path).Length < 1000) throw new InvalidOperationException("mp3 quasi vide");
            return mp3Path;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try { if (File.Exists(mp3Path)) File.Delete(mp3Path); } catch { /* best effort */ }
            throw;
        }
        catch (Exception ex)
        {
            log?.Invoke($"  [upload] encodage mp3 échoué ({ex.Message}), envoi du WAV.");
            try { if (File.Exists(mp3Path)) File.Delete(mp3Path); } catch { /* best effort */ }
            return null;
        }
    }

    // ── ffmpeg plumbing (shared bounded runner: Ffmpeg in AudioPrep.cs) ──────────

    private static bool? _ffmpegUsable;

    /// <summary>True when the resolved ffmpeg actually launches (cached probe).</summary>
    public static bool FfmpegUsable()
    {
        if (_ffmpegUsable is { } cached) return cached;
        try
        {
            var (code, _) = Ffmpeg.Run(new[] { "-version" }, TimeSpan.FromSeconds(30), CancellationToken.None);
            _ffmpegUsable = code == 0;
        }
        catch { _ffmpegUsable = false; }
        return _ffmpegUsable.Value;
    }

    /// <summary>Duration decoded by ffmpeg (<c>-f null -</c>): the max <c>time=…</c>
    /// progress stamp, falling back to the header <c>Duration:</c> line.</summary>
    internal static double DecodedDuration(string mediaPath) =>
        DecodedDuration(mediaPath, Ffmpeg.TimeoutFor(mediaPath), CancellationToken.None);

    internal static double DecodedDuration(string mediaPath, TimeSpan timeout, CancellationToken ct)
    {
        var (code, err) = Ffmpeg.Run(new[] { "-hide_banner", "-i", mediaPath, "-f", "null", "-" }, timeout, ct);
        if (code != 0) throw new InvalidOperationException($"décodage impossible (exit {code})");
        double best = -1;
        foreach (Match m in Regex.Matches(err, @"(?:time=|Duration: )(\d+):(\d+):(\d+(?:\.\d+)?)"))
        {
            double t = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
                     + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60
                     + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            if (t > best) best = t;
        }
        if (best < 0) throw new InvalidOperationException("durée introuvable dans la sortie ffmpeg");
        return best;
    }

    /// <summary>WAV duration from the RIFF header alone — streaming chunk walk, the
    /// data chunk is SKIPPED with Seek (AudioPrep.WavDuration loads the whole file
    /// into memory, which is a no-go on a 1 GB capture).</summary>
    internal static double WavHeaderDuration(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(fs);
        if (fs.Length < 12) throw new InvalidDataException("WAV trop petit");
        fs.Seek(12, SeekOrigin.Begin);                       // RIFF....WAVE

        int sampleRate = 0, channels = 0, bits = 0;
        long dataBytes = -1;
        while (fs.Position + 8 <= fs.Length)
        {
            string id = Encoding.ASCII.GetString(br.ReadBytes(4));
            uint size = br.ReadUInt32();
            long body = fs.Position;
            if (id == "fmt ")
            {
                br.ReadUInt16();                             // wFormatTag
                channels = br.ReadUInt16();
                sampleRate = (int)br.ReadUInt32();
                br.ReadUInt32();                             // byte rate
                br.ReadUInt16();                             // block align
                bits = br.ReadUInt16();
            }
            else if (id == "data")
            {
                // A still-open capture may declare 0/0xFFFFFFFF: fall back to the
                // actual remaining file length.
                dataBytes = size == 0 || size == 0xFFFFFFFF ? fs.Length - body : size;
            }
            long next = body + size + (size % 2);            // chunks are word-aligned
            if (next <= body || next > fs.Length) break;
            fs.Seek(next, SeekOrigin.Begin);
        }

        int bpf = channels * (bits / 8);
        if (sampleRate <= 0 || bpf <= 0 || dataBytes < 0)
            throw new InvalidDataException("en-tête WAV incomplet");
        return (double)(dataBytes / bpf) / sampleRate;
    }

    internal static long TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return 0;
            long size = new FileInfo(path).Length;
            File.Delete(path);
            return size;
        }
        catch { return 0; }
    }

    private static string Tail(string s, int n) => s.Length <= n ? s : s[^n..];
}
