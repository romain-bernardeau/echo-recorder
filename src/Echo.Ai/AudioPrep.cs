using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Echo.Ai;

/// <summary>Result of preparing one track for transcription.</summary>
public sealed record TrackPrep(
    string? WavPath,                                   // null => track was fully silent, skip
    IReadOnlyList<(double TrimStart, double TrimEnd, double RealStart)> Mapping,
    double Duration, double Kept, double Removed, bool Trimmed);

/// <summary>
/// Per-track audio preparation for Voxtral (port of <c>audio_prep.py</c>). Shells
/// out to ffmpeg for resample (16 kHz mono, soxr precision 28) and silence
/// detection, and trims silence in pure C# by slicing PCM frames. The
/// timestamp-remap maths (<see cref="BuildMapping"/>/<see cref="RemapTime"/>) and
/// the speech-interval complement are ported exactly, they underpin attribution.
/// Every ffmpeg call goes through <see cref="Ffmpeg.Run"/> (timeout + kill + async stderr).
/// </summary>
public static class AudioPrep
{
    /// <summary>ffmpeg executable (expected on PATH, like the Python app).</summary>
    public static string FfmpegExe { get; set; } = "ffmpeg";

    /// <summary>
    /// Resolve the ffmpeg executable: an explicit existing <see cref="FfmpegExe"/>
    /// path wins; otherwise a bundled <c>&lt;appdir&gt;\bin\ffmpeg.exe</c> if present;
    /// otherwise the bare command (PATH lookup, like the Python app).
    /// </summary>
    public static string ResolveFfmpeg()
    {
        try
        {
            if (!string.Equals(FfmpegExe, "ffmpeg", StringComparison.OrdinalIgnoreCase) && File.Exists(FfmpegExe))
                return FfmpegExe;
            var bundled = Path.Combine(AppContext.BaseDirectory, "bin", "ffmpeg.exe");
            if (File.Exists(bundled)) return bundled;
        }
        catch { /* fall back to PATH */ }
        return FfmpegExe;
    }

    // ── pure timeline maths (no ffmpeg; unit-testable) ─────────────────────────

    /// <summary>Complement of the (padded) silences = speech spans. Each silence is
    /// shrunk by <paramref name="pad"/> per side, kept only if still >= minDrop.</summary>
    public static List<(double A, double B)> SpeechIntervals(
        IEnumerable<(double A, double B)> silences, double duration, double pad = 0.35, double minDrop = 0.4)
    {
        var drops = new List<(double A, double B)>();
        foreach (var (a, b) in silences)
        {
            double aa = a + pad, bb = b - pad;
            if (bb - aa >= minDrop) drops.Add((aa, bb));
        }
        drops.Sort((x, y) => x.A.CompareTo(y.A));
        var speech = new List<(double, double)>();
        double prev = 0.0;
        foreach (var (aa, bb) in drops)
        {
            if (aa > prev) speech.Add((prev, aa));
            prev = Math.Max(prev, bb);
        }
        if (duration > prev) speech.Add((prev, duration));
        return speech;
    }

    /// <summary>trim->real table: for each kept speech span [a,b] laid end-to-end in
    /// the trimmed audio, remember (trim_start, trim_end, real_start).</summary>
    public static (List<(double TrimStart, double TrimEnd, double RealStart)> Mapping, double Kept) BuildMapping(
        IEnumerable<(double A, double B)> speech)
    {
        var mapping = new List<(double, double, double)>();
        double acc = 0.0;
        foreach (var (a, b) in speech)
        {
            double d = b - a;
            if (d <= 0) continue;
            mapping.Add((acc, acc + d, a));
            acc += d;
        }
        return (mapping, acc);
    }

