using System.IO;
using Microsoft.Win32;
using Echo.Core;

namespace Echo.App.Engine;

/// <summary>
/// Registers Echo in the per-user "Installed apps" list
/// (<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Echo</c>) so a colleague
/// sees it in Windows Settings → Apps and can remove it cleanly — no admin. ONLY the
/// installed copy (under <c>%LOCALAPPDATA%\Programs</c>) registers; the dev build running
/// from the repo <c>bin\</c> never does. Called on every startup so the displayed version
/// tracks silent auto-updates (the updater replaces files but not the registry). The
/// "Uninstall" action runs the <c>uninstall.ps1</c> shipped next to the exe. Never throws.
/// </summary>
public static class UninstallRegistry
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Echo";

    /// <summary>Create/refresh the uninstall entry for the installed instance. No-op for the
    /// dev build or if the shipped uninstaller is missing. Best-effort (swallows all errors).</summary>
    public static void EnsureRegistered()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
            var programs = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
            if (!baseDir.StartsWith(programs, StringComparison.OrdinalIgnoreCase)) return;   // installed copy only

            var exe = Path.Combine(baseDir, "Echo.exe");
            var uninstall = Path.Combine(baseDir, "uninstall.ps1");
            if (!File.Exists(uninstall)) return;   // nothing to point "Uninstall" at

            var ps = $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{uninstall}\"";

            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            if (key is null) return;
            key.SetValue("DisplayName", "Echo");
            key.SetValue("DisplayVersion", EchoConstants.AppVersion);
            key.SetValue("Publisher", "Echo");
            key.SetValue("InstallLocation", baseDir);
            key.SetValue("DisplayIcon", exe);
            key.SetValue("UninstallString", ps);
            key.SetValue("QuietUninstallString", ps);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            try { key.SetValue("EstimatedSize", DirSizeKb(baseDir), RegistryValueKind.DWord); } catch { /* size is optional */ }
        }
        catch { /* registration is best-effort — never break startup */ }
    }

    private static int DirSizeKb(string dir)
    {
        long bytes = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { bytes += new FileInfo(f).Length; } catch { /* skip unreadable */ }
        }
        return (int)Math.Min(int.MaxValue, bytes / 1024);
    }
}
