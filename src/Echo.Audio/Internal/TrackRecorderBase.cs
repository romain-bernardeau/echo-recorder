using Echo.Core.Models;

namespace Echo.Audio.Internal;

/// <summary>
/// Common surface of a single recorded track (mic / endpoint-loopback /
/// process-loopback), mirroring the Python TrackRecorder API consumed by the
/// watchdog and the sidecar writer.
/// </summary>
internal abstract class TrackRecorderBase : ITrack
{
    protected TrackRecorderBase(string label, TrackKind kind, string wavPath, string jsonPath,
        CaptureClock clock)
    {
        Label = label;
        Kind = kind;
        WavPath = wavPath;
        JsonPath = jsonPath;
        Clock = clock;
    }

    public string Label { get; }
    public TrackKind Kind { get; }
    public string WavPath { get; }
    public string JsonPath { get; }
    protected CaptureClock Clock { get; }

    public int Rate { get; protected set; } = AudioConstants.ProcessRate;
    public int Channels { get; protected set; } = AudioConstants.ProcessChannels;
    public string DeviceName { get; protected set; } = "";
    public int DeviceIndex { get; protected set; } = -1;

    // t0 (first frame) — the alignment anchor. Null until first frame received.
    public T0? T0Stamp { get; protected set; }
    public long FramesWritten { get; protected set; }
    public double LastLevel { get; protected set; }       // peak 0..1 (vumeter)
    public double LastRms { get; protected set; }          // RMS 0..1 (meters)
    public double? LastAudibleWall { get; protected set; } // wall epoch of last real signal
    public bool GotAudible { get; protected set; }
    public string? Error { get; protected set; }
    public int RestartCount { get; protected set; }
    public double TotalGapSeconds { get; protected set; }
    public string? LastReason { get; protected set; }

    public bool GotAudio => T0Stamp is not null;
    public double DurationSeconds => Rate > 0 ? (double)FramesWritten / Rate : 0.0;

    // Perf-counter ticks of the last REAL data buffer (0 = none yet) and of the open.
    // Written by the capture thread, read by the watchdog: Volatile, never under a lock.
    private long _lastDataTicks;
    private long _openedTicks;

    /// <summary>Perf seconds of the last buffer delivered by the device (never a pad), null if none yet.</summary>
    public double? LastDataPerfSeconds
    {
        get
        {
            long t = Volatile.Read(ref _lastDataTicks);
            return t == 0 ? null : t / (double)System.Diagnostics.Stopwatch.Frequency;
        }
    }

    /// <summary>Perf seconds of the successful <see cref="Start"/> (reference before any data).</summary>
    public double OpenedPerfSeconds => Volatile.Read(ref _openedTicks) / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>True when the device delivers frames continuously even in silence, so a
    /// lack of frames means a failure (mic capture, kept-alive loopback endpoint).</summary>
    public virtual bool ContinuousStream => false;

    protected void MarkData() => Volatile.Write(ref _lastDataTicks, System.Diagnostics.Stopwatch.GetTimestamp());
    protected void MarkOpened() => Volatile.Write(ref _openedTicks, System.Diagnostics.Stopwatch.GetTimestamp());

    public abstract void Start();
    public abstract void Stop();
    public virtual void WatchdogTick(double nowPerfSeconds) { }

    public TrackResult ToResult() => new(Label, WavPath, DurationSeconds, Error);

    /// <summary>Builds the ordered sidecar map (base fields). Overridden to add source/got_audible.</summary>
    protected virtual IEnumerable<KeyValuePair<string, object?>> BuildSidecar(bool finalized)
    {
        yield return new("label", Label);
        yield return new("kind", Kind == TrackKind.Mic ? "mic" : "loopback");
        yield return new("wav", Path.GetFileName(WavPath));
        yield return new("device_index", DeviceIndex);
        yield return new("device_name", DeviceName);
        yield return new("sample_rate", Rate);
        yield return new("channels", Channels);
        yield return new("sample_width_bytes", AudioConstants.Bits / 8);
        yield return new("got_audio", GotAudio);
        yield return new("t0_wall_epoch", T0Stamp?.WallEpoch);
        yield return new("t0_perf_ns", T0Stamp?.PerfNs);
        yield return new("session_wall0_epoch", ClockUtil.SessionWall0Epoch(Clock));
        yield return new("session_perf0_ns", ClockUtil.SessionPerf0Ns(Clock));
        yield return new("frames_written", FramesWritten);
        yield return new("duration_s", DurationSeconds);
        yield return new("restart_count", RestartCount);
        yield return new("total_gap_s", Math.Round(TotalGapSeconds, 2));
        yield return new("last_restart_reason", LastReason);
        yield return new("finalized", finalized);
    }

    protected void WriteSidecar(bool finalized) => SidecarWriter.Write(JsonPath, BuildSidecar(finalized));
}
