using System.Diagnostics;
using Echo.Audio.Internal;
using Echo.Core.Abstractions;
using Echo.Core.Models;
using NAudio.CoreAudioApi;

namespace Echo.Audio;

/// <summary>
/// Two-track meeting recorder (mic.wav + system.wav) implementing
/// <see cref="IAudioCaptureService"/>. The mic track is a WASAPI capture; the
/// system track is a <see cref="SystemLoopbackController"/> whose mode
/// ("endpoint" default, "process", "auto") comes from settings. A 2 s watchdog
/// supervises both tracks, restarts dead ones, reopens a track that could not be
/// opened at start, raises <see cref="TrackFailed"/> and refreshes <see cref="HealthCode"/>.
/// See docs/contract/audio.md.
/// </summary>
public sealed class AudioCaptureService : IAudioCaptureService
{
    private readonly string _captureMode;
    private readonly Func<bool>? _meetingActive;
    private readonly Action<string>? _notify;
    private readonly string? _micDeviceId;
    private readonly string? _loopbackDeviceId;

    private volatile NAudioTrackRecorder? _mic;
    private SystemLoopbackController? _sys;
    private Thread? _watchdog;
    private volatile bool _watchdogStop;
    private readonly Dictionary<string, int> _restartSeen = new();
    private bool _running;
    private bool _physical;
    private string _trackDir = "";   // the session's hidden .echo\ (SessionLayout)
    private CaptureClock _clock;

    // Mic reopen (no mic at start): guarded against a concurrent StopAsync.
    private readonly object _trackGate = new();
    private bool _stopping;
    private double _lastMicReopen = double.NegativeInfinity;

    private volatile string _health = "";

    public event EventHandler<string>? TrackFailed;

    /// <summary>Live capture health ("" | "loopback_dead" | "no_mic" | "silent_teams"),
    /// refreshed by the watchdog every 2 s. A plain volatile read: cheap, thread-safe.</summary>
    public string HealthCode => _health;

    /// <summary>Default constructor: endpoint capture mode (the shipped default).</summary>
    public AudioCaptureService() : this("endpoint") { }

    /// <summary>Reads the capture mode from <see cref="AppSettings.CaptureMode"/>.</summary>
    public AudioCaptureService(AppSettings settings,
        Func<bool>? meetingActive = null, Action<string>? notify = null)
        : this(settings.CaptureMode, meetingActive, notify) { }

    public AudioCaptureService(string captureMode,
        Func<bool>? meetingActive = null, Action<string>? notify = null,
        string? micDeviceId = null, string? loopbackDeviceId = null)
    {
        _captureMode = string.IsNullOrWhiteSpace(captureMode) ? "endpoint" : captureMode;
        _meetingActive = meetingActive;
        _notify = notify;
        _micDeviceId = micDeviceId;
        _loopbackDeviceId = loopbackDeviceId;
    }