    /// <summary>Convert a trimmed-audio timestamp back to real track time.</summary>
    public static double? RemapTime(double? tTrim, IReadOnlyList<(double TrimStart, double TrimEnd, double RealStart)> mapping)
    {
        if (tTrim is null || mapping.Count == 0) return tTrim;
        double t = tTrim.Value;
        foreach (var (ts, te, ra) in mapping)
            if (t <= te + 1e-6) return ra + Math.Max(0.0, t - ts);
        var (lts, _, lra) = mapping[^1];
        return lra + (t - lts);   // beyond the last span: extrapolate
    }

    // ── ffmpeg-backed pipeline ─────────────────────────────────────────────────

    public static string ResampleTo16kMono(string inWav, string outWav) =>
        ResampleTo16kMono(inWav, outWav, CancellationToken.None);

    public static string ResampleTo16kMono(string inWav, string outWav, CancellationToken ct)
    {
        // soxr (precision 28) when the ffmpeg build provides it; otherwise the built-in
        // swr resampler, which is more than enough for 16 kHz mono speech. The bundled
        // essentials ffmpeg has no libsoxr, so requesting it there fails the whole run.
        string fc = SoxrAvailable()
            ? "aresample=resampler=soxr:precision=28:osr=16000,aformat=channel_layouts=mono"
            : "aresample=osr=16000,aformat=channel_layouts=mono";
        var (code, err) = Ffmpeg.Run(new[] { "-y", "-i", inWav, "-af", fc, "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", outWav },
            Ffmpeg.TimeoutFor(inWav), ct);
        if (code != 0) throw new InvalidOperationException($"ffmpeg resample failed ({inWav}): {Tail(err)}");
        return outWav;
    }

    /// <summary>Decode ANY ffmpeg-readable file (mp3, m4a, mp4 video…) to a 16 kHz mono PCM
    /// WAV, dropping any video stream — the entry point of the audio import.</summary>
    public static string ConvertToWav16kMono(string input, string outWav) =>
        ConvertToWav16kMono(input, outWav, CancellationToken.None);

    public static string ConvertToWav16kMono(string input, string outWav, CancellationToken ct)
    {
        var (code, err) = Ffmpeg.Run(new[] { "-y", "-i", input, "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", outWav },
            Ffmpeg.TimeoutFor(input), ct);
        if (code != 0)
            throw new InvalidOperationException($"Impossible de lire ce fichier audio (ffmpeg code {code}) : {Tail(err)}");
        return outWav;
    }

