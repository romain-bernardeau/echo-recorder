using System.Diagnostics;
using Echo.Core.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Echo.Audio.Internal;

/// <summary>An opened (not yet started) capture and the device it belongs to.</summary>
internal sealed record CaptureEndpoint(IWaveIn Capture, string DeviceId, string DeviceName);

/// <summary>
/// Mic (<see cref="WasapiCapture"/>) or endpoint-loopback (<see cref="WasapiLoopbackCapture"/>)
/// track. Writes 16-bit PCM WAV + JSON sidecar, stamps the t0 anchor on the first frame,
/// meters peak/RMS, and self-heals via a watchdog (device-change switch, dead-stream
/// restart) padding silence to keep the perf clock — a port of capture.py TrackRecorder.
/// The WAV format (rate, channels) is fixed by the FIRST device; any later device is
/// converted to it (<see cref="CaptureConverter"/>: channel up/down-mix + resampling).
/// Both tracks fill device dropouts with silence LIVE, in the data callback, so the WAV
/// never lags the clock by more than a fraction of a second (loopback: endpoint idle in
/// digital silence; mic: device stall of a second or more). Loopback tracks also keep
/// the render endpoint awake by playing silence on the same device, so the audio engine
/// keeps delivering frames even when no app is playing.
/// Locking rule: <c>_lock</c> guards the writer and the capture slot only. A capture is
/// DETACHED under the lock and stopped/disposed OUTSIDE it (NAudio's Dispose joins the
/// capture thread, which may be waiting on <c>_lock</c> inside <see cref="OnData"/>), and
/// callbacks from a detached capture are ignored.
/// </summary>
internal sealed class NAudioTrackRecorder : TrackRecorderBase
{
    private readonly object _lock = new();
    private readonly double _stallLimit;
    private readonly string? _preferredDeviceId;
    private readonly Role _defaultRole;
    private readonly Action<string>? _log;
    // True when the track opened on the *default* endpoint (no pinned device). A pinned
    // device is never switched back to default by the watchdog (audit F2).
    private readonly bool _openedOnDefault;
    private readonly Func<string?, CaptureEndpoint> _openCapture;
    private readonly Func<string?> _defaultDeviceId;

    private IWaveIn? _capture;
    private CaptureConverter? _converter;
    private WaveFileWriter? _writer;
    private volatile string? _deviceId;
    private bool _stopped;
    // Reused per-callback conversion buffer (grow-on-demand) — no per-callback alloc (audit F6).
    private byte[] _scratch = Array.Empty<byte>();
    // Reused 1 s chunk of zeros for silence padding (sized once Rate/Channels are known).
    private byte[] _zero = Array.Empty<byte>();

    // Loopback keep-alive: silent WasapiOut on the captured render device.
    private WasapiOut? _keepAlive;

    // Live gap fill state.
    private long _lastCallbackPerfNs;
    private double _lastGapFillLog = double.NegativeInfinity; // throttle (perf seconds)

    // watchdog state
    private long _wdLastFrames;
    private double _wdLastAdvance;
    private double _wdLastRestart = double.NegativeInfinity;
    private bool _healthySeen;
    private int _neverDataRestarts;                          // mic "never delivered" restarts used

    // Residual loopback drift, aggregated (was one log line every 30 s, 11 % of echo.log).
    private double _lastDriftLog = double.NegativeInfinity;
    private double _driftWindowMax;
    private double _driftSessionMax;
    private int _driftChecks;

    public NAudioTrackRecorder(string label, TrackKind kind, string wavPath, string jsonPath,
        CaptureClock clock, string? preferredDeviceId = null, Action<string>? log = null,
        Role? defaultRole = null,
        Func<string?, CaptureEndpoint>? openCapture = null, Func<string?>? defaultDeviceId = null)
        : base(label, kind, wavPath, jsonPath, clock)
    {
        _preferredDeviceId = preferredDeviceId;
        _openedOnDefault = string.IsNullOrEmpty(preferredDeviceId);
        _log = log;
        _defaultRole = defaultRole ?? (kind == TrackKind.Loopback ? Role.Multimedia : Role.Console);
        _openCapture = openCapture ?? OpenDevice;
        _defaultDeviceId = defaultDeviceId ?? DefaultDeviceId;
        _stallLimit = kind == TrackKind.Mic
            ? AudioConstants.StallLimitMicSeconds
            : AudioConstants.StallLimitLoopbackSeconds;
    }

