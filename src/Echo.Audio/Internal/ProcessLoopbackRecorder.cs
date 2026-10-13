using System.Runtime.InteropServices;
using Echo.Audio.Interop;
using Echo.Core.Models;
using NAudio.Wave;

namespace Echo.Audio.Internal;

/// <summary>
/// WASAPI process-loopback capture of the Teams process tree (48 kHz/stereo/16-bit,
/// fixed). Faithful port of process_loopback.ProcessLoopbackRecorder: raw COM
/// activation via <see cref="NativeMethods.ActivateAudioInterfaceAsync"/> with an
/// AUDIOCLIENT_ACTIVATION_PARAMS blob, event-driven pull on a dedicated MTA thread
/// (MMCSS "Pro Audio"), multi-PID try order, and dead-PID reactivation preserving
/// the same WAV / t0.
/// </summary>
internal sealed class ProcessLoopbackRecorder : TrackRecorderBase
{
    private readonly List<uint> _pids;
    private WaveFileWriter? _writer;
    private Thread? _thread;
    private volatile bool _stop;
    private volatile int _activePid = -1;      // -1 = none (volatile for cross-thread visibility)
    private volatile int _reactivateTo = -1;   // -1 = none
    private byte[] _scratch = Array.Empty<byte>();

    public ProcessLoopbackRecorder(IEnumerable<uint> pids, string wavPath, string jsonPath,
        CaptureClock clock)
        : base("system", TrackKind.Loopback, wavPath, jsonPath, clock)
    {
        _pids = pids.ToList();
        Rate = AudioConstants.ProcessRate;
        Channels = AudioConstants.ProcessChannels;
        DeviceIndex = -1;
        DeviceName = $"Teams process-loopback (pid {string.Join(",", _pids)})";
    }

