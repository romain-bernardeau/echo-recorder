using Echo.Audio.Internal;

namespace Echo.Tests;

/// <summary>
/// Regression tests for the live loopback gap fill (2026-09-16): the WASAPI endpoint
/// loopback stops delivering frames during digital silence, so system.wav used to lag
/// the wall clock by tens of seconds (measured up to 46 s live), breaking the mic/system
/// echo dedup. <see cref="GapFill.FramesToPad"/> decides how much silence to insert
/// before the first buffer that follows a dropout.
/// </summary>
public class AudioGapFillTests
{
    private const int Rate48 = 48000;
    private const int Rate16 = 16000;

    // ── no pad ───────────────────────────────────────────────────────────────────

    [Fact]
    public void NoPad_WhenCallbackIntervalIsShort_EvenWithLargeDeficit()
    {
        // Frames keep flowing (10 ms callbacks): any deficit is clock drift, never a hole.
        double expected = 100.0 * Rate48;
        long written = 90L * Rate48; // 10 s behind
        Assert.Equal(0, GapFill.FramesToPad(expected, written, 0.010, Rate48));
        Assert.Equal(0, GapFill.FramesToPad(expected, written, 0.5, Rate48)); // exactly at the threshold
    }

    [Fact]
    public void NoPad_WhenDeficitBelowThreshold_DespiteLongInterval()
    {
        // The engine paused for 5 s but the file is only 0.3 s behind (e.g. buffered frames).
        double expected = 60.0 * Rate48;
        long written = (long)(59.7 * Rate48);
        Assert.Equal(0, GapFill.FramesToPad(expected, written, 5.0, Rate48));
    }

    [Fact]
    public void NoPad_WhenFileIsAhead()
    {
        double expected = 60.0 * Rate48;
        long written = 61L * Rate48;
        Assert.Equal(0, GapFill.FramesToPad(expected, written, 5.0, Rate48));
    }

    [Fact]
    public void NoPad_WhenRateIsInvalid()
    {
        Assert.Equal(0, GapFill.FramesToPad(1_000_000, 0, 30.0, 0));
        Assert.Equal(0, GapFill.FramesToPad(1_000_000, 0, 30.0, -1));
    }

    // ── pad = deficit ────────────────────────────────────────────────────────────

    [Fact]
    public void Pad_EqualsDeficit_WhenIntervalAndDeficitExceedThreshold()
    {
        // 46 s dropout (the live measurement), 48 kHz.
        double expected = 300.0 * Rate48;
        long written = 254L * Rate48;
        Assert.Equal(46 * Rate48, GapFill.FramesToPad(expected, written, 46.0, Rate48));
    }

    [Fact]
    public void Pad_RoundsFractionalDeficit()
    {
        double expected = 10.0 * Rate48 + 0.4;
        long written = 8L * Rate48;
        Assert.Equal(2 * Rate48, GapFill.FramesToPad(expected, written, 2.0, Rate48));
    }

    [Fact]
    public void Pad_JustAboveThreshold_IsApplied()
    {
        // 0.6 s hole > 0.5 s minimum on both conditions.
        double expected = 20.0 * Rate48;
        long written = (long)(19.4 * Rate48);
        int pad = GapFill.FramesToPad(expected, written, 0.6, Rate48);
        Assert.InRange(pad, (int)(0.6 * Rate48) - 1, (int)(0.6 * Rate48) + 1);
    }

    // ── cap ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Pad_IsCappedAtMaxPadSeconds()
    {
        // 2 h deficit (runaway clock) must be clamped to the 600 s default cap.
        double expected = 7200.0 * Rate48;
        long written = 0;
        Assert.Equal(600 * Rate48, GapFill.FramesToPad(expected, written, 7200.0, Rate48));
        // Custom cap.
        Assert.Equal(10 * Rate48, GapFill.FramesToPad(expected, written, 7200.0, Rate48, maxPadSeconds: 10));
    }

    // ── sample rate independence ─────────────────────────────────────────────────

    [Theory]
    [InlineData(Rate48)]
    [InlineData(Rate16)]
    [InlineData(44100)]
    public void Pad_ScalesWithSampleRate(int rate)
    {
        // Same 3 s hole expressed at different rates → pad = 3 s worth of frames.
        double expected = 120.0 * rate;
        long written = 117L * rate;
        Assert.Equal(3 * rate, GapFill.FramesToPad(expected, written, 3.0, rate));

        // And the 0.5 s deficit threshold is rate-relative: 0.4 s is never padded.
        Assert.Equal(0, GapFill.FramesToPad(expected, (long)(119.6 * rate), 3.0, rate));
    }

    [Fact]
    public void Threshold_16kVersus48k_SameDurationSameDecision()
    {
        // A 0.45 s deficit is ignored at both rates, 0.55 s is padded at both.
        foreach (var rate in new[] { Rate16, Rate48 })
        {
            double expected = 30.0 * rate;
            Assert.Equal(0, GapFill.FramesToPad(expected, (long)(29.55 * rate), 2.0, rate));
            Assert.True(GapFill.FramesToPad(expected, (long)(29.45 * rate), 2.0, rate) > 0);
        }
    }

    [Fact]
    public void CustomMinGap_IsHonoured()
    {
        double expected = 30.0 * Rate48;
        long written = 29L * Rate48; // 1 s deficit
        Assert.Equal(0, GapFill.FramesToPad(expected, written, 1.5, Rate48, minGapSeconds: 2.0));
        Assert.Equal(Rate48, GapFill.FramesToPad(expected, written, 1.5, Rate48, minGapSeconds: 0.5));
    }
}
