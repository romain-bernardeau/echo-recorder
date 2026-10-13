using Echo.Core.Models;
using NAudio.CoreAudioApi;

namespace Echo.Audio.Internal;

/// <summary>
/// System-track controller with mode selection + automatic fallback. Ports
/// capture.SystemLoopback. Modes: "endpoint" (default endpoint loopback),
/// "process" (Teams process-loopback, endpoint fallback only if no Teams),
/// "auto" (process-loopback with the anti-Teams-bug guard: fall back to endpoint
/// if no audible frame after 20 s, or audible-then-muted &gt; 45 s while the
/// meeting is active). Fallback rewrites the same canonical system.wav/json with
/// a fresh clean t0 (no padding).
/// </summary>
internal sealed class SystemLoopbackController : ITrack
{
    private readonly CaptureClock _clock;
    private readonly string _outDir;
    private readonly string _mode;
    private readonly Func<bool>? _meetingActive;
    private readonly Action<string>? _onEvent;
    private readonly string? _preferredLoopbackId;

    private volatile ITrack? _inner;
    private string? _phase;          // "process" | "endpoint"
    private bool _switched;
    private int _switchCount;
    private double _startWallEpoch;
    // No loopback could be opened: quiet reopen attempts from the watchdog.
    private readonly object _gate = new();
    private bool _stopped;
    private double _lastReopenPerf = double.NegativeInfinity;
    private int _reopenAttempts;

    public string WavPath { get; }
    public string JsonPath { get; }

    public SystemLoopbackController(CaptureClock clock, string outDir, string mode,
        Func<bool>? meetingActive = null, Action<string>? onEvent = null,
        string? preferredLoopbackId = null)
    {
        _clock = clock;
        _outDir = outDir;
        _mode = mode;
        _meetingActive = meetingActive;
        _onEvent = onEvent;
        _preferredLoopbackId = preferredLoopbackId;
        WavPath = Path.Combine(outDir, "system.wav");
        JsonPath = Path.Combine(outDir, "system.json");
    }

    public bool HasInner => _inner is not null;

    // ---- ITrack proxy surface -------------------------------------------------
    public string Label => "system";
    public TrackKind Kind => TrackKind.Loopback;
    public T0? T0Stamp => (_inner as TrackRecorderBase)?.T0Stamp;
    public double LastRms => (_inner as TrackRecorderBase)?.LastRms ?? 0.0;
    public double? LastAudibleWall => (_inner as TrackRecorderBase)?.LastAudibleWall;
    public int RestartCount => ((_inner as TrackRecorderBase)?.RestartCount ?? 0) + _switchCount;
    public string? Error => _inner?.Error;
    public double DurationSeconds => _inner?.DurationSeconds ?? 0.0;
    public double? LastDataPerfSeconds => _inner?.LastDataPerfSeconds;
    public double OpenedPerfSeconds => _inner?.OpenedPerfSeconds ?? 0.0;
    public bool ContinuousStream => _inner?.ContinuousStream ?? false;

    private void Emit(string msg)
    {
        try { _onEvent?.Invoke(msg); } catch { /* ignore */ }
    }

    public void Start()
    {
        _startWallEpoch = ClockUtil.NowWallEpoch();
        if (_mode is "auto" or "process")
        {
            var pids = TeamsPidResolver.ResolveTeamsPids(msg => Emit(msg));
            if (pids.Count > 0)
            {
                try
                {
                    var rec = new ProcessLoopbackRecorder(pids, WavPath, JsonPath, _clock);
                    rec.Start();
                    _inner = rec;
                    _phase = "process";
                    Emit($"capture Teams en process-loopback (PID cible {pids[0]})");
                    return;
                }
                catch (Exception ex)
                {
                    Emit($"demarrage process-loopback KO ({ex.Message})");
                    _inner = null;
                }
            }
            else
            {
                Emit("aucune session audio Teams detectee");
            }
        }
        StartEndpoint("demarrage en loopback endpoint");
    }

    private void StartEndpoint(string reason)
    {
        AudioGuard.EnsureEndpointAudible();
        _inner = OpenLoopbackResilient();
        _phase = "endpoint";
        Emit(_inner is null
            ? $"{reason} : aucun loopback endpoint ouvrable (micro seul cote systeme)"
            : reason);
    }