    /// <summary>Does the resolved ffmpeg build ship libsoxr? Probed once via
    /// <c>-buildconf</c> and cached. When absent we fall back to the built-in swr
    /// resampler so transcription works on the bundled essentials ffmpeg. A probe that
    /// could not even run (ffmpeg missing) is not cached.</summary>
    private static bool? _soxr;
    private static bool SoxrAvailable()
    {
        if (_soxr is bool cached) return cached;
        try
        {
            var r = Ffmpeg.RunFull(new[] { "-hide_banner", "-buildconf" }, TimeSpan.FromSeconds(30), CancellationToken.None);
            bool ok = (r.Stdout + r.Stderr).Contains("libsoxr", StringComparison.OrdinalIgnoreCase);
            _soxr = ok;
            return ok;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The silencedetect filter, numbers in invariant culture (fr-FR would print
    /// "1,6" and break the filter syntax).</summary>
    internal static string SilenceDetectFilter(double noiseDb, double minSil) =>
        $"silencedetect=noise={noiseDb.ToString(CultureInfo.InvariantCulture)}dB:d={minSil.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Silence intervals via ffmpeg silencedetect. A silence_start without a
    /// silence_end (EOF) is closed on the total duration.</summary>
    public static (List<(double Start, double End)> Silences, double Duration) DetectSilences(
        string wav16k, double noiseDb = -33.0, double minSil = 1.6) =>
        DetectSilences(wav16k, noiseDb, minSil, CancellationToken.None);

    public static (List<(double Start, double End)> Silences, double Duration) DetectSilences(
        string wav16k, double noiseDb, double minSil, CancellationToken ct)
    {
        var (_, err) = Ffmpeg.Run(new[]
        {
            "-i", wav16k, "-af", SilenceDetectFilter(noiseDb, minSil), "-f", "null", "-",
        }, Ffmpeg.TimeoutFor(wav16k), ct);
        double dur = WavDuration(wav16k);
        var sils = new List<(double, double)>();
        double? cur = null;
        foreach (var line in err.Split('\n'))
        {
            if (line.Contains("silence_start:"))
            {
                cur = TryParse(line.Split("silence_start:")[1].Trim());
            }
            else if (line.Contains("silence_end:") && cur is not null)
            {
                var end = TryParse(line.Split("silence_end:")[1].Split('|')[0].Trim());
                if (end is not null) sils.Add((cur.Value, end.Value));
                cur = null;
            }
        }
        if (cur is not null) sils.Add((cur.Value, dur));
        return (sils, dur);
    }

    /// <summary>Full per-track pipeline -> resampled/trimmed WAV path + trim mapping + info.
    /// Returns a null WavPath when the track is fully silent.</summary>
    public static TrackPrep PrepareTrack(string inWav, string workDir, string tag, bool trim = true,
        double noiseDb = -33.0, double minSil = 1.6, double pad = 0.35, double keepFullBelow = 5.0) =>
        PrepareTrack(inWav, workDir, tag, CancellationToken.None, trim, noiseDb, minSil, pad, keepFullBelow);

    /// <summary>Same as the main overload, with every ffmpeg call cancellable.</summary>
    public static TrackPrep PrepareTrack(string inWav, string workDir, string tag, CancellationToken ct, bool trim = true,
        double noiseDb = -33.0, double minSil = 1.6, double pad = 0.35, double keepFullBelow = 5.0)
    {
        var resampled = Path.Combine(workDir, $"{tag}_16k.wav");
        ResampleTo16kMono(inWav, resampled, ct);
        double fullDur = WavDuration(resampled);
        var identity = new List<(double, double, double)> { (0.0, fullDur, 0.0) };

        if (!trim)
            return new TrackPrep(resampled, identity, fullDur, fullDur, 0.0, false);

        var (silences, dur) = DetectSilences(resampled, noiseDb, minSil, ct);
        var speech = SpeechIntervals(silences, dur, pad);
        if (speech.Count == 0)
            return new TrackPrep(null, new List<(double, double, double)>(), fullDur, 0.0, fullDur, false); // silent

        var (mapping, kept) = BuildMapping(speech);
        double removed = fullDur - kept;
        if (removed < keepFullBelow)
            return new TrackPrep(resampled, identity, fullDur, fullDur, 0.0, false); // not worth trimming

        var trimmed = Path.Combine(workDir, $"{tag}_16k_trim.wav");
        try
        {
            WriteTrimmed(resampled, speech, trimmed);
        }
        catch
        {
            // Trim is opportunistic, never essential: on any failure transcribe the full track.
            return new TrackPrep(resampled, identity, fullDur, fullDur, 0.0, false);
        }
        return new TrackPrep(trimmed, mapping, fullDur, kept, removed, true);
    }

    // ── chunking ───────────────────────────────────────────────────────────────

    /// <summary>How far before the nominal cut a quieter cut point is searched (seconds).</summary>
    internal const double SplitSearchSeconds = 30.0;
    /// <summary>Energy window of the cut search (seconds).</summary>
    internal const double SplitWindowSeconds = 0.05;
    /// <summary>Below this RMS (fraction of full scale, -33 dBFS = the silencedetect
    /// threshold) the nominal cut point is already in a pause and is kept as is.</summary>
    internal const double SplitQuietRms = 0.0224;

    /// <summary>
    /// Split a prepared PCM WAV into consecutive pieces of at most <paramref name="chunkSeconds"/>
    /// (pure frame slicing, no ffmpeg). Returns (path, start offset in seconds) per piece, in
    /// order; a track shorter than one chunk comes back as a single entry pointing at the
    /// ORIGINAL file (nothing written). Pieces are named <c>{base}_c{index}.wav</c>.
    /// Each cut lands on the lowest-energy 50 ms window of the last 30 s before the nominal
    /// cut (unless the nominal point is already in a pause), so a word is never sliced in
    /// two. The last piece absorbs a tail shorter than <paramref name="minTailSeconds"/> so
    /// Whisper never receives a 2 s fragment (hallucination-prone).
    /// </summary>
    public static List<(string Path, double StartSeconds)> SplitWav(string wavPath, int chunkSeconds, double minTailSeconds = 20.0)
    {
        if (chunkSeconds <= 0) return new List<(string, double)> { (wavPath, 0.0) };
        var (fmt, data) = ReadWav(wavPath);
        int bpf = fmt.Channels * (fmt.BitsPerSample / 8);
        if (bpf == 0 || fmt.SampleRate == 0) return new List<(string, double)> { (wavPath, 0.0) };
        long totalFrames = data.Length / bpf;
        long chunkFrames = (long)chunkSeconds * fmt.SampleRate;
        if (totalFrames <= chunkFrames) return new List<(string, double)> { (wavPath, 0.0) };

        var outp = new List<(string, double)>();
        var dir = Path.GetDirectoryName(wavPath) ?? "";
        var stem = Path.GetFileNameWithoutExtension(wavPath);
        long minTailFrames = (long)(minTailSeconds * fmt.SampleRate);
        // Never search further back than half a chunk (keeps pieces reasonably long).
        long searchFrames = Math.Min((long)(SplitSearchSeconds * fmt.SampleRate), chunkFrames / 2);
        long pos = 0;
        int idx = 0;
        while (pos < totalFrames)
        {
            long remaining = totalFrames - pos;
            long len;
            // Merge a tiny tail into this piece rather than emitting it alone.
            if (remaining <= chunkFrames || remaining - chunkFrames < minTailFrames)
                len = remaining;
            else
                len = QuietestCut(data, fmt, pos + chunkFrames - searchFrames, pos + chunkFrames) - pos;
            if (len <= 0) len = Math.Min(chunkFrames, remaining);

            var piece = Path.Combine(dir, $"{stem}_c{idx}.wav");
            var slice = new byte[len * bpf];
            Array.Copy(data, pos * bpf, slice, 0, slice.Length);
            WriteWav(piece, fmt, slice);
            outp.Add((piece, (double)pos / fmt.SampleRate));
            pos += len;
            idx++;
        }
        return outp;
    }

    /// <summary>
    /// Cut frame in [<paramref name="from"/>, <paramref name="to"/>]: <paramref name="to"/>
    /// itself when the 50 ms just before it is already quiet, else the middle of the
    /// lowest-RMS 50 ms window of the range (latest one on ties). 16-bit PCM only; any
    /// other format keeps the nominal cut.
    /// </summary>
    private static long QuietestCut(byte[] data, WavFormat fmt, long from, long to)
    {
        if (fmt.BitsPerSample != 16) return to;
        int win = Math.Max(1, (int)(SplitWindowSeconds * fmt.SampleRate));
        from = Math.Max(0, from);
        if (to - from < win) return to;

        double quiet = SplitQuietRms * 32768.0;
        if (WindowRms(data, fmt.Channels, to - win, win) <= quiet) return to;

        long best = to;
        double bestRms = double.MaxValue;
        for (long s = to - win; s >= from; s -= win)
        {
            double rms = WindowRms(data, fmt.Channels, s, win);
            if (rms < bestRms)
            {
                bestRms = rms;
                best = s + win / 2;
            }
        }
        return best;
    }

    // RMS (int16 units) of `frames` frames from `startFrame`, all channels.
    private static double WindowRms(byte[] data, int channels, long startFrame, int frames)
    {
        long first = startFrame * channels;
        long count = (long)frames * channels;
        double sum = 0;
        for (long i = 0; i < count; i++)
        {
            long b = (first + i) * 2;
            short v = (short)(data[b] | (data[b + 1] << 8));
            sum += (double)v * v;
        }
        return count > 0 ? Math.Sqrt(sum / count) : 0.0;
    }

    /// <summary>Rebuild trimmed audio by slicing PCM frames (16 kHz mono s16le WAV).</summary>
    public static void WriteTrimmed(string wav16k, IEnumerable<(double A, double B)> speech, string outWav)
    {
        var (fmt, data) = ReadWav(wav16k);
        int bpf = fmt.Channels * (fmt.BitsPerSample / 8);
        int totalFrames = data.Length / bpf;
        using var ms = new MemoryStream();
        foreach (var (a, b) in speech)
        {
            int s = Math.Max(0, (int)Math.Round(a * fmt.SampleRate)) * bpf;
            int e = Math.Min(totalFrames, (int)Math.Round(b * fmt.SampleRate)) * bpf;
            if (e > s) ms.Write(data, s, e - s);
        }
        WriteWav(outWav, fmt, ms.ToArray());
    }

    public static double WavDuration(string path)
    {
        var (fmt, data) = ReadWav(path);
        int bpf = fmt.Channels * (fmt.BitsPerSample / 8);
        if (fmt.SampleRate == 0 || bpf == 0) return 0.0;
        return (double)(data.Length / bpf) / fmt.SampleRate;
    }

    // ── minimal WAV I/O (PCM) ──────────────────────────────────────────────────

    private readonly record struct WavFormat(int SampleRate, int Channels, int BitsPerSample);

    private static (WavFormat Fmt, byte[] Data) ReadWav(string path)
    {
        var bytes = File.ReadAllBytes(path);
        // RIFF/WAVE chunk walk
        if (bytes.Length < 12) throw new InvalidDataException("WAV too small.");
        int pos = 12; // skip RIFF....WAVE
        WavFormat fmt = default;
        byte[]? data = null;
        while (pos + 8 <= bytes.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
            int size = BitConverter.ToInt32(bytes, pos + 4);
            int body = pos + 8;
            if (id == "fmt ")
            {
                int channels = BitConverter.ToInt16(bytes, body + 2);
                int sampleRate = BitConverter.ToInt32(bytes, body + 4);
                int bits = BitConverter.ToInt16(bytes, body + 14);
                fmt = new WavFormat(sampleRate, channels, bits);
            }
            else if (id == "data")
            {
                int len = Math.Min(size, bytes.Length - body);
                data = new byte[len];
                Array.Copy(bytes, body, data, 0, len);
            }
            pos = body + size + (size % 2); // chunks are word-aligned
        }
        if (fmt.SampleRate == 0) throw new InvalidDataException("WAV fmt chunk not found.");
        return (fmt, data ?? Array.Empty<byte>());
    }

    private static void WriteWav(string path, WavFormat fmt, byte[] data)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        int byteRate = fmt.SampleRate * fmt.Channels * (fmt.BitsPerSample / 8);
        short blockAlign = (short)(fmt.Channels * (fmt.BitsPerSample / 8));
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + data.Length);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1); // PCM
        w.Write((short)fmt.Channels);
        w.Write(fmt.SampleRate);
        w.Write(byteRate);
        w.Write(blockAlign);
        w.Write((short)fmt.BitsPerSample);
        w.Write("data"u8.ToArray());
        w.Write(data.Length);
        w.Write(data);
    }

