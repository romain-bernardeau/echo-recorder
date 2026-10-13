using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Echo.Core.Storage;

/// <summary>
/// Startup repair of sessions whose capture never reached Stop (app killed, or the
/// 2026-09-14/16 "orphaned capture" bug where a second start replaced the running
/// session). Such a folder has mic.wav/system.wav but no session.json, a WAV header
/// whose RIFF/data sizes were never finalised (NAudio writes them on Dispose only) and
/// a sidecar still saying got_audio=false / t0=null, so the transcriber ignored the track.
/// Recovery: fix the WAV headers from the file length, patch the sidecars (duration,
/// frames, estimated t0) and write a session.json so the session lists with its
/// duration and can be transcribed on demand. Nothing is transcribed automatically.
/// </summary>
public static class OrphanRecovery
{
    public sealed record WavHeader(int SampleRate, int Channels, int BitsPerSample, long DataStart, long DataSize, long FileLength)
    {
        public int BytesPerFrame => Math.Max(1, Channels * Math.Max(1, BitsPerSample / 8));
        /// <summary>Data bytes actually present on disk after the header (the truth for a killed writer).</summary>
        public long ActualDataSize => Math.Max(0, FileLength - DataStart);
        public bool SizesStale => DataSize <= 0 || DataStart + DataSize > FileLength;
        public double ActualDurationSeconds => SampleRate > 0 ? ActualDataSize / (double)(BytesPerFrame * SampleRate) : 0.0;
    }