    /// <summary>Mic: always continuous. Loopback: continuous while the keep-alive plays.</summary>
    public override bool ContinuousStream => Kind == TrackKind.Mic || Volatile.Read(ref _keepAlive) is not null;

    private MMDevice ResolveDevice(MMDeviceEnumerator e, string? id)
    {
        if (!string.IsNullOrEmpty(id))
        {
            try { return e.GetDevice(id); }
            catch { /* fall through to default */ }
        }
        return e.GetDefaultAudioEndpoint(Kind == TrackKind.Loopback ? DataFlow.Render : DataFlow.Capture, _defaultRole);
    }

    private CaptureEndpoint OpenDevice(string? id)
    {
        using var e = new MMDeviceEnumerator();
        var dev = ResolveDevice(e, id);
        IWaveIn cap = Kind == TrackKind.Loopback ? new WasapiLoopbackCapture(dev) : new WasapiCapture(dev);
        return new CaptureEndpoint(cap, dev.ID, dev.FriendlyName);
    }

    private string? DefaultDeviceId()
    {
        try
        {
            using var e = new MMDeviceEnumerator();
            return e.GetDefaultAudioEndpoint(Kind == TrackKind.Loopback ? DataFlow.Render : DataFlow.Capture, _defaultRole).ID;
        }
        catch
        {
            return null;
        }
    }

    // Any readable mix format is fine now that every device is converted to the WAV format.
    private static bool DeviceUsable(string deviceId)
    {
        try
        {
            using var e = new MMDeviceEnumerator();
            var mix = e.GetDevice(deviceId).AudioClient.MixFormat;
            return mix.SampleRate > 0 && mix.Channels > 0;
        }
        catch
        {
            return false;
        }
    }

    public override void Start()
    {
        CaptureEndpoint? ep = null;
        try
        {
            ep = _openCapture(_preferredDeviceId);
            var fmt = ep.Capture.WaveFormat;
            if (fmt.SampleRate <= 0) throw new InvalidOperationException($"invalid capture format ({fmt})");
            _deviceId = ep.DeviceId;
            DeviceName = ep.DeviceName;

            // The device's native rate/channels fix the WAV format for the whole session;
            // the WAV is always 16-bit.
            Rate = fmt.SampleRate;
            Channels = Math.Max(1, fmt.Channels);
            _zero = new byte[Rate * Channels * (AudioConstants.Bits / 8)]; // 1 s chunk

            _writer = new WaveFileWriter(WavPath, new WaveFormat(Rate, AudioConstants.Bits, Channels));
            lock (_lock) Attach(ep.Capture);
            ep.Capture.StartRecording();

            StartKeepAlive(fmt);

            MarkOpened();
            _wdLastAdvance = NowSeconds();
            WriteSidecar(false);
        }
        catch
        {
            // Release the WAV handle so a resilient caller can delete/retry.
            IWaveIn? attached;
            WasapiOut? ka;
            lock (_lock)
            {
                attached = Detach();
                ka = DetachKeepAlive();
            }
            ReleaseKeepAlive(ka);
            ReleaseCapture(attached ?? ep?.Capture);
            try { _writer?.Dispose(); } catch { /* ignore */ }
            _writer = null;
            throw;
        }
    }

    private static double NowSeconds() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    // Caller holds _lock.
    private void Attach(IWaveIn cap)
    {
        _capture = cap;
        _converter = new CaptureConverter(cap.WaveFormat, Rate, Channels);
        cap.DataAvailable += OnData;
        cap.RecordingStopped += OnStopped;
    }

    // Caller holds _lock. Empties the capture slot; the caller releases the returned
    // capture OUTSIDE the lock (ReleaseCapture).
    private IWaveIn? Detach()
    {
        var cap = _capture;
        _capture = null;
        _converter = null;
        if (cap is not null)
        {
            cap.DataAvailable -= OnData;
            cap.RecordingStopped -= OnStopped;
        }
        return cap;
    }

