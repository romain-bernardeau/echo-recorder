using System.Collections.Concurrent;
using Echo.Core.Abstractions;

namespace Echo.App;

/// <summary>
/// <see cref="INotifier"/> implementation with the Python routing rule (echo_ui): a
/// message becomes an in-app toast when a window is visible (drained by
/// <c>Api.poll_toasts</c>), otherwise a silent tray balloon. The toast queue is
/// thread-safe (engine notifications arrive from background tasks).
/// </summary>
public sealed class TrayNotifier : INotifier
{
    private readonly ConcurrentQueue<string> _toasts = new();
    private volatile bool _mainVisible;
    private volatile bool _widgetVisible;

    /// <summary>Set by the App once the tray icon exists; marshals to the UI thread.</summary>
    public Action<string>? ShowBalloon { get; set; }

    public void Notify(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        Log.Info($"notify: {message}");
        if (_mainVisible || _widgetVisible) _toasts.Enqueue(message);
        else
        {
            try { ShowBalloon?.Invoke(message); }
            catch (Exception ex) { Log.Error("balloon failed", ex); }
        }
    }

    /// <summary>JS reports window visibility (Api.set_ui_visible).</summary>
    public void SetUiVisible(string which, bool on)
    {
        if (which == "main") _mainVisible = on;
        else if (which == "widget") _widgetVisible = on;
    }

    /// <summary>Drain queued messages for in-app display (Api.poll_toasts).</summary>
    public string[] Drain()
    {
        var list = new List<string>();
        while (_toasts.TryDequeue(out var m)) list.Add(m);
        return list.ToArray();
    }
}