    /// <summary>
    /// Try the preferred device, then the default render endpoint, then every active
    /// render endpoint; null if none opens (→ mic-only). Removes the empty system.wav
    /// left by failed attempts. Port of open_loopback_resilient.
    /// </summary>
    private NAudioTrackRecorder? OpenLoopbackResilient()
    {
        var candidates = new List<string?>();
        if (!string.IsNullOrEmpty(_preferredLoopbackId)) candidates.Add(_preferredLoopbackId);
        candidates.Add(null); // default render endpoint
        try
        {
            using var e = new MMDeviceEnumerator();
            foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                candidates.Add(d.ID);
        }
        catch { /* enumeration best-effort */ }

        var seen = new HashSet<string>();
        foreach (var id in candidates)
        {
            string key = id ?? "<default>";
            if (!seen.Add(key)) continue;
            var rec = new NAudioTrackRecorder("system", TrackKind.Loopback, WavPath, JsonPath, _clock, id, _onEvent);
            try
            {
                rec.Start();
                return rec;
            }
            catch
            {
                // Start() already released the WAV handle on failure.
            }
        }
        try { if (File.Exists(WavPath)) File.Delete(WavPath); } catch { /* ignore */ }
        return null;
    }

    private void Fallback(string reason)
    {
        if (_switched || _phase != "process") return;
        _switched = true;
        _switchCount++;
        Emit($"repli automatique ({reason}) -> bascule sur le loopback endpoint");
        try { _inner?.Stop(); } catch { /* ignore */ }
        // Remove the (silent) system.wav to restart cleanly on the same name.
        try { if (File.Exists(WavPath)) File.Delete(WavPath); } catch { /* ignore */ }
        StartEndpoint("repli endpoint actif");
    }

    /// <summary>
    /// No loopback opened at start (or after a fallback): retry every
    /// <see cref="AudioConstants.ReopenIntervalSeconds"/>. Before 2026-10 the system track
    /// was simply lost for the whole meeting. The new recorder rewrites system.wav/json
    /// from scratch, so its sidecar carries a fresh, consistent t0 (the transcription
    /// aligns it like any late-starting track). Logs the first failure and the success only.
    /// </summary>
    private void TryReopen(double nowPerfSeconds)
    {
        if (nowPerfSeconds - _lastReopenPerf < AudioConstants.ReopenIntervalSeconds) return;
        _lastReopenPerf = nowPerfSeconds;
        _reopenAttempts++;
        var rec = OpenLoopbackResilient();
        if (rec is null)
        {
            if (_reopenAttempts == 1)
                Emit($"[Echo.Audio] system: aucun loopback ouvrable, nouvelle tentative toutes les {AudioConstants.ReopenIntervalSeconds:0} s.");
            return;
        }
        bool keep;
        lock (_gate)
        {
            keep = !_stopped;
            if (keep) { _inner = rec; _phase = "endpoint"; }
        }
        if (!keep) { try { rec.Stop(); } catch { /* ignore */ } return; }
        Emit($"[Echo.Audio] system: loopback endpoint ouvert apres {_reopenAttempts} nouvelle(s) tentative(s) ({rec.DeviceName}).");
    }

    public void WatchdogTick(double nowPerfSeconds)
    {
        if (_inner is null)
        {
            lock (_gate) { if (_stopped) return; }
            try { TryReopen(nowPerfSeconds); }
            catch { /* a reopen failure must not stop supervision */ }
            return;
        }

        try { _inner?.WatchdogTick(nowPerfSeconds); }
        catch { /* internal tick failures must not stop supervision */ }

        if (_mode != "auto" || _switched || _phase != "process") return;

        var innerErr = _inner?.Error;
        if (innerErr is not null)
        {
            Fallback($"erreur process-loopback : {innerErr}");
            return;
        }

        double elapsed = ClockUtil.NowWallEpoch() - _startWallEpoch;
        double? audible = LastAudibleWall;
        if (audible is null && elapsed >= AudioConstants.FirstAudibleDeadlineSeconds)
        {
            // frames=0 → the loopback never delivered anything (dead capture); frames>0 →
            // it delivered SILENT frames (wrong target, or genuinely nobody spoke yet).
            // The distinction is the key diagnostic for the recurring live fallbacks.
            var frames = (_inner as TrackRecorderBase)?.FramesWritten ?? -1;
            Fallback($"aucune trame audible apres {AudioConstants.FirstAudibleDeadlineSeconds:0}s (trames recues={frames})");
            return;
        }
        if (audible is not null)
        {
            double since = ClockUtil.NowWallEpoch() - audible.Value;
            bool active = true;
            if (_meetingActive is not null)
            {
                try { active = _meetingActive(); } catch { active = true; }
            }
            if (since >= AudioConstants.SilenceLimitSeconds && active)
                Fallback($"process Teams muet depuis {since:0}s");
        }
    }

    public void Stop()
    {
        lock (_gate) _stopped = true;
        try { _inner?.Stop(); } catch { /* ignore */ }
    }

    public TrackResult ToResult() =>
        _inner?.ToResult() ?? new TrackResult("system", WavPath, 0.0, Error);
}