    // NEVER called under _lock. StopRecording + Dispose (which joins the capture thread)
    // on a worker, bounded: a driver stuck in a call must not freeze the watchdog, nor
    // the UI thread through Stop(). An abandoned capture is detached already, so its
    // late callbacks are ignored.
    private void ReleaseCapture(IWaveIn? cap)
    {
        if (cap is null) return;
        var t = Task.Run(() =>
        {
            try { cap.StopRecording(); } catch { /* ignore */ }
            try { cap.Dispose(); } catch { /* ignore */ }
        });
        bool done;
        try { done = t.Wait(TimeSpan.FromSeconds(AudioConstants.CaptureReleaseTimeoutSeconds)); }
        catch { done = true; }
        if (!done)
            _log?.Invoke($"[Echo.Audio] {Label}: liberation de la capture bloquee (> {AudioConstants.CaptureReleaseTimeoutSeconds:0} s), "
                         + "abandonnee en arriere-plan.");
    }

    // Loopback only. WasapiLoopbackCapture is fed by the render engine, which goes idle
    // (no frames at all) when nothing is playing. A silent shared-mode stream on the SAME
    // device keeps the engine mixing, so frames arrive continuously and the live gap fill
    // below rarely has anything to do. Best-effort: on any failure we log once and rely on
    // the gap fill alone. Never called under _lock.
    private void StartKeepAlive(WaveFormat captureFormat)
    {
        if (Kind != TrackKind.Loopback) return;
        WasapiOut? outp = null;
        try
        {
            using var e = new MMDeviceEnumerator();
            var dev = ResolveDevice(e, _deviceId);
            // Shared mode, event-driven, ~100 ms latency. The silence uses the capture's
            // own format (= the device mix format) so no resampler is involved.
            outp = new WasapiOut(dev, AudioClientShareMode.Shared, true, 100);
            outp.Init(new SilenceProvider(captureFormat));
            outp.Play();
        }
        catch (Exception ex)
        {
            ReleaseKeepAlive(outp);
            _log?.Invoke($"[Echo.Audio] {Label}: maintien de l'endpoint impossible ({ex.GetType().Name}: {ex.Message}), "
                         + "comblement des trous en direct seul.");
            return;
        }

        WasapiOut? previous;
        lock (_lock)
        {
            if (_stopped) { previous = outp; }
            else { previous = _keepAlive; _keepAlive = outp; }
        }
        ReleaseKeepAlive(previous);
    }

    // Caller holds _lock.
    private WasapiOut? DetachKeepAlive()
    {
        var ka = _keepAlive;
        _keepAlive = null;
        return ka;
    }

