using System.Diagnostics;
using System.Globalization;
using Echo.Ai;

namespace Echo.Tests;

/// <summary>
/// Echo.Ai audio preparation (2026-10): aligned mixdown (adelay from the sidecar t0s),
/// culture-proof ffmpeg arguments, quiet-point chunk cuts, bounded ffmpeg runner.
/// </summary>
public class AudioPrepTests
{
    // ── mixdown alignment ────────────────────────────────────────────────────────

    [Fact]
    public void MixDelays_LaterTrackDelayedByT0Difference()
    {
        var d = AudioPrep.ShareMixDelaysMs(new double?[] { 1_700_000_000.000, 1_700_000_000.250 });
        Assert.Equal(0.0, d[0], 3);
        Assert.Equal(250.0, d[1], 3);

        // Whichever track starts first gets no delay.
        d = AudioPrep.ShareMixDelaysMs(new double?[] { 1_700_000_002.0, 1_700_000_000.5 });
        Assert.Equal(1500.0, d[0], 3);
        Assert.Equal(0.0, d[1], 3);
    }

    [Fact]
    public void MixDelays_UnknownT0_IsNotDelayed()
    {
        Assert.Equal(new[] { 0.0, 0.0 }, AudioPrep.ShareMixDelaysMs(new double?[] { null, 1_700_000_000.0 }));
        Assert.Equal(new[] { 0.0, 0.0 }, AudioPrep.ShareMixDelaysMs(new double?[] { null, null }));
    }

    [Fact]
    public void MixFilter_TwoTracks_DelaysOnlyTheLaterOne()
    {
        string fc = AudioPrep.BuildShareMixFilter(new[] { 0.0, 250.4 }, soxr: false);
        Assert.Equal(
            "[0:a]aresample=osr=16000,aformat=channel_layouts=mono[a0];" +
            "[1:a]aresample=osr=16000,aformat=channel_layouts=mono,adelay=250[a1];" +
            "[a0][a1]amix=inputs=2:duration=longest:normalize=1[m]", fc);
    }

    [Fact]
    public void MixFilter_SingleTrack_NoMixNoDelay()
    {
        Assert.Equal("[0:a]aresample=resampler=soxr:osr=16000,aformat=channel_layouts=mono[m]",
            AudioPrep.BuildShareMixFilter(new[] { 0.0 }, soxr: true));
    }

