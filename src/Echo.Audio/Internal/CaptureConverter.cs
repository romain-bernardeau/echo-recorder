using NAudio.Dsp;
using NAudio.Wave;

namespace Echo.Audio.Internal;

/// <summary>
/// Converts the buffers of ONE capture (any WASAPI shared-mode format: float32 or
/// 8/16/24/32-bit PCM, any channel count, any rate) into the track WAV's FIXED format:
/// 16-bit PCM at <see cref="TargetRate"/> / <see cref="TargetChannels"/>. The WAV format
/// is set once at open; a watchdog restart or a default-device switch may land on a
/// device with another mix format (Bluetooth HFP 16 kHz mono, 44.1 kHz USB headset,
/// 4-channel array mic…). Writing those bytes as-is stretched / corrupted the WAV, and
/// refusing the device lost the track. Stateful (resampler history across callbacks):
/// create one per capture instance.
/// </summary>
internal sealed class CaptureConverter
{
    private readonly WaveFormat _src;
    private readonly int _srcChannels;
    private readonly int _srcBlock;
    private readonly WdlResampler? _resampler;

    // Reused per-callback buffers (grow-on-demand, never shrunk): no alloc once settled.
    private float[] _decoded = Array.Empty<float>();
    private float[] _mapped = Array.Empty<float>();
    private float[] _resampled = Array.Empty<float>();

    public CaptureConverter(WaveFormat source, int targetRate, int targetChannels)
    {
        if (targetRate <= 0) throw new ArgumentOutOfRangeException(nameof(targetRate));
        if (targetChannels <= 0) throw new ArgumentOutOfRangeException(nameof(targetChannels));
        _src = source;
        _srcChannels = Math.Max(1, source.Channels);
        _srcBlock = _srcChannels * Math.Max(1, source.BitsPerSample / 8);
        TargetRate = targetRate;
        TargetChannels = targetChannels;
        SourceRate = source.SampleRate > 0 ? source.SampleRate : targetRate;

        if (SourceRate != TargetRate)
        {
            // Same settings as NAudio's WdlResamplingSampleProvider (linear interpolation +
            // 2-pole anti-alias filter), but input-driven: every callback pushes exactly
            // the frames it received and gets back whatever the ratio yields.
            _resampler = new WdlResampler();
            _resampler.SetMode(true, 2, false);
            _resampler.SetFilterParms();
            _resampler.SetFeedMode(true);
            _resampler.SetRates(SourceRate, TargetRate);
        }
    }

    public int SourceRate { get; }
    public int TargetRate { get; }
    public int TargetChannels { get; }
    public bool Resampling => _resampler is not null;

    /// <summary>
    /// Converts one capture buffer into interleaved 16-bit PCM in the WAV format, into
    /// <paramref name="pcm16"/> (grown on demand). Returns the number of valid bytes.
    /// A trailing partial frame is dropped.
    /// </summary>
    public int Convert(ReadOnlySpan<byte> src, ref byte[] pcm16)
    {
        int frames = src.Length / _srcBlock;
        if (frames <= 0) return 0;

        Grow(ref _decoded, frames * _srcChannels);
        Decode(src.Slice(0, frames * _srcBlock), _src, _decoded);

        Grow(ref _mapped, frames * TargetChannels);
        MapChannels(_decoded.AsSpan(0, frames * _srcChannels), _srcChannels,
            _mapped.AsSpan(0, frames * TargetChannels), TargetChannels);

        float[] outBuf = _mapped;
        int outFrames = frames;
        if (_resampler is not null)
        {
            int need = _resampler.ResamplePrepare(frames, TargetChannels, out float[] inBuf, out int inOff);
            int feed = Math.Min(need, frames);
            Array.Copy(_mapped, 0, inBuf, inOff, feed * TargetChannels);
            int maxOut = (int)Math.Ceiling((double)feed * TargetRate / SourceRate) + 64;
            Grow(ref _resampled, maxOut * TargetChannels);
            outFrames = _resampler.ResampleOut(_resampled, 0, feed, maxOut, TargetChannels);
            outBuf = _resampled;
        }

        int samples = outFrames * TargetChannels;
        if (pcm16.Length < samples * 2) pcm16 = new byte[samples * 2];
        for (int i = 0; i < samples; i++)
        {
            short s = (short)(Math.Clamp(outBuf[i], -1f, 1f) * 32767f);
            pcm16[i * 2] = (byte)(s & 0xFF);
            pcm16[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return samples * 2;
    }

    /// <summary>Decodes interleaved samples to float [-1, 1]. Float32 when the format says
    /// IEEE float, otherwise integer PCM by bit depth (8 unsigned, 16/24/32 signed).</summary>
    internal static void Decode(ReadOnlySpan<byte> src, WaveFormat fmt, Span<float> dst)
    {
        int bytes = Math.Max(1, fmt.BitsPerSample / 8);
        int n = Math.Min(src.Length / bytes, dst.Length);
        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32;
        for (int i = 0; i < n; i++)
        {
            var s = src.Slice(i * bytes, bytes);
            dst[i] = isFloat
                ? BitConverter.ToSingle(s)
                : bytes switch
                {
                    1 => (s[0] - 128) / 128f,
                    2 => (short)(s[0] | (s[1] << 8)) / 32768f,
                    3 => ((sbyte)s[2] << 16 | s[1] << 8 | s[0]) / 8388608f,
                    _ => BitConverter.ToInt32(s) / 2147483648f,
                };
        }
    }

    /// <summary>
    /// Interleaved channel conversion. Same count: copy. To mono: average of all source
    /// channels. From mono: duplicate. Fewer channels (e.g. 4 → 2): destination channel
    /// c averages the source channels i where i % dstCh == c (FL+BL, FR+BR). More
    /// channels (e.g. 2 → 4): destination channel c repeats source channel c % srcCh.
    /// </summary>
    internal static void MapChannels(ReadOnlySpan<float> src, int srcCh, Span<float> dst, int dstCh)
    {
        int frames = Math.Min(src.Length / srcCh, dst.Length / dstCh);
        if (srcCh == dstCh)
        {
            src.Slice(0, frames * srcCh).CopyTo(dst);
            return;
        }
        for (int f = 0; f < frames; f++)
        {
            var s = src.Slice(f * srcCh, srcCh);
            var d = dst.Slice(f * dstCh, dstCh);
            if (dstCh == 1)
            {
                float sum = 0f;
                for (int c = 0; c < srcCh; c++) sum += s[c];
                d[0] = sum / srcCh;
            }
            else if (srcCh == 1)
            {
                for (int c = 0; c < dstCh; c++) d[c] = s[0];
            }
            else if (srcCh > dstCh)
            {
                for (int c = 0; c < dstCh; c++)
                {
                    float sum = 0f;
                    int count = 0;
                    for (int i = c; i < srcCh; i += dstCh) { sum += s[i]; count++; }
                    d[c] = count > 0 ? sum / count : 0f;
                }
            }
            else
            {
                for (int c = 0; c < dstCh; c++) d[c] = s[c % srcCh];
            }
        }
    }

    private static void Grow(ref float[] buf, int needed)
    {
        if (buf.Length < needed) buf = new float[needed];
    }
}
