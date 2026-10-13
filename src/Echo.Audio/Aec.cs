namespace Echo.Audio;

/// <summary>
/// Acoustic echo cancellation for the no-headset case (port target of aec.py,
/// WebRTC AEC3 via LiveKit's AudioProcessingModule). There is NO managed .NET
/// equivalent for the WebRTC APM, so this is intentionally a compiling stub:
/// <see cref="Available"/> returns false and the caller must fall back to
/// text-level echo de-duplication (exactly as the Python does when livekit is
/// absent — see docs/contract/audio.md §4.4 and ai.md §2.6).
///
/// The exact recipe to reproduce when a native APM is wired in later:
///   • resample mic + system to 16 kHz mono (soxr, same as AudioPrep);
///   • align to common t0 = min(mic_t0, sys_t0), prepending
///     round((track_t0 - common) * 16000) samples of silence to the later track;
///   • truncate both to whole 10 ms frames (n -= n % (FRAME*2), FRAME = 160);
///   • per frame: process_reverse_stream(ref); set_stream_delay_ms(delay);
///     process_stream(mic) — collect processed mic frame;
///   • append the mic tail beyond the reference unchanged;
///   • write out_wav = 16 kHz mono 16-bit; HPF on, NS off, AGC off.
/// </summary>
public static class Aec
{
    public const int Rate = 16000;
    public const int Frame = 160; // 10 ms @ 16 kHz (WebRTC APM frame size)

    /// <summary>Always false: no managed WebRTC APM available (→ text-dedup fallback).</summary>
    public static bool Available() => false;

    // TODO(audio): implement signal-level AEC via a native webrtc-audio-processing
    // (APM3) build exposed through P/Invoke, then reproduce the flow documented
    // above. Until then the transcription pipeline uses text-level echo dedup.
    public static string CancelEcho(string micWav, string sysWav, double micT0, double sysT0,
        string outWav, string workDir, int delayMs = 0, bool noiseSuppression = false)
        => throw new NotSupportedException(
            "AEC is unavailable in the .NET port (no managed WebRTC APM). " +
            "Fall back to text-level echo de-duplication.");
}
