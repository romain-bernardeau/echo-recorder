using System.Diagnostics;

namespace Echo.Core.Storage;

/// <summary>
/// The <c>.echo_status</c> sentinel of a session folder: <c>"state\n&lt;pid&gt;\n&lt;epoch&gt;"</c>,
/// state = running (pipeline at work) | waiting (parked in the retry queue) | error.
/// ONE reading of it for every consumer: the list badge (SessionQuery), the disk sweeper
/// (RetentionSweeper) and the startup recovery. They used to disagree: the sweeper took any
/// "running" for live whatever its pid or age, so a session whose pipeline died with the app
/// stayed "live" forever and was never cleaned, while the list already showed it as stale.
/// </summary>
public static class SessionStatus
{
    public const string FileName = ".echo_status";

    /// <summary>A "running" sentinel older than this is a dead pipeline, whatever its pid says.</summary>
    public const long StaleRunningSeconds = 3 * 3600;

    public sealed record Sentinel(string State, int? Pid, long? Epoch);

    /// <summary>Null when the session has no sentinel. Throws when it exists but cannot be read
    /// (callers decide: the sweeper then assumes "live" and never deletes).</summary>
    public static Sentinel? Read(string dir)
    {
        var p = SessionLayout.Find(dir, FileName);
        if (!File.Exists(p)) return null;
        return Parse(File.ReadAllText(p));
    }

    public static Sentinel Parse(string raw)
    {
        var lines = (raw ?? "").Replace("\r", "").Split('\n');
        var st = lines.Length > 0 ? lines[0].Trim().ToLowerInvariant() : "";
        int? pid = lines.Length > 1 && int.TryParse(lines[1].Trim(), out var p) ? p : null;
        long? epoch = lines.Length > 2 && long.TryParse(lines[2].Trim(), out var t) ? t : null;
        return new Sentinel(st, pid, epoch);
    }

    public static void Write(string dir, string state)
    {
        try
        {
            var pid = Environment.ProcessId;
            var epoch = DateTimeOffset.Now.ToUnixTimeSeconds();
            File.WriteAllText(SessionLayout.ForWrite(dir, FileName), $"{state}\n{pid}\n{epoch}\n");
        }
        catch { /* best effort: the badge only */ }
    }

    public static void Clear(string dir)
    {
        SessionLayout.Delete(dir, FileName);
    }

    /// <summary>
    /// State as the CURRENT process should show it: "error", "waiting", "running", or null
    /// (no live sentinel: the session's files decide). "waiting" and "running" only hold for
    /// THIS process (the queue re-marks its items at startup; a foreign pid is a dead run);
    /// a stale "waiting" reads as "error", a stale "running" as nothing.
    /// </summary>
    public static string? Effective(Sentinel? s, int currentPid, long nowEpoch)
    {
        if (s is null) return null;
        switch (s.State)
        {
            case "error": return "error";
            case "waiting": return s.Pid == currentPid ? "waiting" : "error";
            case "running":
                bool stale = s.Pid is null || s.Pid != currentPid
                             || (s.Epoch is { } e && nowEpoch - e > StaleRunningSeconds);
                return stale ? null : "running";
            default: return null;
        }
    }

    /// <summary>Being processed or queued by THIS process right now: never touch its files.</summary>
    public static bool IsLive(Sentinel? s, int currentPid, long nowEpoch)
        => Effective(s, currentPid, nowEpoch) is "running" or "waiting";

    /// <summary>
    /// A "running"/"waiting" sentinel left by a process that is gone: the app was killed, quit
    /// or logged off in the middle of a pipeline. <paramref name="isEchoAlive"/> tells whether a
    /// pid still belongs to a running Echo (a reused pid owned by another program is foreign).
    /// </summary>
    public static bool IsInterrupted(Sentinel? s, int currentPid, Func<int, bool> isEchoAlive)
    {
        if (s is null || s.State is not ("running" or "waiting")) return false;
        if (s.Pid is not { } pid) return true;
        if (pid == currentPid) return false;
        return !isEchoAlive(pid);
    }

    /// <summary>True when <paramref name="pid"/> is a live process named Echo.</summary>
    public static bool IsEchoProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited && string.Equals(p.ProcessName, EchoConstants.AppName, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }   // no such process
    }
}
