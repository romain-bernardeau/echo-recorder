using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Echo.App;

/// <summary>
/// Wires a <see cref="WebView2"/> to the <see cref="Bridge"/>: injects the pywebview
/// shim (recreating window.pywebview.api + the pywebviewready event), correlates
/// request/reply messages by id, and handles the .pywebview-drag-region drag. Both
/// the main and widget windows share one <see cref="CoreWebView2Environment"/> so
/// they run in a single Edge process (RAM strategy).
///
/// The view is locked to the app's own pages: the bridge can delete folders and open
/// files, so a document that is not one of ours (a link clicked in a CR e-mail body, a
/// dropped file, a page opened by window.open) must never reach it. Top-level
/// navigation is limited to <c>file:///…/ui/</c>, external http(s) links open in the
/// system browser, new windows and downloads are refused, and every bridge message is
/// checked against the document that posted it.
/// </summary>
public static class WebViewHost
{
    /// <summary>Virtual host serving the recordings folder (the transcript tab's audio player).</summary>
    public const string RecordingsHost = "echo-recordings";

    private const string Shim = """
(function(){
  if (window.__echoBridge) return;
  // Only the app's own top-level pages get the bridge (never an iframe, never a remote page).
  if (window.top !== window || location.protocol !== 'file:') return;
  window.__echoBridge = true;
  var pending = {}, seq = 0;
  window.chrome.webview.addEventListener('message', function(e){
    var d = e.data;
    if (!d || !d.__reply) return;
    var p = pending[d.id];
    if (!p) return;
    delete pending[d.id];
    if (d.ok) p.resolve(d.result); else p.reject(new Error(d.error || 'bridge error'));
  });
  function call(method, args){
    return new Promise(function(resolve, reject){
      var id = ++seq;
      pending[id] = { resolve: resolve, reject: reject };
      window.chrome.webview.postMessage({ id: id, method: method, args: Array.prototype.slice.call(args || []) });
    });
  }
  window.pywebview = { api: new Proxy({}, { get: function(_t, m){ return function(){ return call(m, arguments); }; } }) };
  function fireReady(){ try { window.dispatchEvent(new Event('pywebviewready')); } catch (e) {} }
  if (document.readyState === 'loading') window.addEventListener('DOMContentLoaded', fireReady);
  else fireReady();
  document.addEventListener('mousedown', function(e){
    if (e.button !== 0) return;
    var t = e.target;
    if (t && t.closest && t.closest('.pywebview-drag-region, .head') &&
        !t.closest('button,input,select,textarea,a,kbd,.icon-btn,.win-controls')) {
      window.chrome.webview.postMessage({ __drag: true });
    }
  }, true);
})();
""";

