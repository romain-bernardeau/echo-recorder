using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using Echo.Core.Models;

namespace Echo.Audio.Internal;

internal static class AudioConstants
{
    /// <summary>Shared "real signal vs digital silence" RMS/peak threshold (Python AUDIBLE_THR).</summary>
    public const double AudibleThreshold = 0.003;

    // Watchdog thresholds (capture.py TrackRecorder).
    public const double StallLimitMicSeconds = 15.0;
    public const double StallLimitLoopbackSeconds = 25.0;
    public const double MinRestartIntervalSeconds = 4.0;
    public const double MaxPadSeconds = 600.0;

    // run_watchdog cadence.
    public const double WatchdogIntervalSeconds = 2.0;
    public const double HeartbeatSeconds = 60.0;
    public const double InaudibleAfterSeconds = 60.0;
    public const double ReNotifySeconds = 300.0;

    // Mic that never delivered a single frame since open: WasapiCapture normally
    // delivers frames even during silence, so "no data at all" means a stuck
    // device/driver (BT mic in the wrong profile, exclusive-mode grab…). Restart
    // after this long, at most N times (then the inaudible alert takes over).
    public const double MicNeverDataSeconds = 15.0;
    public const int MicNeverDataMaxRestarts = 2;

    // Live gap fill minimum hole. Mic callbacks arrive every ~10 ms even in silence, so
    // only a real stall (USB/BT hiccup) of a second or more is filled.
    public const double MicMinGapSeconds = 1.0;
    // Restart pad: the perf-clock deficit below this is buffer latency, not a hole.
    public const double RestartMinPadSeconds = 0.2;

    // Releasing a capture (StopRecording + Dispose joins the capture thread): bounded
    // so a driver stuck in a call can never freeze the watchdog or the UI thread.
    public const double CaptureReleaseTimeoutSeconds = 3.0;

    // Residual loopback drift log: at most one aggregated line per window.
    public const double DriftLogWindowSeconds = 300.0;

    // Track that could not be opened at start: quiet reopen attempts every N seconds.
    public const double ReopenIntervalSeconds = 10.0;

    // Health (IAudioCaptureService.HealthCode). Frame-based only, never RMS-based for
    // the mic: a continuous stream with no frame for this long is a failure.
    public const double LoopbackDeadSeconds = 20.0;
    public const double MicDeadSeconds = 20.0;
    // Same, for a stream that may legitimately pause (process loopback, endpoint
    // without keep-alive): only a very long absence counts.
    public const double NonContinuousDeadSeconds = 120.0;

    // SystemLoopback auto-fallback.
    // 6 s (was 20 s): the process-loopback probe fails on every real meeting (silent frames),
    // and the participants are lost until the endpoint fallback kicks in.
    public const double FirstAudibleDeadlineSeconds = 6.0;
    public const double SilenceLimitSeconds = 45.0;

    // Fixed process-loopback format.
    public const int ProcessRate = 48000;
    public const int ProcessChannels = 2;
    public const int Bits = 16;
}

internal enum TrackKind { Mic, Loopback }

/// <summary>Common surface watched by the watchdog and reported by the service.</summary>
internal interface ITrack
{
    string Label { get; }
    TrackKind Kind { get; }
    T0? T0Stamp { get; }
    double LastRms { get; }
    double? LastAudibleWall { get; }
    int RestartCount { get; }
    string? Error { get; }
    double DurationSeconds { get; }
    /// <summary>Perf seconds of the last real device buffer (null if none yet).</summary>
    double? LastDataPerfSeconds { get; }
    /// <summary>Perf seconds of the track open (liveness reference before any data).</summary>
    double OpenedPerfSeconds { get; }
    /// <summary>The stream delivers frames even in silence: no frames = failure.</summary>
    bool ContinuousStream { get; }
    void Start();
    void WatchdogTick(double nowPerfSeconds);
    void Stop();
    Echo.Core.Models.TrackResult ToResult();
}

/// <summary>
/// Converts the shared <see cref="CaptureClock"/> and "now" instants into the
/// dual (wall-epoch seconds, perf-counter nanoseconds) anchors written verbatim
/// into the sidecars, mirroring the Python session_t = {wall0, perf0_ns}.
/// </summary>
internal static class ClockUtil
{
    private static readonly double NsPerTick = 1_000_000_000.0 / Stopwatch.Frequency;

    public static long PerfTicksToNs(long ticks) => (long)(ticks * NsPerTick);

    public static long NowPerfNs() => PerfTicksToNs(Stopwatch.GetTimestamp());

    public static double NowWallEpoch() =>
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    // Normalize the kind: a Local/Unspecified DateTime would make the
    // DateTimeOffset ctor throw (audit F8); StartNow() uses UtcNow but callers may not.
    public static double SessionWall0Epoch(CaptureClock clock) =>
        new DateTimeOffset(clock.WallUtc.ToUniversalTime(), TimeSpan.Zero).ToUnixTimeMilliseconds() / 1000.0;

    public static long SessionPerf0Ns(CaptureClock clock) => PerfTicksToNs(clock.PerfTimestamp);
}

/// <summary>First-sample alignment anchor (Python t0 dict).</summary>
internal readonly record struct T0(double WallEpoch, long PerfNs);

/// <summary>Peak + RMS metering over an int16 PCM buffer, normalised 0..1.</summary>
internal static class LevelMeter
{
    public static (double Peak, double Rms) Measure(ReadOnlySpan<byte> pcm16, int maxBytes = 4096)
    {
        int len = Math.Min(pcm16.Length, maxBytes);
        len -= len % 2;
        if (len <= 0) return (0.0, 0.0);
        int n = len / 2;
        int peak = 0;
        double sumSq = 0;
        for (int i = 0; i < len; i += 2)
        {
            short s = (short)(pcm16[i] | (pcm16[i + 1] << 8));
            int a = s < 0 ? -s : s;
            if (a > peak) peak = a;
            sumSq += (double)s * s;
        }
        double rms = Math.Sqrt(sumSq / n) / 32768.0;
        return (peak / 32768.0, rms);
    }
}

internal static class SidecarWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Writes an ordered key/value map as UTF-8 JSON (indent 2, non-ASCII kept).
    /// ATOMIC (temp + rename): an in-place truncating write left a 0-byte mic.json
    /// during a live meeting (concurrent watchdog/stop writers), and an empty sidecar
    /// loses the t0 anchor — the track then silently drops out of the transcription.</summary>
    public static void Write(string path, IEnumerable<KeyValuePair<string, object?>> fields)
    {
        // Ordered dictionary preserves the exact key order of the Python sidecar.
        var map = new Dictionary<string, object?>();
        foreach (var kv in fields) map[kv.Key] = kv.Value;
        try
        {
            var json = JsonSerializer.Serialize(map, Options);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // Sidecar writes are best-effort; never break capture on IO failure.
            // (On failure the previous complete sidecar stays in place — never a torn one.)
        }
    }
}
