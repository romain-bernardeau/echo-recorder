using System.Diagnostics;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
// Disambiguate WPF types from their WinForms/GDI+ namesakes (both are referenced).
using Panel = System.Windows.Controls.Panel;
using TextBlock = System.Windows.Controls.TextBlock;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Echo.App;

/// <summary>
/// Owns the lifecycle of one <see cref="WebView2"/> hosted in a WPF <see cref="Panel"/>.
/// Centralises three concerns that used to live ad-hoc in the windows:
///   1. <b>Self-healing</b> (the "widget froze / app looked dead" fix): subscribes
///      <see cref="CoreWebView2.ProcessFailed"/>; on a renderer/browser process death it
///      disposes the dead control and recreates a fresh one <i>in place</i> — same window,
///      same bridge, page reloads — up to <see cref="MaxHeal"/> attempts with a short
///      backoff, then shows a visible error. The heal counter resets on every successful
///      navigation, so isolated failures always recover and only a true crash-loop trips.
///   2. <b>Dispose-on-hide</b> (RAM strategy): <see cref="TeardownAsync"/> disposes the
///      control (freeing its renderer process) while a window is hidden; <see cref="EnsureAsync"/>
///      recreates + renavigates on show. Idempotent and race-guarded by a semaphore.
///   3. <b>Attach</b>: delegates the pywebview shim + navigate to <see cref="WebViewHost"/>.
/// All work is serialised on <see cref="_gate"/> and marshalled to the owner's UI thread.
/// </summary>
public sealed class WebViewController
{
    private const int MaxHeal = 3;

    private readonly Panel _host;
    private readonly CoreWebView2Environment _env;
    private readonly Func<Bridge> _bridgeFactory;
    private readonly Window _owner;
    private readonly string _fileUri;
    private readonly string _name;          // "main" / "widget" — for logs + the "…webview ready" line

    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebView2? _web;
    private int _healAttempts;
    private bool _closed;                    // permanently torn down (window closing / app quit)

    public WebViewController(Panel host, CoreWebView2Environment env, Func<Bridge> bridgeFactory,
                             Window owner, string fileUri, string name)
    {
        _host = host;
        _env = env;
        _bridgeFactory = bridgeFactory;
        _owner = owner;
        _fileUri = fileUri;
        _name = name;
    }

    /// <summary>True while a live CoreWebView2 backs the control.</summary>
    public bool IsAlive => _web?.CoreWebView2 is not null;

    /// <summary>Create + attach + navigate if not already alive. Idempotent.</summary>
    public async Task EnsureAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_closed || _web?.CoreWebView2 is not null) return;
            await CreateLockedAsync().ConfigureAwait(true);
        }
        catch (Exception ex) { Log.Error($"{_name} webview ensure", ex); }
        finally { _gate.Release(); }
    }

    /// <summary>Dispose the control (release the renderer process) but keep the controller reusable.</summary>
    public async Task TeardownAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(true);
        try { DisposeWebLocked(); }
        finally { _gate.Release(); }
    }

    /// <summary>Fire-and-forget teardown (from UI event handlers).</summary>
    public void Teardown() => _ = TeardownAsync();

    /// <summary>Permanent close (window closing / app quit) — no further recreation.</summary>
    public void Close()
    {
        _closed = true;
        DisposeWebLocked();
    }

    // ── internals ────────────────────────────────────────────────────────────

    private async Task CreateLockedAsync()
    {
        var web = new WebView2();
        _host.Children.Clear();
        _host.Children.Add(web);
        _web = web;

        await WebViewHost.AttachAsync(web, _env, _bridgeFactory(), _owner, _fileUri).ConfigureAwait(true);

        var core = web.CoreWebView2;   // non-null after AttachAsync
        if (core is not null)
        {
            core.ProcessFailed += OnProcessFailed;
            core.NavigationCompleted += OnNavigationCompleted;
        }
        Log.Info($"{_name} webview ready → {_fileUri}");
    }

    private void DisposeWebLocked()
    {
        var web = _web;
        _web = null;
        if (web is null) return;
        try
        {
            var core = web.CoreWebView2;
            if (core is not null)
            {
                core.ProcessFailed -= OnProcessFailed;
                core.NavigationCompleted -= OnNavigationCompleted;
            }
        }
        catch { /* already torn down */ }
        try { _host.Children.Remove(web); } catch { }
        try { web.Dispose(); } catch (Exception ex) { Log.Error($"{_name} webview dispose", ex); }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        // A page that finished loading is proof the pipeline recovered → forgive past failures.
        if (e.IsSuccess) _healAttempts = 0;
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Log.Error($"{_name} webview ProcessFailed: kind={e.ProcessFailedKind}, reason={e.Reason}, exit={e.ExitCode}");
        _owner.Dispatcher.InvokeAsync(() => _ = HealAsync());
    }

    private async Task HealAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_closed) return;
            if (_healAttempts >= MaxHeal)
            {
                Log.Error($"{_name} webview: giving up after {_healAttempts} heal attempts — showing error.");
                ShowFatalLocked();
                return;
            }
            _healAttempts++;
            Log.Info($"{_name} webview heal attempt {_healAttempts}/{MaxHeal}");
            try { await Task.Delay(TimeSpan.FromMilliseconds(250 * _healAttempts)).ConfigureAwait(true); } catch { }
            DisposeWebLocked();
            await CreateLockedAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error($"{_name} webview heal failed", ex);
            // Retry until MaxHeal is reached (counter already incremented above).
            _ = _owner.Dispatcher.InvokeAsync(() => _ = HealAsync());
        }
        finally { _gate.Release(); }
    }

    private void ShowFatalLocked()
    {
        try
        {
            DisposeWebLocked();
            _host.Children.Clear();
            _host.Children.Add(new TextBlock
            {
                Text = "Echo : l'affichage a rencontré un problème.\n\n" +
                       "Fermez puis rouvrez la fenêtre depuis l'icône près de l'horloge.\n" +
                       "L'enregistrement et le traitement continuent en arrière-plan.",
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                FontSize = 13,
                Margin = new Thickness(28),
                Foreground = new SolidColorBrush(Color.FromRgb(0x0b, 0x15, 0x26)),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }
        catch (Exception ex) { Log.Error($"{_name} webview fatal-ui", ex); }
    }

    // ── diagnostics (RAM strategy visibility) ──────────────────────────────────

    /// <summary>Current process working set (MB) + count of live msedgewebview2 processes on the
    /// machine. The count is global (WebView2 has no per-app query), used only for before/after
    /// deltas around a hide so the RAM gain is visible in echo.log.</summary>
    public static (long WorkingSetMB, int WebViewProcs) MemorySnapshot()
    {
        long ws = 0;
        try { ws = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024); } catch { }
        int n = 0;
        try
        {
            var procs = Process.GetProcessesByName("msedgewebview2");
            n = procs.Length;
            foreach (var p in procs) { try { p.Dispose(); } catch { } }
        }
        catch { }
        return (ws, n);
    }
}
