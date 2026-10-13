using System.Diagnostics;
using Echo.Audio.Internal;
using Echo.Core.Models;
using NAudio.Wave;

namespace Echo.Tests;

/// <summary>
/// <see cref="NAudioTrackRecorder"/> driven by a fake <see cref="IWaveIn"/> (no real audio
/// device, temp folder only). Covers the 2026-10 fixes: no deadlock when a capture is
/// released while one of its callbacks is in flight, live gap fill on the mic, restart
/// pad measured on the perf clock, and a device format change after restart.
/// </summary>
public class AudioRecorderTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("echo-rec-");

    public void Dispose()
    {
        try { _dir.Delete(true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Fake capture mimicking NAudio's WasapiCapture threading: StopRecording lets an
    /// already-dispatched callback run on the "capture thread", and Dispose JOINS that
    /// thread (NAudio joins without timeout; here 10 s so a regression fails, not hangs).
    /// </summary>
    private sealed class FakeWaveIn : IWaveIn
    {
        private EventHandler<WaveInEventArgs>? _inFlight;
        private Thread? _captureThread;

        public FakeWaveIn(WaveFormat format) { WaveFormat = format; }

        public WaveFormat WaveFormat { get; set; }
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public bool Started { get; private set; }
        public bool DisposedCleanly { get; private set; }

        public void StartRecording() => Started = true;

        /// <summary>Snapshot the current subscribers: a callback already dispatched by
        /// the capture thread, delivered when StopRecording is called.</summary>
        public void ArmInFlightCallback() => _inFlight = DataAvailable;

        public void StopRecording()
        {
            var handler = _inFlight;
            if (handler is null) return;
            _captureThread = new Thread(() =>
            {
                var buf = new byte[WaveFormat.AverageBytesPerSecond / 100];
                handler(this, new WaveInEventArgs(buf, buf.Length));
            }) { IsBackground = true };
            _captureThread.Start();
            Thread.Sleep(50); // let the callback reach OnData
        }

        public void Dispose() => DisposedCleanly = _captureThread?.Join(TimeSpan.FromSeconds(10)) ?? true;

        public void Push(int frames) =>
            DataAvailable?.Invoke(this, new WaveInEventArgs(new byte[frames * WaveFormat.BlockAlign], frames * WaveFormat.BlockAlign));

        public void Fail() =>
            RecordingStopped?.Invoke(this, new StoppedEventArgs(new IOException("device lost")));
    }

    private NAudioTrackRecorder NewMic(Queue<FakeWaveIn> devices) =>
        new("mic", TrackKind.Mic, Path.Combine(_dir.FullName, "mic.wav"), Path.Combine(_dir.FullName, "mic.json"),
            CaptureClock.StartNow(), preferredDeviceId: "fake-device",
            openCapture: id => new CaptureEndpoint(devices.Dequeue(), "fake-device", "Fake mic"),
            defaultDeviceId: () => null);

    private static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private static void RunBounded(Action a, double maxSeconds, string what)
    {
        var sw = Stopwatch.StartNew();
        var t = Task.Run(a);
        Assert.True(t.Wait(TimeSpan.FromSeconds(15)), $"{what}: interblocage (pas de retour en 15 s)");
        Assert.True(sw.Elapsed.TotalSeconds < maxSeconds, $"{what}: {sw.Elapsed.TotalSeconds:0.0}s, liberation sous verrou ?");
    }

    [Fact]
    public void Restart_WithCallbackInFlight_DoesNotDeadlock()
    {
        var fmt = new WaveFormat(16000, 16, 1);
        var first = new FakeWaveIn(fmt);
        var second = new FakeWaveIn(fmt);
        var rec = NewMic(new Queue<FakeWaveIn>(new[] { first, second }));
        rec.Start();
        Assert.True(first.Started);

        // The device dies while its capture thread is delivering a buffer.
        first.ArmInFlightCallback();
        first.Fail();
        Assert.NotNull(rec.Error);

        // Before the fix: lock(_lock) → StopRecording/Dispose under the lock → Dispose joins
        // the capture thread, which waits on the same lock in OnData → frozen forever.
        // ReleaseCapture is bounded (3 s), so "under 2.5 s" also proves no lock-held join.
        RunBounded(() => rec.WatchdogTick(Now()), 2.5, "WatchdogTick/Restart");

        Assert.True(first.DisposedCleanly, "the in-flight callback must complete before Dispose returns");
        Assert.True(second.Started);
        Assert.Equal(1, rec.RestartCount);
        Assert.Null(rec.Error);
        // The stale buffer of the detached capture was ignored.
        Assert.Null(rec.T0Stamp);
        Assert.Equal(0, rec.FramesWritten);

        // The replacement delivers normally.
        second.Push(160);
        Assert.Equal(160, rec.FramesWritten);

        // Stop has the same shape (UI thread): release outside the lock, bounded.
        second.ArmInFlightCallback();
        RunBounded(rec.Stop, 2.5, "Stop");
        Assert.True(second.DisposedCleanly);
    }

    [Fact]
    public void StaleStopEvent_OfDetachedCapture_DoesNotKillReplacement()
    {
        var fmt = new WaveFormat(16000, 16, 1);
        var first = new FakeWaveIn(fmt);
        var second = new FakeWaveIn(fmt);
        var rec = NewMic(new Queue<FakeWaveIn>(new[] { first, second }));
        rec.Start();
        first.Fail();
        rec.WatchdogTick(Now());
        Assert.Equal(1, rec.RestartCount);

        first.Fail(); // late event from the old device
        Assert.Null(rec.Error);
        rec.Stop();
    }

    [Fact]
    public void Mic_LiveGapFill_PadsStallOfOneSecondOrMore()
    {
        const int rate = 16000;
        var dev = new FakeWaveIn(new WaveFormat(rate, 16, 1));
        var rec = NewMic(new Queue<FakeWaveIn>(new[] { dev }));
        rec.Start();

        dev.Push(160); // t0
        Thread.Sleep(600);
        dev.Push(160);
        // 0.6 s < 1 s mic threshold: a scheduling hiccup, not a hole.
        Assert.Equal(320, rec.FramesWritten);
        Assert.Equal(0.0, rec.TotalGapSeconds);

        Thread.Sleep(1300);
        dev.Push(160);
        // ~1.9 s elapsed since t0: the file is brought back to the clock.
        double seconds = (double)rec.FramesWritten / rate;
        Assert.InRange(seconds, 1.8, 2.4);
        Assert.True(rec.TotalGapSeconds > 1.0);
        rec.Stop();
    }

    [Fact]
    public void Restart_PadsDeficitMeasuredOnPerfClock()
    {
        const int rate = 16000;
        var fmt = new WaveFormat(rate, 16, 1);
        var first = new FakeWaveIn(fmt);
        var second = new FakeWaveIn(fmt);
        var rec = NewMic(new Queue<FakeWaveIn>(new[] { first, second }));
        rec.Start();
        first.Push(160);
        Thread.Sleep(700);
        first.Fail();
        // The watchdog's own "last advance" is ~0 s ago here (first tick): the old
        // now - lastAdvance pad would add nothing. The clock deficit is ~0.7 s.
        rec.WatchdogTick(Now());
        double seconds = (double)rec.FramesWritten / rate;
        Assert.InRange(seconds, 0.6, 1.0);
        rec.Stop();
    }

    [Fact]
    public void Restart_OnDeviceWithOtherFormat_KeepsWavFormat()
    {
        // WAV opened at 48 kHz stereo float; the replacement is a 16 kHz mono BT headset.
        var first = new FakeWaveIn(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        var second = new FakeWaveIn(new WaveFormat(16000, 16, 1));
        var rec = NewMic(new Queue<FakeWaveIn>(new[] { first, second }));
        rec.Start();
        first.Fail();
        rec.WatchdogTick(Now());
        Assert.True(second.Started);

        for (int i = 0; i < 10; i++) second.Push(1600); // 1 s at 16 kHz
        long frames = rec.FramesWritten;
        Assert.InRange(frames, 48000 * 0.98, 48000 * 1.02);
        rec.Stop();

        using var r = new WaveFileReader(Path.Combine(_dir.FullName, "mic.wav"));
        Assert.Equal(48000, r.WaveFormat.SampleRate);
        Assert.Equal(2, r.WaveFormat.Channels);
        Assert.Equal(16, r.WaveFormat.BitsPerSample);
        Assert.Equal(rec.FramesWritten, r.SampleCount);
    }

    [Fact]
    public void Liveness_TracksRealBuffersOnly()
    {
        var dev = new FakeWaveIn(new WaveFormat(16000, 16, 1));
        var rec = NewMic(new Queue<FakeWaveIn>(new[] { dev }));
        rec.Start();
        Assert.Null(rec.LastDataPerfSeconds);
        Assert.True(rec.OpenedPerfSeconds > 0);
        Assert.True(rec.ContinuousStream); // a mic streams even in silence
        dev.Push(160);
        Assert.NotNull(rec.LastDataPerfSeconds);
        rec.Stop();
    }
}
