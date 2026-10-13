namespace Echo.Audio.Internal;

/// <summary>
/// Pure decision logic for the live gap fill. The WASAPI endpoint loopback stops
/// delivering frames during digital silence (nobody talking, no app playing), so
/// system.wav falls behind the wall clock by up to tens of seconds per meeting; a mic
/// can stall the same way (USB/Bluetooth hiccup), with a higher threshold.
/// When frames resume, the recorder asks how much silence to insert BEFORE the new
/// buffer so the file timeline stays aligned with mic.wav and speakers.jsonl.
/// </summary>
internal static class GapFill
{
    /// <summary>Minimum callback interval that counts as a real dropout (not clock drift).</summary>
    public const double DefaultMinGapSeconds = 0.5;

    /// <summary>Per-track minimum hole: 0.5 s for the loopback (silence dropouts), 1 s for
    /// the mic (it streams continuously, only a real device stall leaves a hole).</summary>
    public static double MinGapSeconds(TrackKind kind) =>
        kind == TrackKind.Mic ? AudioConstants.MicMinGapSeconds : DefaultMinGapSeconds;

    /// <summary>
    /// Number of silent frames to insert before writing the current buffer, or 0.
    /// </summary>
    /// <param name="expectedFrames">Frames the file should already hold at this instant
    /// (perf-counter elapsed since t0 times <paramref name="rate"/>, minus the frames of
    /// the buffer about to be written).</param>
    /// <param name="framesWritten">Frames actually written so far (including earlier pads).</param>
    /// <param name="secondsSinceLastCallback">Perf-counter interval since the previous data callback.</param>
    /// <param name="rate">Sample rate of the track (frames per second).</param>
    /// <param name="minGapSeconds">Both the callback interval AND the deficit must exceed this
    /// to pad; below it, the deficit is treated as slow clock drift and left alone.</param>
    /// <param name="maxPadSeconds">Upper bound on a single pad (safety against a runaway clock).</param>
    public static int FramesToPad(double expectedFrames, long framesWritten, double secondsSinceLastCallback,
        int rate, double minGapSeconds = DefaultMinGapSeconds, double maxPadSeconds = AudioConstants.MaxPadSeconds)
    {
        if (rate <= 0) return 0;
        if (double.IsNaN(expectedFrames) || double.IsNaN(secondsSinceLastCallback)) return 0;
        // A short interval means the endpoint kept delivering: any deficit is drift, not a hole.
        if (secondsSinceLastCallback <= minGapSeconds) return 0;

        double deficit = expectedFrames - framesWritten;
        if (deficit <= minGapSeconds * rate) return 0;

        double cap = Math.Max(0.0, maxPadSeconds) * rate;
        double frames = Math.Min(deficit, cap);
        if (frames > int.MaxValue) frames = int.MaxValue;
        return (int)Math.Round(frames);
    }
}
