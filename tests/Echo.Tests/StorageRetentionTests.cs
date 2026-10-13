using Echo.Core.Storage;

namespace Echo.Tests;

/// <summary>
/// Disk housekeeping over failed sessions (v5.4). The sweeper used to skip every session
/// without a transcript, which is exactly where the weight sits: on 2026-09-18, 15 of the
/// 20 GB under %LOCALAPPDATA%\Echo\recordings were four meetings whose transcription had
/// failed, one of them a 4-hour orphan recording. These tests pin the new pass and, above
/// all, what it must NEVER touch: the transcripts, the minutes, the listening mixdown, and
/// any session still being recorded, processed or queued for a retry.
/// </summary>
public sealed class StorageRetentionTests : IDisposable
{
    private readonly string _root;

    public StorageRetentionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "echo-rec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    /// <param name="day">folder date prefix, "yyyyMMdd"</param>
    private string Session(string day, string name, bool transcript, string? status = null, long trackBytes = 5_000_000)
    {
        var dir = Path.Combine(_root, $"{day} - 10h00 - {name}");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "mic.wav"), new byte[trackBytes]);
        File.WriteAllBytes(Path.Combine(dir, "system.wav"), new byte[trackBytes]);
        File.WriteAllBytes(Path.Combine(dir, "mic_16k.wav"), new byte[1_000_000]);
        File.WriteAllBytes(Path.Combine(dir, $"{name} - Audio.mp3"), new byte[300_000]);
        if (transcript) File.WriteAllText(Path.Combine(dir, $"{name} - Transcript.txt"), "bonjour");
        if (status is not null) File.WriteAllText(Path.Combine(dir, ".echo_status"), status);
        return dir;
    }

    private static readonly DateTime Now = new(2026, 9, 18);

    /// <summary>A sentinel written by THIS process just now (what a live pipeline writes).</summary>
    private static string Live(string state) => $"{state}\n{Environment.ProcessId}\n{DateTimeOffset.Now.ToUnixTimeSeconds()}\n";

    [Fact]
    public void FailedSession_OlderThanItsDelay_LosesItsTracksButKeepsTheMixdown()
    {
        var dir = Session("20260715", "Revue fournisseurs", transcript: false, status: "error\n1\n0\n");

        var r = RetentionSweeper.Sweep(_root, retentionDays: 1, failedRetentionDays: 30, Now);

        Assert.True(r.BytesFreed > 9_000_000);
        Assert.False(File.Exists(Path.Combine(dir, "mic.wav")));
        Assert.False(File.Exists(Path.Combine(dir, "system.wav")));
        Assert.True(File.Exists(Path.Combine(dir, "Revue fournisseurs - Audio.mp3")), "the listening MP3 is kept");
    }

    [Fact]
    public void FailedSession_WithinItsDelay_IsLeftAlone()
    {
        var dir = Session("20260916", "Atelier achats", transcript: false, status: "error\n1\n0\n");

        RetentionSweeper.Sweep(_root, retentionDays: 1, failedRetentionDays: 30, Now);

        Assert.True(File.Exists(Path.Combine(dir, "mic.wav")), "a recent failure may still be retried");
    }

    [Fact]
    public void FailedSession_IsNeverSweptWhenTheDelayIsZero()
    {
        var dir = Session("20260101", "Vieux", transcript: false, status: "error\n1\n0\n");

        RetentionSweeper.Sweep(_root, retentionDays: 1, failedRetentionDays: 0, Now);

        Assert.True(File.Exists(Path.Combine(dir, "mic.wav")));
    }

    [Theory]
    [InlineData("running")]
    [InlineData("waiting")]
    public void LiveSession_IsNeverTouched(string status)
    {
        var dir = Session("20260101", "EnCours", transcript: false, status: Live(status));

        RetentionSweeper.Sweep(_root, retentionDays: 1, failedRetentionDays: 1, Now);

        Assert.True(File.Exists(Path.Combine(dir, "mic.wav")));
        Assert.True(File.Exists(Path.Combine(dir, "mic_16k.wav")));
    }

    [Fact]
    public void NeverAttemptedSession_IsNotAFailure_AndKeepsItsOnlyCopy()
    {
        // auto_transcribe off: no transcript, no sentinel. Not a failure: pass 3 must not apply.
        var dir = Session("20260101", "JamaisTente", transcript: false);

        RetentionSweeper.Sweep(_root, retentionDays: 1, failedRetentionDays: 1, Now);

        Assert.True(File.Exists(Path.Combine(dir, "mic.wav")));
        Assert.True(File.Exists(Path.Combine(dir, "system.wav")));
    }

    [Fact]
    public void RecoveredOrphan_CountsAsAFailure()
    {
        var dir = Session("20260101", "Orphelin", transcript: false);
        File.WriteAllText(Path.Combine(dir, "session.json"), "{\"duration_s\": 600, \"recovered\": true}");

        RetentionSweeper.Sweep(_root, retentionDays: 1, failedRetentionDays: 30, Now);

        Assert.False(File.Exists(Path.Combine(dir, "mic.wav")));
    }

    [Theory]
    [InlineData("running\n999999\n1700000000\n")]   // pipeline of a dead process
    [InlineData("running\n")]                         // no pid at all
    public void StaleRunningSentinel_IsNotLive_AndTheSessionIsCleaned(string status)
    {
        // A crash mid-pipeline used to freeze the session as "live" forever.
        var dir = Session("20260101", "Fige", transcript: true, status: status);

        RetentionSweeper.Sweep(_root, retentionDays: 1, failedRetentionDays: 30, Now);

        Assert.False(File.Exists(Path.Combine(dir, "mic.wav")));
        Assert.False(File.Exists(Path.Combine(dir, "mic_16k.wav")));
    }

    [Fact]
    public void ChunkPieces_ArePurgedLikeTheOtherIntermediates()
    {
        var dir = Session("20260918", "Morceaux", transcript: false);
        foreach (var f in new[] { "mic_16k_trim_c0.wav", "mic_16k_trim_c1.mp3", "system_16k_c12.wav" })
            File.WriteAllBytes(Path.Combine(dir, f), new byte[100_000]);
        File.WriteAllBytes(Path.Combine(dir, "notes_c1.wav"), new byte[10]);    // not ours: untouched

        var r = RetentionSweeper.Sweep(_root, retentionDays: 1, failedRetentionDays: 30, Now);

        Assert.False(File.Exists(Path.Combine(dir, "mic_16k_trim_c0.wav")));
        Assert.False(File.Exists(Path.Combine(dir, "mic_16k_trim_c1.mp3")));
        Assert.False(File.Exists(Path.Combine(dir, "system_16k_c12.wav")));
        Assert.True(File.Exists(Path.Combine(dir, "notes_c1.wav")));
        Assert.True(File.Exists(Path.Combine(dir, "mic.wav")));
        Assert.True(r.BytesFreed >= 300_000);
    }

    [Fact]
    public void EverySweep_LogsItsSummary_EvenWhenNothingIsFreed()
    {
        var lines = new List<string>();
        RetentionSweeper.Sweep(_root, retentionDays: 0, failedRetentionDays: 0, Now, lines.Add);
        Assert.Contains(lines, l => l.Contains("0 session(s)") && l.Contains("Mo"));
    }

    [Fact]
    public void ErroredSession_IsSweptOnceOldEnough_UnlikeALiveOne()
    {
        var dir = Session("20260101", "Echec", transcript: false, status: "error");

        RetentionSweeper.Sweep(_root, retentionDays: 1, failedRetentionDays: 30, Now);

        Assert.False(File.Exists(Path.Combine(dir, "mic.wav")));
    }

    [Fact]
    public void Intermediates_ArePurgedEvenWithoutATranscript()
    {
        // The _16k prep files are regenerable; a failed run strands hundreds of MB of them,
        // and the old sweeper bailed out before reaching them on untranscribed sessions.
        var dir = Session("20260918", "DuJour", transcript: false);

        RetentionSweeper.Sweep(_root, retentionDays: 1, failedRetentionDays: 30, Now);

        Assert.False(File.Exists(Path.Combine(dir, "mic_16k.wav")));
        Assert.True(File.Exists(Path.Combine(dir, "mic.wav")), "the source track itself stays");
    }

    [Fact]
    public void TranscribedSession_KeepsTranscriptAndMixdown()
    {
        var dir = Session("20260715", "Lancement Atlas", transcript: true);

        RetentionSweeper.Sweep(_root, retentionDays: 1, failedRetentionDays: 30, Now);

        Assert.False(File.Exists(Path.Combine(dir, "mic.wav")));
        Assert.True(File.Exists(Path.Combine(dir, "Lancement Atlas - Transcript.txt")));
        Assert.True(File.Exists(Path.Combine(dir, "Lancement Atlas - Audio.mp3")));
    }

    [Fact]
    public void DryRun_MeasuresWithoutDeleting()
    {
        var dir = Session("20260715", "Mesure", transcript: true);

        var dry = RetentionSweeper.Sweep(_root, 1, 30, Now, log: null, dryRun: true);
        Assert.True(dry.BytesFreed > 9_000_000);
        Assert.True(File.Exists(Path.Combine(dir, "mic.wav")), "a dry run must not delete anything");

        var real = RetentionSweeper.Sweep(_root, 1, 30, Now);
        Assert.Equal(dry.BytesFreed, real.BytesFreed);          // the estimate is the truth
    }

    [Fact]
    public void Report_SeparatesRawTracksFromTheKeptMixdown_AndFlagsUntranscribed()
    {
        Session("20260715", "AvecTr", transcript: true);
        Session("20260916", "SansTr", transcript: false);

        var rep = StorageReport.Build(_root);

        Assert.Equal(2, rep.SessionCount);
        Assert.Equal(1, rep.UntranscribedCount);
        Assert.Equal(22_000_000, rep.RawTrackBytes);            // 2 × (2 tracks + one _16k)
        Assert.Equal(600_000, rep.MixdownBytes);
        Assert.True(rep.Heaviest.Count == 2 && rep.Heaviest[0].Bytes >= rep.Heaviest[1].Bytes);
    }
}
