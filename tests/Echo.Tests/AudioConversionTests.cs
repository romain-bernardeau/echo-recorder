using Echo.Audio.Internal;
using NAudio.Wave;

namespace Echo.Tests;

/// <summary>
/// <see cref="CaptureConverter"/>: after a watchdog restart or a default-device switch the
/// new device may have another channel count (4-ch array mic, mono BT headset) or rate
/// (HFP 16 kHz, 44.1 kHz USB) than the WAV fixed at open. Bytes written as-is stretched the
/// WAV; refusing the device lost the track. Everything is now converted to the WAV format.
/// </summary>
public class AudioConversionTests
{
    // ── channel mapping ──────────────────────────────────────────────────────────

    [Fact]
    public void Map_StereoToMono_Averages()
    {
        float[] src = { 0.2f, 0.4f, -1f, 1f };
        var dst = new float[2];
        CaptureConverter.MapChannels(src, 2, dst, 1);
        Assert.Equal(0.3f, dst[0], 5);
        Assert.Equal(0f, dst[1], 5);
    }

    [Fact]
    public void Map_MonoToStereo_Duplicates()
    {
        float[] src = { 0.5f, -0.25f };
        var dst = new float[4];
        CaptureConverter.MapChannels(src, 1, dst, 2);
        Assert.Equal(new[] { 0.5f, 0.5f, -0.25f, -0.25f }, dst);
    }

    [Fact]
    public void Map_FourToStereo_AveragesFrontAndBackPairs()
    {
        // FL FR BL BR → L = (FL+BL)/2, R = (FR+BR)/2
        float[] src = { 0.1f, 0.2f, 0.3f, 0.4f };
        var dst = new float[2];
        CaptureConverter.MapChannels(src, 4, dst, 2);
        Assert.Equal(0.2f, dst[0], 5);
        Assert.Equal(0.3f, dst[1], 5);
    }

    [Fact]
    public void Map_FourToMono_AveragesAll()
    {
        float[] src = { 0.1f, 0.2f, 0.3f, 0.4f };
        var dst = new float[1];
        CaptureConverter.MapChannels(src, 4, dst, 1);
        Assert.Equal(0.25f, dst[0], 5);
    }

    [Fact]
    public void Map_StereoToFour_Repeats()
    {
        float[] src = { 0.1f, -0.1f };
        var dst = new float[4];
        CaptureConverter.MapChannels(src, 2, dst, 4);
        Assert.Equal(new[] { 0.1f, -0.1f, 0.1f, -0.1f }, dst);
    }

    [Fact]
    public void Map_MonoToFour_Duplicates()
    {
        float[] src = { 0.7f };
        var dst = new float[4];
        CaptureConverter.MapChannels(src, 1, dst, 4);
        Assert.All(dst, v => Assert.Equal(0.7f, v));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(4, 2)]
    [InlineData(2, 4)]
    [InlineData(4, 1)]
    [InlineData(1, 4)]
    public void Convert_ChannelChange_KeepsFrameCount(int srcCh, int dstCh)
    {
        // 480 float frames at 48 kHz → exactly 480 int16 frames in the WAV's channel count
        // (the old passthrough wrote srcCh samples per frame: a stretched / corrupt WAV).
        var conv = new CaptureConverter(WaveFormat.CreateIeeeFloatWaveFormat(48000, srcCh), 48000, dstCh);
        var buf = FloatBytes(Enumerable.Repeat(0.5f, 480 * srcCh).ToArray());
        byte[] outp = Array.Empty<byte>();
        int n = conv.Convert(buf, ref outp);
        Assert.Equal(480 * dstCh * 2, n);
        short first = (short)(outp[0] | (outp[1] << 8));
        Assert.InRange(first, 16000, 16500); // 0.5 full scale survives the mapping
    }

    // ── decoding ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Convert_Pcm16Passthrough_SameFormat()
    {
        var fmt = new WaveFormat(16000, 16, 1);
        var conv = new CaptureConverter(fmt, 16000, 1);
        var src = new byte[] { 0x00, 0x40, 0x00, 0xC0 }; // +16384, -16384
        byte[] outp = Array.Empty<byte>();
        int n = conv.Convert(src, ref outp);
        Assert.Equal(4, n);
        Assert.InRange((short)(outp[0] | (outp[1] << 8)), 16380, 16384);
        Assert.InRange((short)(outp[2] | (outp[3] << 8)), -16384, -16380);
    }

    [Fact]
    public void Convert_Pcm24_SignExtended()
    {
        var fmt = new WaveFormat(48000, 24, 1);
        var conv = new CaptureConverter(fmt, 48000, 1);
        // -0.5 full scale in 24-bit = 0xC00000
        var src = new byte[] { 0x00, 0x00, 0xC0 };
        byte[] outp = Array.Empty<byte>();
        Assert.Equal(2, conv.Convert(src, ref outp));
        Assert.InRange((short)(outp[0] | (outp[1] << 8)), -16384, -16380);
    }

    [Fact]
    public void Convert_DropsTrailingPartialFrame()
    {
        var conv = new CaptureConverter(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), 48000, 2);
        var buf = new byte[8 * 3 + 5]; // 3 frames + 5 stray bytes
        byte[] outp = Array.Empty<byte>();
        Assert.Equal(3 * 2 * 2, conv.Convert(buf, ref outp));
    }

    // ── resampling ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(48000, 16000)]
    [InlineData(16000, 48000)]
    [InlineData(44100, 48000)]
    [InlineData(48000, 44100)]
    public void Resample_LengthMatchesRatio_WithinOnePercent(int inRate, int outRate)
    {
        // 2 s of a 440 Hz tone fed as 10 ms callbacks, state kept between them.
        var conv = new CaptureConverter(WaveFormat.CreateIeeeFloatWaveFormat(inRate, 1), outRate, 1);
        Assert.True(conv.Resampling);
        int perCallback = inRate / 100;
        long outFrames = 0;
        double peak = 0;
        byte[] outp = Array.Empty<byte>();
        for (int cb = 0; cb < 200; cb++)
        {
            var samples = new float[perCallback];
            for (int i = 0; i < perCallback; i++)
                samples[i] = 0.5f * (float)Math.Sin(2 * Math.PI * 440 * (cb * perCallback + i) / inRate);
            int n = conv.Convert(FloatBytes(samples), ref outp);
            outFrames += n / 2;
            for (int i = 0; i < n; i += 2)
                peak = Math.Max(peak, Math.Abs((short)(outp[i] | (outp[i + 1] << 8))) / 32768.0);
        }
        double expected = 2.0 * outRate;
        Assert.InRange(outFrames, expected * 0.99, expected * 1.01);
        Assert.InRange(peak, 0.4, 0.55); // the tone survives (no silence, no clipping)
    }

    [Fact]
    public void Resample_StereoTo48k_FromMono16k_FrameCount()
    {
        // BT headset (HFP 16 kHz mono) after a switch, WAV fixed at 48 kHz stereo.
        var conv = new CaptureConverter(new WaveFormat(16000, 16, 1), 48000, 2);
        byte[] outp = Array.Empty<byte>();
        long frames = 0;
        for (int cb = 0; cb < 100; cb++)
            frames += conv.Convert(new byte[160 * 2], ref outp) / 4;
        Assert.InRange(frames, 48000 * 0.99, 48000 * 1.01);
    }

    private static byte[] FloatBytes(float[] samples)
    {
        var b = new byte[samples.Length * 4];
        Buffer.BlockCopy(samples, 0, b, 0, b.Length);
        return b;
    }
}
