using System.Text;
using Echo.Ai;
using Echo.Core.Storage;

namespace Echo.Tests;

/// <summary>
/// Disk-housekeeping regression tests (A+B+C of 2026-07-13): retention sweep of
/// per-track audio, retroactive purge of regenerable _16k intermediates, and the
/// WAV→MP3 archive round-trip (duration-verified before the WAV is deleted).
/// </summary>
public class HousekeepingTests
{
    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), $"echo_hk_{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Put(string dir, string name, int bytes = 1024)
        => File.WriteAllBytes(Path.Combine(dir, name), new byte[bytes]);

    private static bool Has(string dir, string name) => File.Exists(Path.Combine(dir, name));

    // ── RetentionSweeper ─────────────────────────────────────────────────────────

    [Fact]
    public void Sweep_PurgesIntermediates_OnlyWhenSourcePresent_AndDropsZeroByteWavs()
    {
        var root = NewDir();
        try
        {
            var s = Path.Combine(root, "20260713 - 10h00 - A");
            Directory.CreateDirectory(s);
            Put(s, "A - Transcript.txt");
            Put(s, "mic.wav", 5000);            // source present → intermediates purgeable
            Put(s, "mic_16k.wav", 4000);
            Put(s, "mic_16k_trim.wav", 3000);
            Put(s, "mic_16k_trim.mp3", 2000);   // upload-mp3 leftover (crash before eager delete)
            Put(s, "system_16k.wav", 4000);     // NO system source → kept (only audio left)
            Put(s, "system.wav", 0);            // dead capture → dropped

            var r = RetentionSweeper.Sweep(root, retentionDays: 0, DateTime.Now);

            Assert.True(Has(s, "mic.wav"));
            Assert.False(Has(s, "mic_16k.wav"));
            Assert.False(Has(s, "mic_16k_trim.wav"));
            Assert.False(Has(s, "mic_16k_trim.mp3"));
            Assert.True(Has(s, "system_16k.wav"));
            Assert.False(Has(s, "system.wav"));
            Assert.True(r.BytesFreed >= 7000);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Sweep_Retention_PurgesOldAudio_KeepsMixdownTranscriptAndRecent()
    {
        var root = NewDir();
        try
        {
            var old = Path.Combine(root, "20260501 - 10h00 - Vieux");
            Directory.CreateDirectory(old);
            Put(old, "Vieux - Transcript.txt");
            Put(old, "Vieux - Audio.mp3", 9000);   // listening mixdown: never touched
            Put(old, "mic.mp3", 5000);
            Put(old, "system.wav", 5000);

            var recent = Path.Combine(root, "20260713 - 10h00 - Recent");
            Directory.CreateDirectory(recent);
            Put(recent, "Recent - Transcript.txt");
            Put(recent, "mic.wav", 5000);

            var untranscribed = Path.Combine(root, "20260401 - 10h00 - Brut");
            Directory.CreateDirectory(untranscribed);
            Put(untranscribed, "mic.wav", 5000);    // only copy → retention must not touch it

            var processing = Path.Combine(root, "20260401 - 11h00 - EnCours");
            Directory.CreateDirectory(processing);
            Put(processing, "EnCours - Transcript.txt");
            Put(processing, "mic.wav", 5000);
            // A sentinel of THIS process, written just now: a pipeline really at work.
            File.WriteAllText(Path.Combine(processing, ".echo_status"),
                $"running\n{Environment.ProcessId}\n{DateTimeOffset.Now.ToUnixTimeSeconds()}\n", new UTF8Encoding(false));

            var now = new DateTime(2026, 7, 14);
            RetentionSweeper.Sweep(root, retentionDays: 30, now);

            Assert.False(Has(old, "mic.mp3"));
            Assert.False(Has(old, "system.wav"));
            Assert.True(Has(old, "Vieux - Audio.mp3"));
            Assert.True(Has(old, "Vieux - Transcript.txt"));

            Assert.True(Has(recent, "mic.wav"));           // 1 day old < 30
            Assert.True(Has(untranscribed, "mic.wav"));    // no transcript
            Assert.True(Has(processing, "mic.wav"));       // .echo_status sentinel
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Sweep_RetentionDisabled_NeverTouchesTrackSources()
    {
        var root = NewDir();
        try
        {
            var s = Path.Combine(root, "20240101 - 10h00 - TresVieux");
            Directory.CreateDirectory(s);
            Put(s, "TresVieux - Transcript.txt");
            Put(s, "mic.wav", 5000);
            Put(s, "system.mp3", 5000);

            RetentionSweeper.Sweep(root, retentionDays: 0, new DateTime(2026, 7, 14));

            Assert.True(Has(s, "mic.wav"));
            Assert.True(Has(s, "system.mp3"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ── AudioArchive ────────────────────────────────────────────────────────────

    [Fact]
    public void FindTrackSource_PrefersWav_FallsBackToMp3_IgnoresTinyFiles()
    {
        var s = NewDir();
        try
        {
            Assert.Null(AudioArchive.FindTrackSource(s, "mic"));

            Put(s, "mic.mp3", 5000);
            Assert.EndsWith("mic.mp3", AudioArchive.FindTrackSource(s, "mic")!);

            Put(s, "mic.wav", 5000);
            Assert.EndsWith("mic.wav", AudioArchive.FindTrackSource(s, "mic")!);

            var tiny = Path.Combine(s, "system.wav");
            File.WriteAllBytes(tiny, new byte[10]);       // dead capture stub
            Assert.Null(AudioArchive.FindTrackSource(s, "system"));
        }
        finally { Directory.Delete(s, recursive: true); }
    }

    /// <summary>Minimal canonical PCM WAV writer (44-byte header + silence).</summary>
    private static void WriteWav(string path, int seconds, int rate = 48000, short channels = 2)
    {
        const short bits = 16;
        int bpf = channels * (bits / 8);
        int dataLen = seconds * rate * bpf;
        using var bw = new BinaryWriter(File.Create(path));
        bw.Write(Encoding.ASCII.GetBytes("RIFF")); bw.Write(36 + dataLen);
        bw.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); bw.Write(16);
        bw.Write((short)1); bw.Write(channels); bw.Write(rate);
        bw.Write(rate * bpf); bw.Write((short)bpf); bw.Write(bits);
        bw.Write(Encoding.ASCII.GetBytes("data")); bw.Write(dataLen);
        bw.Write(new byte[dataLen]);
    }

    [Fact]
    public void WavHeaderDuration_ReadsHeaderWithoutLoadingData()
    {
        var s = NewDir();
        try
        {
            var wav = Path.Combine(s, "mic.wav");
            WriteWav(wav, seconds: 3);
            Assert.Equal(3.0, AudioArchive.WavHeaderDuration(wav), precision: 2);
        }
        finally { Directory.Delete(s, recursive: true); }
    }

    [Fact]
    public void ArchiveTrackToMp3_ConvertsAndDeletesWav_OrKeepsItWithoutFfmpeg()
    {
        var s = NewDir();
        try
        {
            // 2 s of 48 kHz stereo silence — enough to exercise the full pipeline.
            var wav = Path.Combine(s, "mic.wav");
            WriteWav(wav, seconds: 2);

            var mp3 = AudioArchive.ArchiveTrackToMp3(wav);

            if (AudioArchive.FfmpegUsable())
            {
                Assert.NotNull(mp3);
                Assert.True(File.Exists(mp3));
                Assert.True(new FileInfo(mp3!).Length > 0);
                Assert.False(File.Exists(wav));            // deleted only after the decode check
            }
            else
            {
                Assert.Null(mp3);                          // no ffmpeg → WAV kept, no error
                Assert.True(File.Exists(wav));
            }
        }
        finally { Directory.Delete(s, recursive: true); }
    }

    [Fact]
    public void CleanupIntermediates_RemovesRegenerables_KeepsThemWithoutSource()
    {
        var s = NewDir();
        try
        {
            Put(s, "mic.wav", 5000);
            Put(s, "mic_16k.wav");
            Put(s, "mic_16k_trim.wav");
            Put(s, "mic_16k_trim.mp3");                    // upload-mp3 leftover
            Put(s, "system_16k.wav");                      // no system source → kept
            Put(s, "system_16k.mp3");                      // idem: may be the only audio left

            AudioArchive.CleanupIntermediates(s);

            Assert.True(Has(s, "mic.wav"));
            Assert.False(Has(s, "mic_16k.wav"));
            Assert.False(Has(s, "mic_16k_trim.wav"));
            Assert.False(Has(s, "mic_16k_trim.mp3"));
            Assert.True(Has(s, "system_16k.wav"));
            Assert.True(Has(s, "system_16k.mp3"));
        }
        finally { Directory.Delete(s, recursive: true); }
    }

    [Fact]
    public void EncodeForUpload_ProducesMp3_AndKeepsTheWav()
    {
        var s = NewDir();
        try
        {
            var wav = Path.Combine(s, "mic_16k_trim.wav");
            WriteWav(wav, seconds: 2, rate: 16000, channels: 1);

            var mp3 = AudioArchive.EncodeForUpload(wav);

            if (AudioArchive.FfmpegUsable())
            {
                Assert.NotNull(mp3);
                Assert.EndsWith("mic_16k_trim.mp3", mp3!);
                Assert.True(new FileInfo(mp3!).Length >= 1000);
                Assert.True(File.Exists(wav));             // upload encode NEVER deletes the source
            }
            else
            {
                Assert.Null(mp3);                          // no ffmpeg → caller uploads the WAV
                Assert.True(File.Exists(wav));
            }
        }
        finally { Directory.Delete(s, recursive: true); }
    }
}