    // ── shareable mixdown ──────────────────────────────────────────────────────

    /// <summary>
    /// Build the human-shareable MP3 mixdown of the present tracks (port of
    /// <c>audio_prep.make_share_mp3</c>). One track -&gt; resample to 16 kHz mono;
    /// several -&gt; <c>amix</c> with <c>normalize=1</c>, each track delayed by its t0
    /// offset (sidecar <c>{tag}.json</c>) so they play in sync. Encoded with libmp3lame
    /// -q:a 5. This has NO impact on transcription (done per track). Never throws: if
    /// ffmpeg is absent or fails, a warning is logged and null is returned so the
    /// pipeline continues.
    /// </summary>
    public static string? MakeShareMp3(IReadOnlyList<string> presentWavs, string outMp3, IProgress<string>? log = null) =>
        MakeShareMp3(presentWavs, outMp3, log, CancellationToken.None);

    /// <summary>Same as the main overload; only a cancellation of <paramref name="ct"/> throws.</summary>
    public static string? MakeShareMp3(IReadOnlyList<string> presentWavs, string outMp3, IProgress<string>? log,
        CancellationToken ct)
    {
        if (presentWavs is null || presentWavs.Count == 0) return null;
        var args = new List<string> { "-y" };
        foreach (var p in presentWavs) { args.Add("-i"); args.Add(p); }

        try
        {
            var delays = ShareMixDelaysMs(presentWavs.Select(ReadSidecarT0).ToList());
            string fc = BuildShareMixFilter(delays, SoxrAvailable());
            args.AddRange(new[] { "-filter_complex", fc, "-map", "[m]", "-ac", "1", "-ar", "16000", "-c:a", "libmp3lame", "-q:a", "5", outMp3 });

            var (code, err) = Ffmpeg.Run(args, Ffmpeg.TimeoutFor(presentWavs), ct);
            if (code != 0)
            {
                log?.Report($"  [share-mp3] ffmpeg failed (code {code}); skipped: {Tail(err)}");
                return null;
            }
            return outMp3;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // ffmpeg missing (Win32Exception), timeout or any other failure: skip, never crash.
            log?.Report($"  [share-mp3] ffmpeg unavailable; shareable MP3 skipped ({e.GetType().Name}).");
            return null;
        }
    }

