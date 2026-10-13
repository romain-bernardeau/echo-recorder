using System.IO;

namespace Echo.App;

/// <summary>
/// Minimal file logger → %LOCALAPPDATA%\Echo\echo.log (rotates at 2 MB → echo.log.1,
/// mirroring the Python engine). A tray app has no console, so unhandled exceptions
/// are logged here for post-mortem of startup crashes.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _file;
    private const long RotateBytes = 2 * 1024 * 1024;

    public static void Init(string logFile)
    {
        _file = logFile;
        try { Directory.CreateDirectory(Path.GetDirectoryName(logFile)!); } catch { }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message) => Write("ERROR", message);
    public static void Error(string message, Exception ex) => Write("ERROR", $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        if (_file is null) return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}";
        lock (Gate)
        {
            if (TryAppend(_file, line)) return;
            // Primary locked (e.g. a legacy Echo process holds echo.log open) →
            // fall back to a sibling file so .NET logs are never lost.
            var fallback = Path.Combine(Path.GetDirectoryName(_file)!, "echo.net.log");
            if (!string.Equals(fallback, _file, StringComparison.OrdinalIgnoreCase) && TryAppend(fallback, line))
                _file = fallback;
        }
    }

    private static bool TryAppend(string path, string line)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > RotateBytes)
            {
                var bak = path + ".1";
                if (File.Exists(bak)) File.Delete(bak);
                File.Move(path, bak);
            }
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var w = new StreamWriter(fs);
            w.Write(line);
            return true;
        }
        catch { return false; }
    }
}