    /// <summary>Parse the RIFF/WAVE header. Returns null when the file is not a WAV we understand.</summary>
    public static WavHeader? ReadHeader(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return ReadHeader(fs);
        }
        catch { return null; }
    }

    public static WavHeader? ReadHeader(Stream fs)
    {
        long len = fs.Length;
        if (len < 44) return null;
        var br = new BinaryReader(fs, Encoding.ASCII, leaveOpen: true);
        fs.Position = 0;
        if (new string(br.ReadChars(4)) != "RIFF") return null;
        br.ReadUInt32(); // riff size (possibly stale)
        if (new string(br.ReadChars(4)) != "WAVE") return null;

        int rate = 0, channels = 0, bits = 0;
        long dataStart = -1, dataSize = 0;
        while (fs.Position + 8 <= len)
        {
            string id = new string(br.ReadChars(4));
            uint size = br.ReadUInt32();
            long body = fs.Position;
            if (id == "fmt ")
            {
                br.ReadUInt16();                 // format tag
                channels = br.ReadUInt16();
                rate = (int)br.ReadUInt32();
                br.ReadUInt32();                 // byte rate
                br.ReadUInt16();                 // block align
                bits = br.ReadUInt16();
            }
            else if (id == "data")
            {
                dataStart = body;
                dataSize = size;
                break;                            // data is the last chunk NAudio writes
            }
            long next = body + size + (size % 2);
            if (next <= body) break;              // stale size 0 on a non-data chunk: stop
            fs.Position = next;
        }
        if (rate <= 0 || channels <= 0 || dataStart < 0) return null;
        return new WavHeader(rate, channels, bits, dataStart, dataSize, len);
    }

    /// <summary>Rewrite RIFF and data chunk sizes from the file length when they are stale.
    /// Returns true when the file was modified.</summary>
    public static bool RepairHeader(string path)
    {
        var h = ReadHeader(path);
        if (h is null || !h.SizesStale) return false;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var bw = new BinaryWriter(fs);
        fs.Position = 4;
        bw.Write((uint)Math.Min(uint.MaxValue, fs.Length - 8));
        fs.Position = h.DataStart - 4;
        bw.Write((uint)Math.Min(uint.MaxValue, h.ActualDataSize));
        bw.Flush();
        return true;
    }

    /// <summary>True when another process still writes the file (a live capture) — never touch it.</summary>
    private static bool IsLocked(string path)
    {
        try { using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (IOException) { return true; }
        catch { return false; }
    }

    public sealed record Result(int Recovered, List<string> Dirs);

    /// <summary>Scan <paramref name="recordingsDir"/> and recover every orphaned session.
    /// <paramref name="skipDir"/> is the session currently being recorded (never touched).</summary>
    public static Result Run(string recordingsDir, string? skipDir, Action<string> log)
    {
        var done = new List<string>();
        if (!Directory.Exists(recordingsDir)) return new Result(0, done);
        foreach (var dir in Directory.EnumerateDirectories(recordingsDir))
        {
            try
            {
                if (skipDir is not null && string.Equals(Path.GetFullPath(dir), Path.GetFullPath(skipDir), StringComparison.OrdinalIgnoreCase)) continue;
                if (SessionLayout.Exists(dir, SessionLayout.Session)) continue;
                var wavs = SessionLayout.Tags.Where(t => SessionLayout.Exists(dir, t + ".wav")).ToList();
                if (wavs.Count == 0) continue;
                if (wavs.Any(t => IsLocked(SessionLayout.Find(dir, t + ".wav")))) continue;   // live capture elsewhere
                if (RecoverSession(dir, wavs, log)) done.Add(dir);
            }
            catch (Exception ex) { log($"[recovery] {Path.GetFileName(dir)}: {ex.Message}"); }
        }
        return new Result(done.Count, done);
    }

    private static bool RecoverSession(string dir, List<string> tags, Action<string> log)
    {
        double wall0 = 0; long perf0 = 0;
        var tracks = new JsonObject();
        double maxDur = 0;
        int usable = 0;
        foreach (var tag in tags)
        {
            var wav = SessionLayout.Find(dir, tag + ".wav");
            bool repaired = RepairHeader(wav);
            var h = ReadHeader(wav);
            double dur = h?.ActualDurationSeconds ?? 0.0;
            long frames = h is null ? 0 : h.ActualDataSize / h.BytesPerFrame;
            if (dur > 0.5) usable++;
            maxDur = Math.Max(maxDur, dur);

            // Sidecar: make the track visible to the transcriber (got_audio + t0 anchor).
            var jp = SessionLayout.Find(dir, tag + ".json");
            JsonObject sc;
            try { sc = File.Exists(jp) ? (JsonNode.Parse(File.ReadAllText(jp))?.AsObject() ?? new()) : new(); }
            catch { sc = new JsonObject(); }
            wall0 = wall0 == 0 ? (sc["session_wall0_epoch"]?.GetValue<double>() ?? 0) : wall0;
            perf0 = perf0 == 0 ? (sc["session_perf0_ns"]?.GetValue<long>() ?? 0) : perf0;
            if (frames > 0)
            {
                sc["got_audio"] = true;
                if (sc["t0_wall_epoch"] is null || sc["t0_wall_epoch"]!.GetValueKind() == JsonValueKind.Null)
                {
                    // Estimated anchor: the mic normally stamps ~0.1-1.3 s after session start.
                    sc["t0_wall_epoch"] = wall0 > 0 ? wall0 + 0.5 : (double?)null;
                    sc["t0_perf_ns"] = perf0 > 0 ? perf0 + 500_000_000L : (long?)null;
                    sc["t0_estimated"] = true;
                }
                sc["frames_written"] = frames;
                sc["duration_s"] = Math.Round(dur, 3);
            }
            sc["finalized"] = true;
            sc["recovered"] = true;
            WriteAtomic(SessionLayout.ForWrite(dir, tag + ".json"), sc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            tracks[tag] = new JsonObject
            {
                ["wav"] = wav, ["duration_s"] = Math.Round(dur, 3), ["error"] = null, ["recovered"] = true,
            };
            log($"[recovery] {Path.GetFileName(dir)}/{tag}.wav: {dur / 60:0.0} min{(repaired ? ", header repaired" : "")}");
        }
        if (usable == 0) return false;

        bool physical = false;
        try
        {
            var mj = JsonNode.Parse(File.ReadAllText(SessionLayout.Find(dir, SessionLayout.Meeting)))?.AsObject();
            physical = mj?["physical"]?.GetValue<bool>() ?? false;
        }
        catch { }

        var o = new JsonObject
        {
            ["out_dir"] = dir,
            ["physical"] = physical,
            ["ended_wall_epoch"] = wall0 > 0 ? (long)(wall0 + maxDur) : DateTimeOffset.Now.ToUnixTimeSeconds(),
            ["duration_s"] = Math.Round(maxDur, 3),
            ["tracks"] = tracks,
            ["recovered"] = true,
        };
        WriteAtomic(SessionLayout.ForWrite(dir, SessionLayout.Session), o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        // A stale "running"/"error" sentinel from the dead pipeline must not stick.
        SessionLayout.Delete(dir, SessionStatus.FileName);
        return true;
    }

    private static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }
}
