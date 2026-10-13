using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Echo.App;

/// <summary>
/// Small always-on-top floating recording widget (ui/widget.html), shown while
/// recording. Shares the main window's <see cref="CoreWebView2Environment"/> (one
/// Edge process). Its WebView2 is created lazily on first Show and disposed on Hide
/// (RAM), and self-heals on process death — both via <see cref="WebViewController"/>.
/// Sizing is driven from JS via <c>Api.widget_resize</c>.
/// </summary>
public partial class WidgetWindow : Window
{
    private readonly WebViewController _controller;

    public WidgetWindow(AppServices services, IAppShell shell, CoreWebView2Environment env)
    {
        InitializeComponent();
        var uri = new Uri(Path.Combine(AppContext.BaseDirectory, "ui", "widget.html")).AbsoluteUri;
        _controller = new WebViewController(Host, env, () => new Bridge(services, shell), this, uri, "widget");
        SourceInitialized += (_, _) => PlaceBottomRight();
        Closing += (_, _) => _controller.Close();
    }

    /// <summary>Create the WebView2 lazily on show (recreated after a hide teardown).</summary>
    public Task EnsureWebViewAsync() => _controller.EnsureAsync();

    /// <summary>Dispose the WebView2 while hidden to release its renderer process.</summary>
    public void TeardownWebView() => _controller.Teardown();

    /// <summary>Resize from JS (min 52×120), then keep it anchored bottom-right.
    /// Short-circuits when the target size is unchanged (JS resizes every drag frame).</summary>
    public void ResizeWidget(double w, double? h)
    {
        double nw = Math.Max(52, w);
        double nh = h is not null ? Math.Max(120, h.Value) : Height;
        if (Math.Abs(nw - Width) < 0.5 && Math.Abs(nh - Height) < 0.5) return;   // no-op
        Width = nw;
        if (h is not null) Height = nh;
        PlaceBottomRight();
    }

    public void SetPinned(bool on) => Topmost = on;

    private void PlaceBottomRight()
    {
        try
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - Width - 24;
            Top = wa.Bottom - Height - 24;
        }
        catch { }
    }
}
