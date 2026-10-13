using Microsoft.Win32;

namespace Echo.App;

/// <summary>
/// Launch-at-logon toggle via the HKCU Run key (admin-free, mirrors settings.autostart).
/// The spec allows either a Startup-folder Echo.lnk or a registry Run value; the Run
/// key avoids COM shell-link interop. See docs/contract/platform.md §8.
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Echo";

    private static string ExePath => Environment.ProcessPath ?? "";

    public static bool IsEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
        }
        catch { return false; }
    }

    public static bool Set(bool enable)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (k is null) return false;
            if (enable) k.SetValue(ValueName, $"\"{ExePath}\"");
            else if (k.GetValue(ValueName) is not null) k.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex) { Log.Error("autostart set", ex); return false; }
    }
}