    public Task StartAsync(string sessionDir, bool physical, CaptureClock clock, CancellationToken ct)
    {
        if (_running) throw new InvalidOperationException("Capture already started.");
        Directory.CreateDirectory(sessionDir);
        _trackDir = Echo.Core.Storage.SessionLayout.EnsureHidden(sessionDir);
        _clock = clock;
        _physical = physical;
        _stopping = false;
        // Diagnostic: the effective capture mode at record time (2026-07-17: process-loopback
        // ran although settings said "endpoint"). Goes to echo.log, not a toast.
        EmitEvent($"[Echo.Audio] capture mode = {_captureMode}, physical = {physical}");

        _mic = OpenMicResilient(quiet: false, out var micError);
        if (_mic is null)
        {
            // Physical meeting = mic only: no mic is a real start failure. Otherwise the
            // participants (system track) are still worth recording: carry on, flag the
            // health as no_mic and keep retrying from the watchdog.
            if (physical)
                throw micError ?? new InvalidOperationException("Aucun micro utilisable.");
            EmitEvent($"[Echo.Audio] mic: aucun micro ouvrable ({micError?.Message ?? "?"}), "
                      + "enregistrement de la piste systeme seule, nouvelle tentative toutes les "
                      + $"{AudioConstants.ReopenIntervalSeconds:0} s.");
            _lastMicReopen = NowPerfSeconds();
        }

        if (!physical)
        {
            // System track is tolerant: no inner recorder → mic-only for now, the
            // controller retries from the watchdog.
            var sys = new SystemLoopbackController(clock, _trackDir, _captureMode,
                _meetingActive, EmitEvent, _loopbackDeviceId);
            sys.Start();
            _sys = sys;
        }

        _restartSeen["mic"] = 0;
        _restartSeen["system"] = 0;
        _running = true;
        _health = "";
        StartWatchdog();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Opens the mic in this order: the device chosen by the user (if any) → default
    /// Communications endpoint (the one Teams uses) → default Console endpoint → first
    /// active capture device. Returns null when none opens (last error in
    /// <paramref name="lastError"/>) and removes the empty mic.wav left behind.
    /// </summary>
    private NAudioTrackRecorder? OpenMicResilient(bool quiet, out Exception? lastError)
    {
        lastError = null;
        var attempts = new List<(string? Id, Role Role)>();
        if (!string.IsNullOrEmpty(_micDeviceId)) attempts.Add((_micDeviceId, Role.Console));
        attempts.Add((null, Role.Communications));
        attempts.Add((null, Role.Console));
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var e = new MMDeviceEnumerator();
            foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                attempts.Add((d.ID, Role.Console));
        }
        catch { /* enumeration best-effort */ }

        string wav = Path.Combine(_trackDir, "mic.wav");
        string json = Path.Combine(_trackDir, "mic.json");
        foreach (var (id, role) in attempts)
        {
            string? key = id ?? DefaultCaptureId(role);
            if (key is not null && !tried.Add(key)) continue; // same device as a failed attempt
            var rec = new NAudioTrackRecorder("mic", TrackKind.Mic, wav, json, _clock, id, EmitEvent, role);
            try
            {
                rec.Start();
                if (!quiet && (id is null ? role != Role.Communications : id != _micDeviceId))
                    EmitEvent($"[Echo.Audio] mic: ouvert sur un peripherique de repli ({rec.DeviceName}).");
                return rec;
            }
            catch (Exception ex)
            {
                // Start() already released the WAV handle on failure.
                lastError = ex;
            }
        }
        try { if (File.Exists(wav) && new FileInfo(wav).Length <= 44) File.Delete(wav); } catch { /* ignore */ }
        return null;
    }

    private static string? DefaultCaptureId(Role role)
    {
        try
        {
            using var e = new MMDeviceEnumerator();
            return e.GetDefaultAudioEndpoint(DataFlow.Capture, role).ID;
        }
        catch
        {
            return null;
        }
    }

    public Task<IReadOnlyList<TrackResult>> StopAsync()
    {
        StopWatchdog();
        lock (_trackGate) _stopping = true;

        var results = new List<TrackResult>();
        var mic = _mic;
        if (mic is not null)
        {
            try { mic.Stop(); } catch { /* ignore */ }
            results.Add(mic.ToResult());
        }
        if (_sys is not null)
        {
            try { _sys.Stop(); } catch { /* ignore */ }
            if (_sys.HasInner) results.Add(_sys.ToResult());
        }
        _running = false;
        _health = "";
        return Task.FromResult<IReadOnlyList<TrackResult>>(results);
    }

    public (double Mic, double System) GetLevels() =>
        (_mic?.LastRms ?? 0.0, _sys?.LastRms ?? 0.0);

    // ---- watchdog -------------------------------------------------------------
    private void StartWatchdog()
    {
        _watchdogStop = false;
        _watchdog = new Thread(WatchdogLoop) { IsBackground = true, Name = "EchoAudioWatchdog" };
        _watchdog.Start();
    }

    private void StopWatchdog()
    {
        _watchdogStop = true;
        try { _watchdog?.Join(TimeSpan.FromSeconds(3)); } catch { /* ignore */ }
        _watchdog = null;
    }

    private static double NowPerfSeconds() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private void WatchdogLoop()
    {
        double startWall = ClockUtil.NowWallEpoch();
        double lastInaudibleNotify = 0.0;

        while (!_watchdogStop)
        {
            if (_mic is null && !_physical) TryReopenMic();
            TickAndReport(_mic);
            TickAndReport(_sys);

            // "Participants inaudible": system alive (t0 set) but no real signal for
            // > 60 s. Feeds the health banner; the toast is re-notified at most every 5 min.
            bool inaudible = false;
            if (_sys is not null && _sys.HasInner)
            {
                try
                {
                    double nowWall = ClockUtil.NowWallEpoch();
                    if (nowWall - startWall > AudioConstants.InaudibleAfterSeconds)
                    {
                        var la = _sys.LastAudibleWall;
                        bool alive = _sys.T0Stamp is not null;
                        inaudible = alive && (la is null || nowWall - la > AudioConstants.InaudibleAfterSeconds);
                        if (_notify is not null && inaudible
                            && nowWall - lastInaudibleNotify > AudioConstants.ReNotifySeconds)
                        {
                            lastInaudibleNotify = nowWall;
                            _notify("Son des participants inaudible depuis plus de 60 s. " +
                                    "Verifiez la sortie audio ou le peripherique utilise par Teams.");
                        }
                    }
                }
                catch { /* alert failures must not stop supervision */ }
            }

            // NOTE: deliberately NO "mic inaudible" alert. The user often attends a
            // whole meeting without speaking, so a voice-based toast is pure noise
            // (explicit user request 2026-07-13). The REAL failure seen live — a mic
            // delivering ZERO FRAMES (stuck device) — is frame-based, not RMS-based,
            // and is handled silently by the never-data restart in the track watchdog
            // (a merely-quiet mic still delivers its noise floor, so it never trips).
            // The no_mic health code below is frame-based too.
            try
            {
                _health = EvaluateHealth(_physical, Liveness(_mic),
                    _sys is { HasInner: true } ? Liveness(_sys) : null, NowPerfSeconds(), inaudible);
            }
            catch { /* health is advisory */ }

            for (int i = 0; i < (int)(AudioConstants.WatchdogIntervalSeconds * 10) && !_watchdogStop; i++)
                Thread.Sleep(100);
        }
    }

