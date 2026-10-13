using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Echo.App.Engine;

/// <summary>
/// Global start/stop recording hotkey (settings <c>hotkey_enabled</c> + <c>hotkey</c>,
/// e.g. "Ctrl+Alt+R"). Uses a message-only window (HWND_MESSAGE parent) so the hotkey
/// works while the main window is closed to tray or disposed. Registration always
/// happens on the thread that created the window (WPF dispatcher). A combo already
/// owned by another app is logged and skipped, never fatal.
/// </summary>
internal static class HotkeyManager
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0xE010;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private static HwndSource? _src;
    private static AppServices? _services;
    private static bool _registered;

    /// <summary>Create the message-only window and apply the current settings. UI thread only.</summary>
    public static void Init(AppServices services)
    {
        _services = services;
        try
        {
            var p = new HwndSourceParameters("EchoHotkey")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
                ParentWindow = new IntPtr(-3),   // HWND_MESSAGE: message-only, no visual
            };
            _src = new HwndSource(p);
            _src.AddHook(Hook);
        }
        catch (Exception ex)
        {
            Log.Error("hotkey window", ex);
            return;
        }
        Apply();
    }

    /// <summary>(Re)apply registration from current settings. Safe from any thread.</summary>
    public static void Reapply()
    {
        var src = _src;
        if (src is null) return;
        try { src.Dispatcher.BeginInvoke(Apply); } catch { /* app shutting down */ }
    }

    private static void Apply()
    {
        if (_src is null || _services is null) return;
        if (_registered)
        {
            try { UnregisterHotKey(_src.Handle, HotkeyId); } catch { }
            _registered = false;
        }
        var s = _services.Settings.Current;
        if (!s.HotkeyEnabled) { Log.Info("[hotkey] désactivé (réglages)"); return; }
        if (!TryParse(s.Hotkey, out var mods, out var vk))
        {
            Log.Info($"[hotkey] combinaison illisible « {s.Hotkey} » — non enregistré");
            return;
        }
        _registered = RegisterHotKey(_src.Handle, HotkeyId, mods | ModNoRepeat, vk);
        Log.Info(_registered
            ? $"[hotkey] {s.Hotkey} actif (démarrer/arrêter l'enregistrement)"
            : $"[hotkey] {s.Hotkey} refusé par Windows (déjà pris par une autre application ?)");
    }

    private static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            OnPressed();
        }
        return IntPtr.Zero;
    }

    private static void OnPressed()
    {
        var sv = _services;
        if (sv is null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var sm = sv.Session;
                // A press while the previous capture is still being finalised (IsRecording is
                // already false then) used to START a new recording: ignore it.
                if (sm.IsStopping)
                {
                    Log.Info("[hotkey] ignoré : arrêt de l'enregistrement en cours");
                    return;
                }
                if (sm.IsRecording || sm.CaptureAlive)
                {
                    sv.Notifier.Notify(sv.I18n.T("hk_stop"));
                    await sm.StopAndProcessAsync();
                }
                else
                {
                    sv.Notifier.Notify(sv.I18n.T("hk_start"));
                    await sm.StartSmartAsync();
                }
            }
            catch (Exception ex) { Log.Error("hotkey action", ex); }
        });
    }

    /// <summary>"Ctrl+Alt+R" → (MOD_CONTROL|MOD_ALT, 'R'). Letters, digits, F1-F24, Space.</summary>
    internal static bool TryParse(string? combo, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        foreach (var raw in (combo ?? "").Split('+'))
        {
            var tok = raw.Trim();
            if (tok.Length == 0) continue;
            switch (tok.ToLowerInvariant())
            {
                case "ctrl" or "control" or "ctl": mods |= ModControl; break;
                case "alt": mods |= ModAlt; break;
                case "shift" or "maj": mods |= ModShift; break;
                case "win" or "windows" or "meta": mods |= ModWin; break;
                case "space" or "espace": if (vk != 0) return false; vk = 0x20; break;
                default:
                    if (vk != 0) return false;                        // a single main key only
                    if (tok.Length == 1 && char.IsLetterOrDigit(tok[0]))
                        vk = char.ToUpperInvariant(tok[0]);           // 'A'-'Z' / '0'-'9' == VK codes
                    else if (tok.Length is 2 or 3 && (tok[0] is 'f' or 'F')
                             && int.TryParse(tok[1..], out var fn) && fn is >= 1 and <= 24)
                        vk = (uint)(0x70 + fn - 1);                   // VK_F1..VK_F24
                    else return false;
                    break;
            }
        }
        return vk != 0 && mods != 0;   // require at least one modifier (global hotkey etiquette)
    }
}
