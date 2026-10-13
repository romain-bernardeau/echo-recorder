using System.Text;
using System.Text.Json;
using Echo.Core.Storage;

namespace Echo.Tests;

/// <summary>
/// Pins the 2026-09-16 fixes around the post-meeting pipeline: the deferred-retry queue
/// (a 429 parks the session instead of failing it) and the recovery of orphaned captures
/// (WAV headers never finalised + sidecars still saying got_audio=false).
/// </summary>
public sealed class SessionRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "echo-tests-" + Guid.NewGuid().ToString("N"));

    public SessionRecoveryTests() { Directory.CreateDirectory(_root); }
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string NewSessionDir(string name)
    {
        var d = Path.Combine(_root, name);
        Directory.CreateDirectory(d);
        return d;
    }

    // ── ProcessingQueue ─────────────────────────────────────────────────────────

    [Fact]
    public void Queue_EscalatesDelays_ThenGivesUp()
    {
        var q = new ProcessingQueue(Path.Combine(_root, "pending.json"), _ => { });
        var dir = NewSessionDir("s1");
        var t0 = new DateTimeOffset(2026, 9, 16, 10, 0, 0, TimeSpan.Zero);

        Assert.Equal(10, q.Enqueue(dir, minutes: false, summarize: true, "429", t0));
        Assert.Equal(30, q.Enqueue(dir, minutes: true, summarize: false, "429", t0));
        Assert.Equal(60, q.Enqueue(dir, minutes: false, summarize: false, "429", t0));
        var it = Assert.Single(q.Snapshot());
        Assert.Equal(3, it.Attempts);
        Assert.True(it.Minutes && it.Summarize);   // flags accumulate across attempts
        Assert.Equal(t0.AddMinutes(60).ToUnixTimeSeconds(), it.NextAtEpoch);

        Assert.Null(q.Enqueue(dir, false, false, "429", t0));   // budget exhausted
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public void Queue_Due_And_Clear_And_Persistence()
    {
        var file = Path.Combine(_root, "pending.json");
        var q = new ProcessingQueue(file, _ => { });
        var a = NewSessionDir("a");
        var b = NewSessionDir("b");
        var t0 = DateTimeOffset.Now;
        q.Enqueue(a, true, true, "429", t0);
        q.Enqueue(b, true, true, "503", t0);

        Assert.Empty(q.Due(t0.AddMinutes(5)));
        Assert.Equal(2, q.Due(t0.AddMinutes(11)).Count);

        q.Clear(a);
        Assert.False(q.Contains(a));
        Assert.True(q.Contains(b));

        // Reload from disk in a fresh instance (survives an app restart).
        var q2 = new ProcessingQueue(file, _ => { });
        var only = Assert.Single(q2.Snapshot());
        Assert.Equal(Path.GetFullPath(b), Path.GetFullPath(only.Dir));
        Assert.Equal(1, only.Attempts);
    }

    [Fact]
    public void Queue_DropsEntriesWhoseFolderDisappeared()
    {
        var file = Path.Combine(_root, "pending.json");
        var q = new ProcessingQueue(file, _ => { });
        var gone = NewSessionDir("gone");
        q.Enqueue(gone, true, true, "429");
        Directory.Delete(gone, true);
        var q2 = new ProcessingQueue(file, _ => { });
        Assert.Equal(0, q2.Count);
    }

    // ── OrphanRecovery ──────────────────────────────────────────────────────────

    /// <summary>A WAV exactly as NAudio's WaveFileWriter leaves it when the process dies:
    /// RIFF and data sizes still 0, PCM bytes appended after the header.</summary>
    private static void WriteUnfinalizedWav(string path, int rate, int channels, int seconds)
    {
        using var fs = new FileStream(path, FileMode.Create);
        var bw = new BinaryWriter(fs);
        bw.Write(Encoding.ASCII.GetBytes("RIFF")); bw.Write(0u); bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt ")); bw.Write(16u);
        bw.Write((ushort)1); bw.Write((ushort)channels); bw.Write((uint)rate);
        bw.Write((uint)(rate * channels * 2)); bw.Write((ushort)(channels * 2)); bw.Write((ushort)16);
        bw.Write(Encoding.ASCII.GetBytes("data")); bw.Write(0u);
        bw.Write(new byte[rate * channels * 2 * seconds]);
    }

    [Fact]
    public void ReadHeader_DetectsStaleSizes_AndRepairFixesThem()
    {
        var wav = Path.Combine(_root, "x.wav");
        WriteUnfinalizedWav(wav, 48000, 2, 3);
        var h = OrphanRecovery.ReadHeader(wav)!;
        Assert.True(h.SizesStale);
        Assert.Equal(48000, h.SampleRate);
        Assert.Equal(2, h.Channels);
        Assert.Equal(3.0, h.ActualDurationSeconds, 3);

        Assert.True(OrphanRecovery.RepairHeader(wav));
        var h2 = OrphanRecovery.ReadHeader(wav)!;
        Assert.False(h2.SizesStale);
        Assert.Equal(h.ActualDataSize, h2.DataSize);
        Assert.False(OrphanRecovery.RepairHeader(wav));   // idempotent
    }

    [Fact]
    public void Run_RecoversOrphan_SkipsFinalizedAndLiveSessions()
    {
        // Orphan: WAVs + sidecar with got_audio=false, no session.json.
        var orphan = NewSessionDir("20260916 - 09h51 - Orphan");
        WriteUnfinalizedWav(Path.Combine(orphan, "mic.wav"), 48000, 2, 2);
        WriteUnfinalizedWav(Path.Combine(orphan, "system.wav"), 48000, 2, 4);
        File.WriteAllText(Path.Combine(orphan, "mic.json"),
            """{"label":"mic","got_audio":false,"t0_wall_epoch":null,"t0_perf_ns":null,"session_wall0_epoch":1789545077.763,"session_perf0_ns":4402364775700,"frames_written":0,"duration_s":0,"finalized":false}""");
        File.WriteAllText(Path.Combine(orphan, "meeting.json"), """{"name":"Orphan","physical":false}""");
        File.WriteAllText(Path.Combine(orphan, ".echo_status"), "running\n1\n0\n");

        // Finalized: has session.json → untouched.
        var done = NewSessionDir("20260916 - 09h54 - Done");
        WriteUnfinalizedWav(Path.Combine(done, "mic.wav"), 48000, 2, 1);
        File.WriteAllText(Path.Combine(done, "session.json"), "{}");

        // Live: the current recording → skipped by path.
        var live = NewSessionDir("20260916 - 11h00 - Live");
        WriteUnfinalizedWav(Path.Combine(live, "mic.wav"), 48000, 2, 1);

        var r = OrphanRecovery.Run(_root, skipDir: live, _ => { });
        Assert.Equal(1, r.Recovered);
        Assert.Equal(Path.GetFullPath(orphan), Path.GetFullPath(r.Dirs[0]));

        var session = JsonDocument.Parse(File.ReadAllText(SessionLayout.Find(orphan, "session.json"))).RootElement;
        Assert.Equal(4.0, session.GetProperty("duration_s").GetDouble(), 2);
        Assert.True(session.GetProperty("recovered").GetBoolean());
        Assert.Equal(2.0, session.GetProperty("tracks").GetProperty("mic").GetProperty("duration_s").GetDouble(), 2);
        Assert.False(File.Exists(Path.Combine(orphan, ".echo_status")));

        var mic = JsonDocument.Parse(File.ReadAllText(SessionLayout.Find(orphan, "mic.json"))).RootElement;
        Assert.True(mic.GetProperty("got_audio").GetBoolean());
        Assert.Equal(1789545078.263, mic.GetProperty("t0_wall_epoch").GetDouble(), 3);   // wall0 + 0.5
        Assert.True(mic.GetProperty("t0_estimated").GetBoolean());
        Assert.Equal(48000 * 2, mic.GetProperty("frames_written").GetInt64());
        Assert.False(OrphanRecovery.ReadHeader(Path.Combine(orphan, "mic.wav"))!.SizesStale);

        Assert.False(File.Exists(Path.Combine(live, "session.json")));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(done, "session.json")));
    }
}