    [Fact]
    public void SidecarT0_ReadFromTagJson_ForWavOrMp3()
    {
        var dir = Directory.CreateTempSubdirectory("echo-prep-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "system.json"),
                "{\"got_audio\": true, \"t0_wall_epoch\": 1700000123.456}");
            File.WriteAllText(Path.Combine(dir.FullName, "mic.json"),
                "{\"got_audio\": false, \"t0_wall_epoch\": null}");
            Assert.Equal(1700000123.456, AudioPrep.ReadSidecarT0(Path.Combine(dir.FullName, "system.wav"))!.Value, 3);
            Assert.Equal(1700000123.456, AudioPrep.ReadSidecarT0(Path.Combine(dir.FullName, "system.mp3"))!.Value, 3);
            Assert.Null(AudioPrep.ReadSidecarT0(Path.Combine(dir.FullName, "mic.wav")));
            Assert.Null(AudioPrep.ReadSidecarT0(Path.Combine(dir.FullName, "absent.wav")));
        }
        finally { dir.Delete(true); }
    }

    // ── culture ──────────────────────────────────────────────────────────────────

    [Fact]
    public void FfmpegArguments_AreCultureInvariant_UnderFrFR()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            Assert.Equal("1,6", 1.6.ToString()); // the trap being guarded against
            Assert.Equal("silencedetect=noise=-33.5dB:d=1.6", AudioPrep.SilenceDetectFilter(-33.5, 1.6));
            string fc = AudioPrep.BuildShareMixFilter(new[] { 0.0, 1234.6 }, soxr: false);
            Assert.Contains("adelay=1235[a1]", fc);
            Assert.DoesNotContain(",5", fc);
            Assert.DoesNotContain(" ", fc); // fr-FR group separator
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    // ── chunk cut on a quiet point ───────────────────────────────────────────────

    private static string WriteNoiseWav(string path, int rate, int seconds, double silenceStart, double silenceEnd)
    {
        int frames = rate * seconds;
        var data = new byte[frames * 2];
        uint lcg = 12345;
        for (int i = 0; i < frames; i++)
        {
            double t = (double)i / rate;
            short v = 0;
            if (t < silenceStart || t >= silenceEnd)
            {
                lcg = lcg * 1664525 + 1013904223;
                v = (short)((int)(lcg >> 16) % 16000 - 8000); // speech-level noise
            }
            data[i * 2] = (byte)(v & 0xFF);
            data[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        using var fs = new FileStream(path, FileMode.Create);
        var bw = new BinaryWriter(fs);
        bw.Write("RIFF"u8.ToArray()); bw.Write(36 + data.Length); bw.Write("WAVE"u8.ToArray());
        bw.Write("fmt "u8.ToArray()); bw.Write(16); bw.Write((short)1); bw.Write((short)1); bw.Write(rate);
        bw.Write(rate * 2); bw.Write((short)2); bw.Write((short)16);
        bw.Write("data"u8.ToArray()); bw.Write(data.Length);
        bw.Write(data);
        return path;
    }

    [Fact]
    public void SplitWav_CutsInThePause_NotAtTheNominalSecond()
    {
        var dir = Directory.CreateTempSubdirectory("echo-split-");
        try
        {
            // 150 s of continuous "speech" with one 0.5 s pause at 45 s; 60 s chunks.
            var wav = WriteNoiseWav(Path.Combine(dir.FullName, "mic_16k.wav"), 16000, 150, 45.0, 45.5);
            var pieces = AudioPrep.SplitWav(wav, 60);
            Assert.True(pieces.Count >= 2);
            Assert.Equal(0.0, pieces[0].StartSeconds);
            Assert.InRange(pieces[1].StartSeconds, 45.0, 45.5);
            // Pieces stay contiguous, cover the whole track and never exceed the chunk
            // (except the merged tail).
            double total = 0;
            for (int i = 0; i < pieces.Count; i++)
            {
                double d = AudioPrep.WavDuration(pieces[i].Path);
                if (i < pieces.Count - 1)
                {
                    Assert.True(d <= 60.0 + 1e-9);
                    Assert.Equal(pieces[i + 1].StartSeconds, pieces[i].StartSeconds + d, 3);
                }
                total += d;
            }
            Assert.Equal(150.0, total, 3);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void SplitWav_NoPause_FallsBackInsideTheSearchWindow()
    {
        var dir = Directory.CreateTempSubdirectory("echo-split-");
        try
        {
            // No pause at all: the cut still lands in [30 s, 60 s], never beyond the chunk.
            var wav = WriteNoiseWav(Path.Combine(dir.FullName, "sys_16k.wav"), 16000, 130, -1, -1);
            var pieces = AudioPrep.SplitWav(wav, 60);
            Assert.InRange(pieces[1].StartSeconds, 30.0, 60.0);
        }
        finally { dir.Delete(true); }
    }

    // ── bounded ffmpeg ───────────────────────────────────────────────────────────

    [Fact]
    public void FfmpegTimeout_ProportionalWithBounds()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), Ffmpeg.TimeoutFor(10.0));          // floor
        Assert.Equal(TimeSpan.FromSeconds(60 + 1800), Ffmpeg.TimeoutFor(3600.0)); // 1 h input
        Assert.Equal(TimeSpan.FromHours(4), Ffmpeg.TimeoutFor(100_000.0));       // ceiling
        Assert.Equal(TimeSpan.FromMinutes(2), Ffmpeg.TimeoutFor(double.NaN));
    }

    [Fact]
    public void Ffmpeg_HungProcess_IsKilledAtTimeout()
    {
        if (!AudioArchive.FfmpegUsable()) return; // needs ffmpeg (bundled in releases)
        var sw = Stopwatch.StartNew();
        // -re paces a 10 min silent source in real time: a stand-in for a hung ffmpeg.
        Assert.Throws<TimeoutException>(() => Ffmpeg.Run(
            new[] { "-hide_banner", "-re", "-f", "lavfi", "-i", "anullsrc=r=16000:cl=mono", "-t", "600", "-f", "null", "-" },
            TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void Ffmpeg_Cancellation_KillsProcess()
    {
        if (!AudioArchive.FfmpegUsable()) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var sw = Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => Ffmpeg.Run(
            new[] { "-hide_banner", "-re", "-f", "lavfi", "-i", "anullsrc=r=16000:cl=mono", "-t", "600", "-f", "null", "-" },
            TimeSpan.FromMinutes(5), cts.Token));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15));
    }
}