    /// <summary>Per-track delay (ms) aligning every track on the earliest t0: the
    /// later-starting track is delayed by its t0 difference. Unknown t0 → 0.</summary>
    internal static IReadOnlyList<double> ShareMixDelaysMs(IReadOnlyList<double?> t0Epochs)
    {
        var known = t0Epochs.Where(t => t is { } v && double.IsFinite(v)).Select(t => t!.Value).ToList();
        if (known.Count == 0) return t0Epochs.Select(_ => 0.0).ToList();
        double first = known.Min();
        return t0Epochs.Select(t => t is { } v && double.IsFinite(v) ? Math.Max(0.0, (v - first) * 1000.0) : 0.0).ToList();
    }

    /// <summary>
    /// The mixdown filter_complex, one input per delay. Each input is resampled to 16 kHz
    /// mono, delayed by its offset (<c>adelay</c>, whole ms, invariant culture), then all
    /// are <c>amix</c>-ed into <c>[m]</c>. A single input is only resampled.
    /// </summary>
    internal static string BuildShareMixFilter(IReadOnlyList<double> delaysMs, bool soxr)
    {
        string rs = soxr ? "resampler=soxr:" : "";
        if (delaysMs.Count <= 1)
            return $"[0:a]aresample={rs}osr=16000,aformat=channel_layouts=mono[m]";

        var parts = new List<string>();
        for (int i = 0; i < delaysMs.Count; i++)
        {
            long ms = (long)Math.Round(delaysMs[i]);
            string delay = ms >= 1 ? ",adelay=" + ms.ToString(CultureInfo.InvariantCulture) : "";
            parts.Add($"[{i}:a]aresample={rs}osr=16000,aformat=channel_layouts=mono{delay}[a{i}]");
        }
        var mixIn = string.Concat(Enumerable.Range(0, delaysMs.Count).Select(i => $"[a{i}]"));
        return string.Join(";", parts)
               + $";{mixIn}amix=inputs={delaysMs.Count.ToString(CultureInfo.InvariantCulture)}:duration=longest:normalize=1[m]";
    }

