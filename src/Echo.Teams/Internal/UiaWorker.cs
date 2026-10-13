using System.Collections.Concurrent;
using FlaUI.UIA3;

namespace Echo.Teams.Internal;

/// <summary>
/// A dedicated MTA background thread that owns exactly one <see cref="UIA3Automation"/>
/// instance and serialises all UIA work onto it. UI Automation bulk tree walks must run
/// on an MTA thread (never a WinForms/WPF STA thread), and a <c>UIA3Automation</c> is not
/// safe to share across threads — this class enforces both. Short synchronous calls
/// (InMeeting / ReadMeetingInfo / HealthCheck) marshal here; the fast speaker
/// <c>WatchAsync</c> loop uses its own separate MTA thread + automation so it never
/// blocks these calls.
/// </summary>
internal sealed class UiaWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private readonly Action<string>? _log;
    private UIA3Automation? _automation;

    public UiaWorker(Action<string>? log)
    {
        _log = log;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Echo.Teams.UIA",
        };
        _thread.SetApartmentState(ApartmentState.MTA); // must be set before Start()
        _thread.Start();
    }

    private void Run()
    {
        try
        {
            _automation = new UIA3Automation(); // CoInitializeEx (MTA) happens for this thread
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[Echo.Teams] UIA init failed: {ex.Message}");
        }

        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try { work(); }
            catch { /* per-item errors are surfaced to the caller via Invoke */ }
        }

        try { _automation?.Dispose(); } catch { /* ignore */ }
    }

    /// <summary>Runs <paramref name="fn"/> on the MTA thread and blocks for its result.</summary>
    public T Invoke<T>(Func<UIA3Automation, T> fn)
    {
        T result = default!;
        Exception? error = null;
        using var done = new ManualResetEventSlim(false);

        _queue.Add(() =>
        {
            try
            {
                var automation = _automation ?? throw new InvalidOperationException("UIA automation not initialised.");
                result = fn(automation);
            }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        });

        done.Wait();
        if (error != null) throw error;
        return result;
    }

    public void Dispose()
    {
        try { _queue.CompleteAdding(); } catch { /* ignore */ }
        try { _thread.Join(2000); } catch { /* ignore */ }
        try { _queue.Dispose(); } catch { /* ignore */ }
    }
}