    // No mic at start (non-physical session): quiet reopen every ReopenIntervalSeconds.
    private void TryReopenMic()
    {
        double now = NowPerfSeconds();
        if (now - _lastMicReopen < AudioConstants.ReopenIntervalSeconds) return;
        _lastMicReopen = now;
        NAudioTrackRecorder? rec;
        try { rec = OpenMicResilient(quiet: true, out _); }
        catch { return; }
        if (rec is null) return;
        bool keep;
        lock (_trackGate)
        {
            keep = !_stopping;
            if (keep) _mic = rec;
        }
        if (!keep) { try { rec.Stop(); } catch { /* ignore */ } return; }
        EmitEvent($"[Echo.Audio] mic: micro ouvert apres nouvelle tentative ({rec.DeviceName}).");
    }

    private static TrackLiveness? Liveness(ITrack? t) =>
        t is null ? null : new TrackLiveness(t.LastDataPerfSeconds, t.OpenedPerfSeconds, t.ContinuousStream);

    /// <summary>Liveness snapshot of one track (perf-counter seconds).</summary>
    internal readonly record struct TrackLiveness(double? LastDataPerf, double OpenedPerf, bool Continuous);

    /// <summary>
    /// Pure health rule. Only real failures, all FRAME-based (never the mic level: the
    /// user often listens without speaking):
    ///  - "loopback_dead": non-physical session whose system track could not be opened,
    ///    or delivered no frame for 20 s (the keep-alive makes the endpoint stream
    ///    continuous, so no frame = failure; 120 s for a stream that may pause);
    ///  - "no_mic": mic absent, or no frame for 20 s;
    ///  - "silent_teams": the existing "participants inaudible" condition;
    ///  - "" otherwise.
    /// </summary>
    internal static string EvaluateHealth(bool physical, TrackLiveness? mic, TrackLiveness? system,
        double nowPerf, bool participantsInaudible)
    {
        if (!physical && (system is null || Stalled(system.Value, nowPerf, AudioConstants.LoopbackDeadSeconds)))
            return "loopback_dead";
        if (mic is null || Stalled(mic.Value, nowPerf, AudioConstants.MicDeadSeconds))
            return "no_mic";
        if (!physical && participantsInaudible)
            return "silent_teams";
        return "";
    }

    private static bool Stalled(TrackLiveness t, double nowPerf, double limitSeconds)
    {
        double reference = t.LastDataPerf ?? t.OpenedPerf;
        double limit = t.Continuous ? limitSeconds : AudioConstants.NonContinuousDeadSeconds;
        return nowPerf - reference > limit;
    }

    private void TickAndReport(ITrack? track)
    {
        if (track is null) return;
        double now = NowPerfSeconds();
        try { track.WatchdogTick(now); }
        catch { /* per-track tick failure isolated */ }

        int seen = _restartSeen.TryGetValue(track.Label, out var v) ? v : 0;
        if (track.RestartCount > seen)
        {
            _restartSeen[track.Label] = track.RestartCount;
            try { TrackFailed?.Invoke(this, track.Label); } catch { /* ignore */ }
        }
        else if (track.RestartCount < seen)
        {
            // A reopened track starts counting from zero again.
            _restartSeen[track.Label] = track.RestartCount;
        }
    }

    private void EmitEvent(string msg)
    {
        // System controller events (mode start, auto-fallback) surface via notify.
        try { _notify?.Invoke(msg); } catch { /* ignore */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_running)
        {
            try { await StopAsync().ConfigureAwait(false); }
            catch { /* ignore */ }
        }
        else
        {
            StopWatchdog();
        }
    }
}
