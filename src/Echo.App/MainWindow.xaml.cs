using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Echo.App;

/// <summary>
/// Frameless main window hosting the WebView2 that loads ui/index.html. Closing hides
/// to the tray (real quit is via the tray menu). The WebView2 is torn down while
/// hidden and recreated on show (via <see cref="WebViewController"/>) to release the
/// Edge renderer RAM, and self-heals if its process dies.
/// </summary>
public partial class MainWindow : Window
{
    private readonly WebViewController _controller;

    /// <summary>Raised when the user closes the window (hide-to-tray decision lives in App).</summary>
    public event EventHandler? HideToTrayRequested;

    /// <summary>When true, the [x]/Close really closes (used during app shutdown).</summary>
    public bool ForceClose { get; set; }

    public MainWindow(AppServices services, IAppShell shell, CoreWebView2Environment env)
    {
        InitializeComponent();
        var uri = new Uri(Path.Combine(AppContext.BaseDirectory, "ui", "index.html")).AbsoluteUri;
        _controller = new WebViewController(Host, env, () => new Bridge(services, shell), this, uri, "main");
        Loaded += OnLoaded;
        Closing += OnClosing;
        // Minimised: hide the host so the WebView2 reports document.hidden; the page then
        // suspends its 1 s get_state polling (and Edge throttles the renderer) until restored.
        StateChanged += (_, _) =>
            Host.Visibility = WindowState == WindowState.Minimized ? Visibility.Hidden : Visibility.Visible;
    }

    /// <summary>Create (or recreate) the WebView2 — called on first show and on show-from-tray.</summary>
    public Task EnsureWebViewAsync() => _controller.EnsureAsync();

    /// <summary>Dispose the WebView2 while hidden to release its renderer process.</summary>
    public void TeardownWebView() => _controller.Teardown();

    private async void OnLoaded(object? sender, RoutedEventArgs e) => await _controller.EnsureAsync();

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (ForceClose) { _controller.Close(); return; }
        // Never really close from the [x]; hide to tray instead.
        e.Cancel = true;
        HideToTrayRequested?.Invoke(this, EventArgs.Empty);
    }
}