    // NEVER called under _lock. WasapiOut.Stop joins its playback thread: bounded too.
    private static void ReleaseKeepAlive(WasapiOut? ka)
    {
        if (ka is null) return;
        var t = Task.Run(() =>
        {
            try { ka.Stop(); } catch { /* ignore */ }
            try { ka.Dispose(); } catch { /* ignore */ }
        });
        try { t.Wait(TimeSpan.FromSeconds(AudioConstants.CaptureReleaseTimeoutSeconds)); } catch { /* ignore */ }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;
        lock (_lock)
        {
            // A detached capture (restart, stop) may still be finishing a callback.
            if (_stopped || _writer is null || _converter is null || !ReferenceEquals(sender, _capture)) return;

            int nbytes = _converter.Convert(e.Buffer.AsSpan(0, e.BytesRecorded), ref _scratch);
            MarkData();
            if (nbytes <= 0) return;
            int bytesPerFrame = Channels * (AudioConstants.Bits / 8);
            int frames = nbytes / bytesPerFrame;
            long nowNs = ClockUtil.NowPerfNs();
            if (T0Stamp is null)
            {
                T0Stamp = new T0(ClockUtil.NowWallEpoch(), nowNs);
                _lastCallbackPerfNs = nowNs;
            }

            // Live gap fill: if the device went quiet for a while and the file is behind
            // the perf clock, insert the missing silence BEFORE this buffer.
            if (Rate > 0)
            {
                // Frames the file should hold before appending this buffer.
                double expected = (nowNs - T0Stamp.Value.PerfNs) / 1e9 * Rate - frames;
                double sinceLast = (nowNs - _lastCallbackPerfNs) / 1e9;
                int pad = GapFill.FramesToPad(expected, FramesWritten, sinceLast, Rate, GapFill.MinGapSeconds(Kind));
                if (pad > 0)
                {
                    int padded = PadSilence(pad);
                    double seconds = (double)padded / Rate;
                    TotalGapSeconds += seconds;
                    double nowSec = nowNs / 1e9;
                    if (_log is not null && (nowSec - _lastGapFillLog) > 30.0)
                    {
                        _lastGapFillLog = nowSec;
                        string who = Kind == TrackKind.Loopback ? "l'endpoint" : "le micro";
                        _log.Invoke($"[Echo.Audio] {Label}: trou de {seconds:0.0}s comble en direct ({who} a cesse d'emettre)");
                    }
                }
            }
            _lastCallbackPerfNs = nowNs;

            var (peak, rms) = LevelMeter.Measure(_scratch.AsSpan(0, nbytes));
            LastLevel = peak;
            LastRms = rms;
            if (rms >= AudioConstants.AudibleThreshold)
            {
                LastAudibleWall = ClockUtil.NowWallEpoch();
                GotAudible = true;
            }

            try
            {
                _writer.Write(_scratch, 0, nbytes);
                FramesWritten += frames;
            }
            catch (Exception ex)
            {
                Error = ex.GetType().Name + ": " + ex.Message;
            }
        }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null) return;
        lock (_lock)
        {
            // A late stop event of a detached capture must not kill its replacement.
            if (!ReferenceEquals(sender, _capture)) return;
            Error ??= e.Exception.GetType().Name + ": " + e.Exception.Message;
        }
    }

    public override void WatchdogTick(double nowPerfSeconds)
    {
        // Device queries are COM calls: done OUTSIDE the lock, the data callback must
        // never wait on them.
        // Default-device change (headset<->speakers, dock…) → switch. ONLY when this
        // track was opened on the default endpoint: a user-pinned device must never be
        // dragged back to the default by the watchdog (audit F2).
        string? switchCandidate = null;
        if (_openedOnDefault)
        {
            string? newId = _defaultDeviceId();
            string? current = _deviceId;
            if (newId is not null && newId != current && DeviceUsable(newId))
                switchCandidate = newId;
        }

        string? reason = null;
        string? switchTo = null;
        bool restart = false;
        lock (_lock)
        {
            if (_writer is null || _stopped) return;

            if (FramesWritten > _wdLastFrames)
            {
                _wdLastFrames = FramesWritten;
                _wdLastAdvance = nowPerfSeconds;
                _healthySeen = true;
            }

            bool dead = false;

            if (Error is not null)
            {
                dead = true;
                reason = $"erreur callback ({Error})";
            }

            if (switchCandidate is not null && switchCandidate != _deviceId)
            {
                dead = true;
                reason = $"peripherique par defaut change ({_deviceId}->{switchCandidate})";
                switchTo = switchCandidate;
            }

            // Prolonged stall, but only after the track was seen healthy (never
            // restarts a merely-silent loopback at session start).
            if (!dead && _healthySeen && (nowPerfSeconds - _wdLastAdvance) > _stallLimit)
            {
                dead = true;
                reason = $"aucune donnee depuis {nowPerfSeconds - _wdLastAdvance:0}s";
            }

            // Mic that NEVER delivered a single frame: the _healthySeen guard above
            // (needed for silence-prone loopback) would wait forever — seen live: a
            // 95-min meeting with mic.wav at 0 byte and no restart. A mic normally
            // delivers frames even during silence, so no data at all = stuck device.
            if (!dead && !_healthySeen && Kind == TrackKind.Mic && FramesWritten == 0
                && _neverDataRestarts < AudioConstants.MicNeverDataMaxRestarts
                && (nowPerfSeconds - _wdLastAdvance) > AudioConstants.MicNeverDataSeconds)
            {
                dead = true;
                reason = "aucune donnee du micro depuis le demarrage";
                _neverDataRestarts++;
            }

            if (dead && (nowPerfSeconds - _wdLastRestart) > AudioConstants.MinRestartIntervalSeconds)
            {
                _wdLastRestart = nowPerfSeconds;
                restart = true;
            }
            else if (!dead)
            {
                DetectSilenceGap(nowPerfSeconds);
            }
        }

        if (restart) Restart(nowPerfSeconds, reason, switchTo);
    }

    // NOT under _lock (takes it only for the short state changes).
    private void Restart(double now, string? reason, string? switchTo)
    {
        // 1) Detach under the lock: from here on, the old capture's callbacks are ignored.
        //    Stop/dispose it OUTSIDE the lock (Dispose joins its capture thread, which may
        //    be blocked on _lock in OnData: the former watchdog deadlock).
        IWaveIn? old;
        WasapiOut? oldKa;
        string? currentId;
        lock (_lock)
        {
            if (_stopped || _writer is null) return;
            old = Detach();
            oldKa = DetachKeepAlive();
            Error = null;
            currentId = _deviceId;
        }
        ReleaseKeepAlive(oldKa);
        ReleaseCapture(old);

        // 2) Open the replacement, still outside the lock (device activation = COM).
        //    Any format is accepted: CaptureConverter maps it to the WAV's fixed format.
        CaptureEndpoint? ep = null;
        foreach (var id in new[] { switchTo ?? currentId, currentId }.Distinct())
        {
            try
            {
                ep = _openCapture(id);
                if (ep.Capture.WaveFormat.SampleRate > 0) break;
                ReleaseCapture(ep.Capture);
                ep = null;
            }
            catch
            {
                ep = null; // try next candidate
            }
        }

        // 3) Pad the hole and attach, under the lock.
        bool attached = false;
        lock (_lock)
        {
            if (!_stopped && _writer is not null)
            {
                // Pad up to the high-resolution clock (expected frames since t0 minus frames
                // written), not "now - last advance": the stall detector only sees the hole
                // seconds late, and the device clock drifts against the perf counter.
                int padded = PadSilence(ClockDeficitFrames(AudioConstants.RestartMinPadSeconds));
                TotalGapSeconds += Rate > 0 ? (double)padded / Rate : 0.0;

                if (ep is not null)
                {
                    Attach(ep.Capture);
                    _deviceId = ep.DeviceId;
                    DeviceName = ep.DeviceName;
                    attached = true;
                }
                _wdLastFrames = FramesWritten;
                _wdLastAdvance = now;
                // The first callback after a restart must not be read as a dropout: the
                // file has just been padded up to "now".
                _lastCallbackPerfNs = ClockUtil.NowPerfNs();
                RestartCount++;
                LastReason = reason;
            }
        }
        if (ep is not null && !attached)
        {
            ReleaseCapture(ep.Capture); // stopped meanwhile
            return;
        }

        bool started = false;
        if (attached)
        {
            var fmt = ep!.Capture.WaveFormat;
            if (fmt.SampleRate != Rate || fmt.Channels != Channels)
                _log?.Invoke($"[Echo.Audio] {Label}: peripherique en {fmt.SampleRate} Hz / {fmt.Channels} canal(aux), "
                             + $"converti vers le format du WAV ({Rate} Hz / {Channels}).");
            try
            {
                ep.Capture.StartRecording();
                started = true;
            }
            catch (Exception ex)
            {
                IWaveIn? failed = null;
                lock (_lock)
                {
                    if (ReferenceEquals(_capture, ep.Capture)) failed = Detach();
                }
                ReleaseCapture(failed ?? ep.Capture);
                _log?.Invoke($"[Echo.Audio] {Label}: demarrage de la capture impossible ({ex.GetType().Name}: {ex.Message}).");
            }
        }
        if (started) StartKeepAlive(ep!.Capture.WaveFormat);

        lock (_lock)
        {
            if (!_stopped) WriteSidecar(false);
        }
        if (!started)
            _log?.Invoke($"[Echo.Audio] {Label}: relance impossible "
                         + "(peripherique indisponible), nouvelle tentative au prochain tick.");
    }

    // Caller holds _lock. Frames missing from the file against the perf clock since t0,
    // or 0 when the deficit is at most minSeconds (buffer latency / rounding).
    private int ClockDeficitFrames(double minSeconds)
    {
        if (T0Stamp is null || Rate <= 0) return 0;
        double deficit = (ClockUtil.NowPerfNs() - T0Stamp.Value.PerfNs) / 1e9 * Rate - FramesWritten;
        if (deficit <= minSeconds * Rate) return 0;
        return SecondsToFrames(deficit / Rate);
    }

    // Caller holds _lock. F5: WasapiLoopbackCapture can stop delivering frames during
    // digital silence. Holes are filled live in OnData (GapFill) as soon as frames
    // resume, and the tail is padded on Stop. This detector only reports the RESIDUAL
    // drift (endpoint still silent, no callback yet to trigger the fill), AGGREGATED:
    // at most one line per 5 min with the max seen, plus a recap at Stop.
    private void DetectSilenceGap(double nowPerfSeconds)
    {
        if (_log is null || Kind != TrackKind.Loopback || T0Stamp is null || Rate <= 0) return;
        double drift = (ClockUtil.NowPerfNs() - T0Stamp.Value.PerfNs) / 1e9 - DurationSeconds;
        if (drift <= 1.0) return;
        _driftChecks++;
        _driftWindowMax = Math.Max(_driftWindowMax, drift);
        _driftSessionMax = Math.Max(_driftSessionMax, drift);
        if ((nowPerfSeconds - _lastDriftLog) >= AudioConstants.DriftLogWindowSeconds)
        {
            _lastDriftLog = nowPerfSeconds;
            _log.Invoke($"[Echo.Audio] {Label}: derive loopback residuelle max ~{_driftWindowMax:0.0}s " +
                        "(endpoint silencieux par moments ?), comblee a la prochaine trame ou a l'arret. " +
                        "Prochain point dans 5 min au plus.");
            _driftWindowMax = 0.0;
        }
    }

    // Caller holds _lock.
    private void LogDriftRecap()
    {
        if (_log is null || Kind != TrackKind.Loopback) return;
        if (_driftChecks == 0 && TotalGapSeconds <= 0.0) return;
        _log.Invoke($"[Echo.Audio] {Label}: recapitulatif derive loopback : max residuel ~{_driftSessionMax:0.0}s "
                    + $"({_driftChecks} controle(s) au-dessus de 1 s), {TotalGapSeconds:0.0}s de silence comble au total, "
                    + $"{RestartCount} relance(s).");
    }

    // Caller holds _lock. F5: on Stop, bring the WAV up to the clock so it stays aligned
    // with speakers.jsonl when the device stopped delivering (loopback silence, mic stall).
    private void PadTailToClock()
    {
        if (_writer is null) return;
        int padded = PadSilence(ClockDeficitFrames(1.0)); // ignore sub-second rounding noise
        if (Rate > 0) TotalGapSeconds += (double)padded / Rate;
    }

    private int SecondsToFrames(double seconds) =>
        Rate > 0 ? (int)Math.Round(Math.Min(Math.Max(0.0, seconds), AudioConstants.MaxPadSeconds) * Rate) : 0;

    // Caller holds _lock. Writes `frames` frames of silence using the reusable 1 s zero
    // chunk (no allocation on the callback path). Returns the frames actually written.
    private int PadSilence(int frames)
    {
        if (_writer is null || frames <= 0 || Rate <= 0) return 0;
        int bytesPerFrame = Channels * (AudioConstants.Bits / 8);
        if (_zero.Length < Rate * bytesPerFrame) _zero = new byte[Rate * bytesPerFrame];
        int left = frames;
        while (left > 0)
        {
            int n = Math.Min(Rate, left);
            try { _writer.Write(_zero, 0, n * bytesPerFrame); }
            catch { break; }
            FramesWritten += n;
            left -= n;
        }
        return frames - left;
    }

    public override void Stop()
    {
        IWaveIn? cap;
        WasapiOut? ka;
        lock (_lock)
        {
            if (_stopped) return;
            _stopped = true;
            cap = Detach();
            ka = DetachKeepAlive();
        }
        ReleaseKeepAlive(ka);
        ReleaseCapture(cap);
        lock (_lock)
        {
            try { PadTailToClock(); } catch { /* padding is best-effort */ }
            try { LogDriftRecap(); } catch { /* ignore */ }
            try { _writer?.Dispose(); } catch { /* ignore */ }
            _writer = null;
        }
        WriteSidecar(true);
    }
}