    public override void Start()
    {
        _writer = new WaveFileWriter(WavPath, new WaveFormat(Rate, AudioConstants.Bits, Channels));
        MarkOpened();
        WriteSidecar(false);
        _thread = new Thread(Run) { IsBackground = true, Name = "EchoProcessLoopback" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    private static IAudioClient ActivateAudioClient(uint pid, TimeSpan timeout)
    {
        int paramsSize = Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>();
        IntPtr pParams = Marshal.AllocHGlobal(paramsSize);
        IntPtr pPv = Marshal.AllocHGlobal(Marshal.SizeOf<PROPVARIANT_BLOB>());
        var handler = new ActivateCompletionHandler();
        IActivateAudioInterfaceAsyncOperation? op = null;
        try
        {
            Marshal.StructureToPtr(new AUDIOCLIENT_ACTIVATION_PARAMS
            {
                ActivationType = WasapiConstants.AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
                TargetProcessId = pid,
                ProcessLoopbackMode = WasapiConstants.PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE,
            }, pParams, false);

            Marshal.StructureToPtr(new PROPVARIANT_BLOB
            {
                vt = WasapiConstants.VT_BLOB,
                cbSize = (uint)paramsSize,
                pBlobData = pParams,
            }, pPv, false);

            var iid = WasapiConstants.IID_IAudioClient;
            int hr = NativeMethods.ActivateAudioInterfaceAsync(
                WasapiConstants.VirtualAudioDeviceProcessLoopback, ref iid, pPv, handler, out op);
            if (hr != WasapiConstants.S_OK)
                throw new COMException($"ActivateAudioInterfaceAsync HRESULT=0x{hr:X8}", hr);

            if (!handler.Wait(timeout))
                throw new TimeoutException("process-loopback activation callback timeout");

            int callHr = op.GetActivateResult(out int activateResult, out object? punk);
            if (callHr != WasapiConstants.S_OK)
                throw new COMException($"GetActivateResult call HRESULT=0x{callHr:X8}", callHr);
            if (activateResult != WasapiConstants.S_OK || punk is null)
                throw new COMException(
                    $"GetActivateResult activateResult=0x{activateResult:X8} (process renders no audio stream?)",
                    activateResult);

            return (IAudioClient)punk;
        }
        finally
        {
            // Release the activation operation + wait handle every (re)activation (audit F7).
            if (op is not null) { try { Marshal.ReleaseComObject(op); } catch { /* ignore */ } }
            handler.Dispose();
            Marshal.FreeHGlobal(pPv);
            Marshal.FreeHGlobal(pParams);
        }
    }

    private (IAudioClient Client, uint Pid) ActivateFirstWorking(IReadOnlyList<uint> pids)
    {
        Exception? last = null;
        foreach (var pid in pids)
        {
            try { return (ActivateAudioClient(pid, TimeSpan.FromSeconds(5.0)), pid); }
            catch (Exception ex) { last = ex; }
        }
        throw last ?? new InvalidOperationException("No usable Teams PID.");
    }

    private void Run()
    {
        uint mmcssIndex = 0;
        IntPtr mmcss = IntPtr.Zero;
        try { mmcss = NativeMethods.AvSetMmThreadCharacteristicsW("Pro Audio", ref mmcssIndex); }
        catch { /* MMCSS is optional hardening */ }

        try
        {
            // (Re)activation loop: replay activate+capture whenever the watchdog
            // requests reactivation (dead Teams PID replaced). Same WAV / same t0.
            while (!_stop)
            {
                var pids = _reactivateTo >= 0 ? new List<uint> { (uint)_reactivateTo } : _pids.ToList();
                _reactivateTo = -1;
                IAudioClient? client = null;
                IAudioCaptureClient? capture = null;
                IntPtr hev = IntPtr.Zero;
                try
                {
                    (client, var pid) = ActivateFirstWorking(pids);
                    _activePid = (int)pid;

                    var wfx = WAVEFORMATEX.MakePcm(Rate, Channels, AudioConstants.Bits);
                    IntPtr pWfx = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEFORMATEX>());
                    Marshal.StructureToPtr(wfx, pWfx, false);
                    try
                    {
                        uint flags = WasapiConstants.AUDCLNT_STREAMFLAGS_LOOPBACK
                                     | WasapiConstants.AUDCLNT_STREAMFLAGS_EVENTCALLBACK
                                     | WasapiConstants.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
                                     | WasapiConstants.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
                        int hr = client.Initialize(WasapiConstants.AUDCLNT_SHAREMODE_SHARED, flags,
                            WasapiConstants.BufferDuration100Ns, 0, pWfx, IntPtr.Zero);
                        if (hr != WasapiConstants.S_OK)
                            throw new COMException($"IAudioClient.Initialize HRESULT=0x{hr:X8}", hr);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(pWfx);
                    }

                    hev = NativeMethods.CreateEventW(IntPtr.Zero, false, false, null);
                    client.SetEventHandle(hev);
                    var capIid = WasapiConstants.IID_IAudioCaptureClient;
                    int gsHr = client.GetService(ref capIid, out object? capObj);
                    if (gsHr != WasapiConstants.S_OK || capObj is null)
                        throw new COMException($"IAudioClient.GetService HRESULT=0x{gsHr:X8}", gsHr);
                    capture = (IAudioCaptureClient)capObj;
                    client.Start();
                    Loop(hev, capture);
                }
                finally
                {
                    try { client?.Stop(); } catch { /* ignore */ }
                    // CloseHandle every session (HANDLE-leak fix, per Python note).
                    if (hev != IntPtr.Zero) { try { NativeMethods.CloseHandle(hev); } catch { } }
                    // Release the capture-client + client COM objects every session (audit F7).
                    if (capture is not null) { try { Marshal.ReleaseComObject(capture); } catch { } }
                    if (client is not null) { try { Marshal.ReleaseComObject(client); } catch { } }
                }
                if (_stop || _reactivateTo < 0) break;
            }
        }
        catch (Exception ex)
        {
            Error = ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            try { _writer?.Dispose(); } catch { /* ignore */ }
            _writer = null;
            WriteSidecar(true);
            if (mmcss != IntPtr.Zero) { try { NativeMethods.AvRevertMmThreadCharacteristics(mmcss); } catch { } }
        }
    }

    private void Loop(IntPtr hev, IAudioCaptureClient capture)
    {
        int block = Channels * (AudioConstants.Bits / 8);
        while (!_stop && _reactivateTo < 0)
        {
            NativeMethods.WaitForSingleObject(hev, 200);
            while (true)
            {
                if (capture.GetNextPacketSize(out uint nframes) != WasapiConstants.S_OK || nframes == 0)
                    break;
                if (capture.GetBuffer(out IntPtr dataPtr, out uint got, out uint dwflags, out _, out _)
                    != WasapiConstants.S_OK)
                    break;

                int nbytes = (int)got * block;
                bool silent = (dwflags & WasapiConstants.AUDCLNT_BUFFERFLAGS_SILENT) != 0
                              || dataPtr == IntPtr.Zero;

                if (_scratch.Length < nbytes) _scratch = new byte[nbytes];
                if (silent)
                    Array.Clear(_scratch, 0, nbytes);
                else if (nbytes > 0)
                    Marshal.Copy(dataPtr, _scratch, 0, nbytes);

                MarkData();
                if (T0Stamp is null)
                    T0Stamp = new T0(ClockUtil.NowWallEpoch(), ClockUtil.NowPerfNs());

                try
                {
                    _writer!.Write(_scratch, 0, nbytes);
                    FramesWritten += got;
                }
                catch (Exception ex)
                {
                    Error = ex.GetType().Name + ": " + ex.Message;
                    capture.ReleaseBuffer(got);
                    return;
                }

                if (!silent && nbytes > 0)
                {
                    var (peak, rms) = LevelMeter.Measure(_scratch.AsSpan(0, nbytes));
                    LastLevel = peak;
                    LastRms = rms;
                    if (rms >= AudioConstants.AudibleThreshold)
                    {
                        LastAudibleWall = ClockUtil.NowWallEpoch();
                        GotAudible = true;
                    }
                }
                else
                {
                    LastLevel = 0.0;
                }

                capture.ReleaseBuffer(got);
            }
        }
    }

    public override void WatchdogTick(double nowPerfSeconds)
    {
        int active = _activePid;
        if (_stop || active < 0 || _reactivateTo >= 0) return;
        bool alive;
        try { alive = TeamsPidResolver.PidExists((uint)active); }
        catch { alive = true; } // on doubt, don't disturb capture
        if (alive) return;

        List<uint> newpids;
        try { newpids = TeamsPidResolver.TeamsTargetPids().Where(p => p != (uint)active).ToList(); }
        catch { newpids = new List<uint>(); }

        if (newpids.Count > 0)
        {
            _reactivateTo = (int)newpids[0];
            RestartCount++;
            LastReason = $"PID {active} mort -> reactivation sur {newpids[0]}";
        }
        else
        {
            Error ??= $"PID Teams {active} mort, aucun remplacant";
        }
    }

    public override void Stop()
    {
        _stop = true;
        _thread?.Join(TimeSpan.FromSeconds(4));
    }

    protected override IEnumerable<KeyValuePair<string, object?>> BuildSidecar(bool finalized)
    {
        yield return new("label", Label);
        yield return new("kind", "loopback");
        yield return new("wav", Path.GetFileName(WavPath));
        yield return new("device_index", -1);
        yield return new("device_name", DeviceName);
        yield return new("sample_rate", Rate);
        yield return new("channels", Channels);
        yield return new("sample_width_bytes", AudioConstants.Bits / 8);
        yield return new("got_audio", GotAudio);
        yield return new("got_audible", GotAudible);
        yield return new("t0_wall_epoch", T0Stamp?.WallEpoch);
        yield return new("t0_perf_ns", T0Stamp?.PerfNs);
        yield return new("session_wall0_epoch", ClockUtil.SessionWall0Epoch(Clock));
        yield return new("session_perf0_ns", ClockUtil.SessionPerf0Ns(Clock));
        yield return new("frames_written", FramesWritten);
        yield return new("duration_s", DurationSeconds);
        yield return new("restart_count", RestartCount);
        yield return new("total_gap_s", Math.Round(TotalGapSeconds, 2));
        yield return new("last_restart_reason", LastReason);
        yield return new("source", "process_loopback");
        yield return new("finalized", finalized);
    }
}