    /// <summary>t0 (wall epoch) of a track from its sidecar (<c>mic.wav</c>/<c>mic.mp3</c> →
    /// <c>mic.json</c>), the same anchor the transcription aligns on; null if unknown.</summary>
    internal static double? ReadSidecarT0(string trackPath)
    {
        try
        {
            var json = Path.Combine(Path.GetDirectoryName(trackPath) ?? "", Path.GetFileNameWithoutExtension(trackPath) + ".json");
            if (!File.Exists(json)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(json));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("got_audio", out var ga) && ga.ValueKind == JsonValueKind.False) return null;
            return root.TryGetProperty("t0_wall_epoch", out var t0) && t0.ValueKind == JsonValueKind.Number
                ? t0.GetDouble()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static double? TryParse(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static string Tail(string s) => s.Length <= 2000 ? s : s[^2000..];
}

/// <summary>
/// The single ffmpeg runner of Echo.Ai (AudioPrep + AudioArchive). Bounded: a hung
/// ffmpeg (corrupt input, stuck pipe, AV hook) is killed with its whole process tree
/// after a delay proportional to the input duration, or on cancellation. stdout and
/// stderr are drained asynchronously (no pipe deadlock) and stdin is closed (ffmpeg
/// never waits for an interactive answer).
/// </summary>
internal static class Ffmpeg
{
    internal static readonly TimeSpan MinTimeout = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan MaxTimeout = TimeSpan.FromHours(4);

    /// <summary>60 s + half the input duration (ffmpeg runs these jobs at 20-200× real
    /// time; 2× real time is a hang), clamped to [2 min, 4 h].</summary>
    internal static TimeSpan TimeoutFor(double inputSeconds)
    {
        if (double.IsNaN(inputSeconds) || inputSeconds < 0) inputSeconds = 0;
        double s = 60.0 + 0.5 * inputSeconds;
        s = Math.Clamp(s, MinTimeout.TotalSeconds, MaxTimeout.TotalSeconds);
        return TimeSpan.FromSeconds(s);
    }

    internal static TimeSpan TimeoutFor(string input) => TimeoutFor(EstimateSeconds(input));

    internal static TimeSpan TimeoutFor(IEnumerable<string> inputs) =>
        TimeoutFor(inputs.Select(EstimateSeconds).DefaultIfEmpty(0.0).Max());

    /// <summary>Input duration: from the WAV header when possible, else from the file size
    /// at a conservative 32 kbps (over-estimates any normal mp3/m4a/video audio track, so
    /// the timeout stays generous). 0 when the file cannot be read.</summary>
    internal static double EstimateSeconds(string path)
    {
        try
        {
            if (path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            {
                try { return AudioArchive.WavHeaderDuration(path); }
                catch { /* not a plain PCM WAV: size estimate */ }
            }
            return new FileInfo(path).Length / 4000.0;
        }
        catch
        {
            return 0.0;
        }
    }

    internal static (int Code, string Stderr) Run(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var r = RunFull(args, timeout, ct);
        return (r.Code, r.Stderr);
    }

    internal static (int Code, string Stdout, string Stderr) RunFull(IReadOnlyList<string> args, TimeSpan timeout,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo
        {
            FileName = AudioPrep.ResolveFfmpeg(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        try { p.StandardInput.Close(); } catch { /* ignore */ }
        var errTask = p.StandardError.ReadToEndAsync();
        var outTask = p.StandardOutput.ReadToEndAsync();

        bool exited;
        using (ct.Register(() => Kill(p)))
            exited = p.WaitForExit((int)Math.Min(int.MaxValue, Math.Max(1.0, timeout.TotalMilliseconds)));
        if (!exited)
        {
            Kill(p);
            try { p.WaitForExit(5000); } catch { /* ignore */ }
            throw new TimeoutException(
                $"ffmpeg n'a pas termine en {timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s, processus arrete.");
        }
        ct.ThrowIfCancellationRequested();

        string err = Drain(errTask), outp = Drain(outTask);
        return (p.ExitCode, outp, err);
    }

    private static string Drain(Task<string> t)
    {
        try { return t.Wait(TimeSpan.FromSeconds(5)) ? t.Result : ""; }
        catch { return ""; }
    }

    private static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }
}
