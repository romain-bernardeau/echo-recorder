using System.Text;

namespace Echo.Core.Storage;

/// <summary>
/// Resolves every on-disk location (see docs/contract/platform.md §3).
/// Settings/secret live under %APPDATA%\Echo; recordings/logs/webview under
/// %LOCALAPPDATA%\Echo. The recordings root can be overridden by settings.
/// </summary>
public sealed class StoragePaths
{
    private readonly Func<string?> _recordingsOverride;

    /// <param name="appDataRoot">Stand-in for %APPDATA% — TESTS ONLY. Without this seam a test
    /// cannot redirect the store: <see cref="Environment.GetFolderPath"/> asks Windows and
    /// ignores the APPDATA variable, so tests that "swapped APPDATA" were in fact reading and
    /// writing the developer's own settings.json (2026-09-18: a test overwrote it outright).</param>
    /// <param name="localAppDataRoot">Same, for %LOCALAPPDATA% (recordings, logs).</param>
    public StoragePaths(Func<string?>? recordingsOverride = null,
                        string? appDataRoot = null, string? localAppDataRoot = null)
    {
        var appData = appDataRoot;
        if (string.IsNullOrEmpty(appData))
            appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData))
            appData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = localAppDataRoot;
        if (string.IsNullOrEmpty(local))
            local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local)) local = appData;

        SettingsDir = Path.Combine(appData, EchoConstants.AppName);
        DataDir = Path.Combine(local, EchoConstants.AppName);
        _recordingsOverride = recordingsOverride ?? (() => null);
    }

    public string SettingsDir { get; }
    public string DataDir { get; }

    public string SettingsFile => Path.Combine(SettingsDir, "settings.json");
    public string SecretBin => Path.Combine(SettingsDir, "secret.bin");
    public string SecretTxt => Path.Combine(SettingsDir, "secret.txt");
    /// <summary>DPAPI-encrypted Google AI Studio (Gemini) key, kept in a separate file
    /// so the existing Mistral secret.bin is never touched.</summary>
    public string SecretGoogleBin => Path.Combine(SettingsDir, "secret_google.bin");
    /// <summary>DPAPI-encrypted Cloudflare Workers AI token (separate file, same scheme).</summary>
    public string SecretCloudflareBin => Path.Combine(SettingsDir, "secret_cloudflare.bin");
    /// <summary>DPAPI-encrypted OpenRouter API key (separate file, same scheme).</summary>
    public string SecretOpenRouterBin => Path.Combine(SettingsDir, "secret_openrouter.bin");

    public string ContactsFile => Path.Combine(DataDir, "contacts.json");
    public string WebViewDir => Path.Combine(DataDir, "webview");
    public string LogFile => Path.Combine(DataDir, "echo.log");

    public string RecordingsDir
    {
        get
        {
            var o = _recordingsOverride();
            return string.IsNullOrWhiteSpace(o) ? Path.Combine(DataDir, "recordings") : o!;
        }
    }

    public void EnsureBaseDirs()
    {
        Directory.CreateDirectory(SettingsDir);
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(RecordingsDir);
    }

    /// <summary>
    /// Session folder name: "{yyyyMMdd - HHhmm} - {safeName}".
    /// Physical meetings default to "Reunion physique".
    /// </summary>
    public string SessionFolderName(DateTime local, string? meetingName, bool physical)
    {
        var stamp = $"{local:yyyyMMdd} - {local:HH}h{local:mm}";
        var name = SafeName(meetingName);
        if (string.IsNullOrEmpty(name)) name = physical ? "Reunion physique" : "Reunion";
        return $"{stamp} - {name}";
    }

    /// <summary>True when <paramref name="dir"/> must not become the recordings root: a drive root,
    /// a network path, the user profile or one of its main folders, Windows or Program Files.
    /// Every path guard and clean-up is anchored to that root, so it has to be a folder of its own.</summary>
    public static bool IsUnsafeRecordingsRoot(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return true;
        string full;
        try { full = Path.GetFullPath(dir).TrimEnd('\\', '/'); }
        catch { return true; }
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.Length <= 2) return true;   // UNC, "C:"
        var sf = new[]
        {
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Desktop,
            Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.System,
        };
        var banned = sf.Select(Environment.GetFolderPath)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"))
            .Append(Environment.GetEnvironmentVariable("OneDrive") ?? "")
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.TrimEnd('\\', '/'));
        return banned.Any(b => string.Equals(b, full, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Sanitise a meeting name for use in a folder name (mirrors the Python rule).</summary>
    public static string SafeName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
            if ("<>:\"/|?*\\".IndexOf(c) < 0 && !char.IsControl(c)) sb.Append(c);
        var s = sb.ToString().Trim();
        if (s.Length > 50) s = s[..50];
        return s.TrimEnd(' ', ',', ';', '-', '.');
    }
}