    /// <summary>The shipped ui folder, canonical, with a trailing separator.</summary>
    private static readonly string UiDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "ui")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

    public static async Task AttachAsync(WebView2 web, CoreWebView2Environment env, Bridge bridge, Window owner, string fileUri)
    {
        await web.EnsureCoreWebView2Async(env);
        var core = web.CoreWebView2;

        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
#if DEBUG
        core.Settings.AreDevToolsEnabled = true;
#else
        core.Settings.AreDevToolsEnabled = false;
#endif
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        // A file dropped on the window would otherwise replace the app page.
        try { web.AllowExternalDrop = false; } catch (Exception ex) { Log.Error("webview external drop", ex); }

        // Serve the recordings folder as https://echo-recordings/ so the transcript tab can
        // stream the "… - Audio.mp3" mixdown in a plain <audio> element (a file:// page
        // cannot reliably reference media files outside its own tree). Re-mapped at once
        // when the user picks another recordings folder (Bridge.RecordingsDirChanged).
        MapRecordings(core, bridge.RecordingsDir);
        var dispatcher = owner.Dispatcher;
        bridge.RecordingsDirChanged = dir =>
        {
            try { dispatcher.Invoke(() => MapRecordings(core, dir)); }
            catch { /* view torn down: the next attach maps the new folder */ }
        };

        core.NavigationStarting += (_, e) =>
        {
            if (IsAppPage(e.Uri) || IsBlank(e.Uri)) return;
            e.Cancel = true;
            OpenExternal(e.Uri, e.IsUserInitiated);
        };
        core.FrameNavigationStarting += (_, e) =>
        {
            // The CR preview is a sandboxed srcdoc iframe; a link clicked inside it goes to
            // the browser, never into the app window.
            if (IsBlank(e.Uri) || IsRecordingsUrl(e.Uri)) return;
            e.Cancel = true;
            OpenExternal(e.Uri, e.IsUserInitiated);
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternal(e.Uri, e.IsUserInitiated);
        };
        core.DownloadStarting += (_, e) => { e.Cancel = true; };

        await core.AddScriptToExecuteOnDocumentCreatedAsync(Shim);

        core.WebMessageReceived += (_, e) =>
        {
            // Only our own pages may drive the bridge.
            if (!IsAppPage(e.Source))
            {
                Log.Error($"webview: message from a foreign document ignored ({Trim(e.Source)})");
                return;
            }
            string json;
            try { json = e.WebMessageAsJson; }
            catch { return; }
            OnMessage(core, dispatcher, bridge, owner, json);
        };

        core.Navigate(fileUri);
    }

    private static void MapRecordings(CoreWebView2 core, string dir)
    {
        try
        {
            try { core.ClearVirtualHostNameToFolderMapping(RecordingsHost); } catch { /* not mapped yet */ }
            core.SetVirtualHostNameToFolderMapping(RecordingsHost, dir, CoreWebView2HostResourceAccessKind.Allow);
        }
        catch (Exception ex) { Log.Error("virtual host mapping", ex); }
    }

    /// <summary>True for a file:// document inside the shipped ui folder.</summary>
    internal static bool IsAppPage(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var u) || !u.IsFile) return false;
        try { return Path.GetFullPath(u.LocalPath).StartsWith(UiDir, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static bool IsBlank(string? uri)
        => uri is not null && (uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase)
                               || uri.Equals("about:srcdoc", StringComparison.OrdinalIgnoreCase));

    private static bool IsRecordingsUrl(string? uri)
        => Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
           && string.Equals(u.Host, RecordingsHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>A user click on an http(s) link opens the system browser; anything else is dropped.</summary>
    private static void OpenExternal(string? uri, bool userInitiated)
    {
        if (!userInitiated || !Uri.TryCreate(uri, UriKind.Absolute, out var u)) return;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return;
        if (IsRecordingsUrl(uri)) return;
        try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("open external link", ex); }
    }

    private static string Trim(string? s) => s is null ? "" : s.Length <= 120 ? s : s[..120] + "…";

    private static void OnMessage(CoreWebView2 core, Dispatcher dispatcher, Bridge bridge, Window owner, string json)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(json).RootElement; }
        catch { return; }

        // Frameless window drag request.
        if (root.TryGetProperty("__drag", out var dragEl) && dragEl.ValueKind == JsonValueKind.True)
        {
            try { Native.BeginDrag(new WindowInteropHelper(owner).Handle); }
            catch (Exception ex) { Log.Error("begin drag", ex); }
            return;
        }

        if (!root.TryGetProperty("id", out var idEl) || !root.TryGetProperty("method", out var methodEl))
            return;

        var id = idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64() : 0;
        var method = methodEl.GetString() ?? "";
        var args = root.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array
            ? argsEl.EnumerateArray().ToArray()
            : Array.Empty<JsonElement>();

        _ = Task.Run(async () =>
        {
            bool ok = true;
            object? result = null;
            string? error = null;
            try { result = await bridge.DispatchAsync(method, args); }
            catch (Exception ex) { ok = false; error = ex.Message; Log.Error($"dispatch {method}", ex); }

            string reply;
            try { reply = JsonSerializer.Serialize(new { __reply = true, id, ok, result, error }); }
            catch (Exception ex)
            {
                Log.Error($"serialize reply {method}", ex);
                reply = JsonSerializer.Serialize(new { __reply = true, id, ok = false, result = (object?)null, error = "serialization error" });
            }

            try { dispatcher.Invoke(() => { try { core.PostWebMessageAsJson(reply); } catch { } }); }
            catch { /* window closing */ }
        });
    }
}
