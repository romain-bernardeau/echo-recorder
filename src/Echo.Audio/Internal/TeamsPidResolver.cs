using System.Diagnostics;
using Echo.Audio.Interop;
using NAudio.CoreAudioApi;

namespace Echo.Audio.Internal;

/// <summary>
/// Teams process discovery for process-loopback targeting. Ports
/// process_loopback.teams_root_pids / teams_target_pids and
/// audio_guard.pick_target_pid. Parent-PID discovery uses
/// CreateToolhelp32Snapshot (no BCL API for parent PID); recency uses
/// Process.StartTime; render sessions come from NAudio's AudioSessionManager.
/// </summary>
internal static class TeamsPidResolver
{
    public const string TeamsRoot = "ms-teams.exe";
    public const string TeamsMediaHost = "ms-teams_modulehost.exe";

    /// <summary>{pid: (ppid, name_lower)} via CreateToolhelp32Snapshot.</summary>
    public static Dictionary<uint, (uint Ppid, string Name)> ToolhelpProcesses()
    {
        var result = new Dictionary<uint, (uint, string)>();
        IntPtr snap = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return result;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<PROCESSENTRY32W>() };
            bool ok = NativeMethods.Process32FirstW(snap, ref e);
            while (ok)
            {
                string name = (e.szExeFile ?? "").ToLowerInvariant();
                result[e.th32ProcessID] = (e.th32ParentProcessID, name);
                ok = NativeMethods.Process32NextW(snap, ref e);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(snap);
        }
        return result;
    }

    /// <summary>Root ms-teams.exe PIDs (parent not itself ms-teams.exe); all sorted as fallback.</summary>
    public static List<uint> TeamsRootPids(Dictionary<uint, (uint Ppid, string Name)>? procs = null)
    {
        procs ??= ToolhelpProcesses();
        var teams = procs.Where(kv => kv.Value.Name == TeamsRoot).Select(kv => kv.Key).ToHashSet();
        var roots = new List<uint>();
        foreach (var pid in teams)
        {
            var ppid = procs[pid].Ppid;
            if (!teams.Contains(ppid)) roots.Add(pid);
        }
        if (roots.Count > 0) return roots;
        var all = teams.ToList();
        all.Sort();
        return all;
    }

    private static double CreateTime(uint pid)
    {
        try
        {
            var p = Process.GetProcessById((int)pid);
            return new DateTimeOffset(p.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds() / 1000.0;
        }
        catch
        {
            return 0.0;
        }
    }

    /// <summary>
    /// PIDs to target, media-host (ms-teams_modulehost.exe) first by default, each
    /// group most-recent-first. Encodes the Microsoft silent-frame workaround.
    /// </summary>
    public static List<uint> TeamsTargetPids(bool preferModuleHost = true,
        Dictionary<uint, (uint Ppid, string Name)>? procs = null)
    {
        procs ??= ToolhelpProcesses();
        var hosts = procs.Where(kv => kv.Value.Name == TeamsMediaHost).Select(kv => kv.Key).ToList();
        var roots = TeamsRootPids(procs);

        List<uint> ByRecency(IEnumerable<uint> pids) =>
            pids.OrderByDescending(CreateTime).ToList();

        hosts = ByRecency(hosts);
        roots = ByRecency(roots.Where(p => !hosts.Contains(p)));
        return preferModuleHost ? hosts.Concat(roots).ToList() : roots.Concat(hosts).ToList();
    }

    public static bool PidExists(uint pid)
    {
        try
        {
            var p = Process.GetProcessById((int)pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// PIDs owning a render audio session, over EVERY active render endpoint (not only the
    /// default multimedia one: Teams typically renders on the COMMUNICATIONS device, which
    /// is often a different endpoint — the old default-only scan missed its session entirely).
    /// Returns both sets: any session, and sessions currently ACTIVE (playing right now).
    /// </summary>
    private static (HashSet<uint> Any, HashSet<uint> Active) RenderSessionPids()
    {
        var any = new HashSet<uint>();
        var active = new HashSet<uint>();
        try
        {
            using var enumr = new MMDeviceEnumerator();
            foreach (var dev in enumr.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    var sessions = dev.AudioSessionManager?.Sessions;
                    if (sessions is null) continue;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        try
                        {
                            var s = sessions[i];
                            uint pid = s.GetProcessID;
                            any.Add(pid);
                            if (s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive)
                                active.Add(pid);
                        }
                        catch { /* per-session query can fail; skip */ }
                    }
                }
                catch { /* endpoint without session manager */ }
                finally { try { dev.Dispose(); } catch { } }
            }
        }
        catch { /* enumeration best-effort */ }
        return (any, active);
    }

    /// <summary>
    /// audio_guard.pick_target_pid, session-state aware: media-host with an ACTIVE session
    /// > media-host with any session > media-host > root (same ladder), each
    /// most-recent-first. Null if no Teams.
    /// </summary>
    public static uint? PickTargetPid()
    {
        var procs = ToolhelpProcesses();
        var teams = procs
            .Where(kv => kv.Value.Name is TeamsRoot or TeamsMediaHost)
            .Select(kv => kv.Key)
            .ToList();
        if (teams.Count == 0) return null;

        var (withSession, activeSession) = RenderSessionPids();
        var hosts = teams.Where(p => procs[p].Name == TeamsMediaHost).ToList();
        var roots = teams.Where(p => procs[p].Name == TeamsRoot).ToList();

        List<uint> Recent(IEnumerable<uint> pids) => pids.OrderByDescending(CreateTime).ToList();

        foreach (var pool in new[]
                 {
                     hosts.Where(activeSession.Contains),
                     hosts.Where(withSession.Contains),
                     hosts,
                     roots.Where(activeSession.Contains),
                     roots.Where(withSession.Contains),
                     roots,
                 })
        {
            var ordered = Recent(pool);
            if (ordered.Count > 0) return ordered[0];
        }
        return Recent(teams)[0];
    }

    /// <summary>
    /// Try-order used by the system controller: pick_target_pid first (session-aware),
    /// then any teams_target_pids not already present (capture.SystemLoopback._resolve_teams_pids).
    /// <paramref name="log"/> receives ONE decision trace (which Teams processes exist, which
    /// own (active) render sessions, and the final order) — this is what tells us, from a real
    /// meeting's echo.log, why process-loopback picked the wrong target if it ever does.
    /// </summary>
    public static List<uint> ResolveTeamsPids(Action<string>? log = null)
    {
        var pids = new List<uint>();
        try
        {
            var target = PickTargetPid();
            if (target is not null) pids.Add(target.Value);
        }
        catch { /* best effort */ }
        try
        {
            foreach (var p in TeamsTargetPids())
                if (!pids.Contains(p)) pids.Add(p);
        }
        catch { /* best effort */ }

        if (log is not null)
        {
            try
            {
                var procs = ToolhelpProcesses();
                var (any, act) = RenderSessionPids();
                string Describe(uint pid)
                {
                    var name = procs.TryGetValue(pid, out var v) ? v.Name : "?";
                    var tag = act.Contains(pid) ? "+actif" : any.Contains(pid) ? "+session" : "";
                    return $"{pid}({name}{tag})";
                }
                log($"[Echo.Audio] cibles process-loopback : {string.Join(" > ", pids.Select(Describe))}");
            }
            catch { /* diagnostics only */ }
        }
        return pids;
    }
}
