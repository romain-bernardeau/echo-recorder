using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Echo.Core.Models;

namespace Echo.App.Engine;

/// <summary>
/// Admin-free update via a remote <c>version.json</c> (https URL OR a synced
/// SharePoint/OneDrive folder path). Faithful port of <c>updater.py</c>: numeric-tuple
/// version compare, sha256 (mandatory) + zip-CRC integrity, staging in
/// <c>%TEMP%\echo_update</c>, a one-deep <c>.previous</c> backup, and a detached
/// applier/rollback that runs AFTER the app closes (so locked runtime files copy) then
/// relaunches. The applier is a small <c>.cmd</c> (run by cmd.exe, not the locked
/// Echo.exe) that backs up, robocopies with retry-on-lock, and relaunches Echo.exe.
/// </summary>
public static class Updater
{
    private const string BackupDir = ".previous";
    // Top-level dirs never backed up/restored (heavy or off-code); + "*.log".
    private static readonly string[] SkipTop = { BackupDir, "recordings", "dist", "build" };

    // ── version.json check ───────────────────────────────────────────────────────

    /// <summary>Returns an <see cref="UpdateInfo"/> or null (no URL / error). Comparison is
    /// numeric-tuple so <c>4.10 &gt; 4.9</c>. An update that could not be installed safely (no
    /// sha256, or a plain-http source) is reported as NOT available, with the reason logged:
    /// advertising it would only end in a refused download.</summary>
    public static UpdateInfo? Check(string updateUrl, string currentVersion)
    {
        if (string.IsNullOrWhiteSpace(updateUrl)) return null;
        if (RefusedSource(updateUrl) is { } why0) { Log.Info($"[update] {why0} : {updateUrl}"); return null; }
        string text;
        try { text = ReadRemoteText(updateUrl, TimeSpan.FromSeconds(10)); }
        catch (Exception ex) { Log.Error("update check", ex); return null; }
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            string remote = GetStr(root, "version");
            if (string.IsNullOrWhiteSpace(remote)) return null;
            var url = ResolveUrl(updateUrl, GetStr(root, "url"));
            var sha = GetStr(root, "sha256");
            bool newer = CompareVt(remote, currentVersion) > 0;
            var refused = RefusedPackage(url, sha);
            if (newer && refused is not null) Log.Info($"[update] version {remote} ignorée : {refused}");
            return new UpdateInfo(
                Available: newer && refused is null,
                Version: remote,
                Url: url,
                Notes: GetStr(root, "notes"),
                Sha256: sha);
        }
        catch (Exception ex) { Log.Error("update parse", ex); return null; }
    }

    /// <summary>Why an update source must not be used, or null when acceptable: https or a
    /// local / UNC / synced-folder path only (plain http can be altered in transit).</summary>
    internal static string? RefusedSource(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "adresse vide";
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return "source http:// refusée (https ou dossier local uniquement)";
        return null;
    }

    /// <summary>Why a package must not be installed, or null: the sha256 is MANDATORY (the zip
    /// CRC only catches accidental corruption, not a replaced archive), and the zip source
    /// follows the same https / local rule as version.json.</summary>
    internal static string? RefusedPackage(string? zipUrl, string? sha256)
    {
        if (string.IsNullOrWhiteSpace(zipUrl)) return "url du paquet absente";
        if (RefusedSource(zipUrl!) is { } why) return why;
        var sha = (sha256 ?? "").Trim();
        if (sha.Length == 0) return "sha256 absent du version.json";
        if (sha.Length != 64 || !sha.All(Uri.IsHexDigit)) return "sha256 invalide dans version.json";
        return null;
    }

    // ── download + stage ───────────────────────────────────────────────────────

    /// <summary>Downloads the update zip, VERIFIES integrity (sha256, mandatory, + zip CRC via
    /// extraction), extracts to <c>%TEMP%\echo_update</c>, and descends into a single top-level
    /// directory if present. Never applies a corrupt/altered archive (MITM / truncation), nor
    /// one that comes without its sha256 or over plain http.
    /// No Authenticode check: the packages are not code-signed (no certificate).</summary>
    public static async Task<string> DownloadAndStageAsync(string zipUrl, string? expectedSha256)
    {
        if (RefusedPackage(zipUrl, expectedSha256) is { } why)
            throw new InvalidOperationException($"mise à jour refusée : {why}");
        var data = await ReadRemoteBytesAsync(zipUrl, TimeSpan.FromSeconds(120));
        var actual = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        var expect = expectedSha256!.Trim().ToLowerInvariant();
        if (actual != expect)
            throw new InvalidOperationException(
                $"integrity KO: sha256 {actual[..12]}... != expected {expect[..Math.Min(12, expect.Length)]}...");
        var staging = Path.Combine(Path.GetTempPath(), "echo_update");
        try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
        Directory.CreateDirectory(staging);
        var zp = Path.Combine(staging, "_update.zip");
        await File.WriteAllBytesAsync(zp, data);

        // ExtractToDirectory validates each entry's CRC and throws on a corrupt archive.
        ZipFile.ExtractToDirectory(zp, staging, overwriteFiles: true);
        File.Delete(zp);

        var entries = Directory.GetFileSystemEntries(staging);
        if (entries.Length == 1 && Directory.Exists(entries[0])) return entries[0];
        return staging;
    }

    /// <summary>True if a non-empty <c>&lt;app_dir&gt;\.previous</c> backup is restorable.</summary>
    public static bool HasBackup(string appDir)
    {
        try
        {
            var b = Path.Combine(appDir, BackupDir);
            return Directory.Exists(b) && Directory.EnumerateFileSystemEntries(b).Any();
        }
        catch { return false; }
    }

    // ── detached applier / rollback ──────────────────────────────────────────────

    /// <summary>Seconds the applier waits for Echo to exit before giving up (the user may
    /// have cancelled the quit: the update is then simply not applied).</summary>
    internal const int ExitWaitSeconds = 180;

    /// <summary>Writes and launches a detached applier .cmd: waits for the app to exit, snapshots
    /// the current install into <c>.previous</c>, robocopies staging over the app dir (retry on
    /// lock), removes staging, relaunches Echo.exe. A failed copy restores <c>.previous</c> and
    /// relaunches the previous version.</summary>
    public static void SpawnApply(string staging, string appDir, int? pid = null)
        => LaunchCmd(BuildApplyScript(staging, appDir, pid ?? Environment.ProcessId), "echo_update_apply.cmd", appDir);

    /// <summary>Writes and launches a detached rollback .cmd: waits for the app to exit, restores
    /// <c>.previous</c> over the app dir (retry on lock), relaunches Echo.exe.</summary>
    public static void SpawnRollback(string appDir, int? pid = null)
        => LaunchCmd(BuildRollbackScript(appDir, pid ?? Environment.ProcessId), "echo_update_rollback.cmd", appDir);

    /// <summary>The applier script (pure, for the tests). Robocopy exit codes 0-7 are success
    /// variants; 8 and above mean files were NOT copied (<c>if errorlevel 8</c>).</summary>
    internal static string BuildApplyScript(string staging, string appDir, int pid)
    {
        var exe = Path.Combine(appDir, "Echo.exe");
        var prev = Path.Combine(appDir, BackupDir);
        var xd = string.Join(" ", SkipTop.Select(n => Q(Path.Combine(appDir, n))));

        var sb = ScriptHeader(pid);
        sb.AppendLine($"rmdir /s /q {Q(prev)} 2>nul");                   // keep a single previous
        // backup current install (code + assets) minus heavy/off-code dirs and logs
        sb.AppendLine($"robocopy {Q(appDir)} {Q(prev)} /E /R:2 /W:1 /XD {xd} /XF *.log /NFL /NDL /NJH /NJS /NP >nul");
        sb.AppendLine("if errorlevel 8 goto backupfail");                // no safe backup: do not touch the install
        // apply the staged update (retry locked files as the app finishes closing)
        sb.AppendLine($"robocopy {Q(staging)} {Q(appDir)} /E /R:30 /W:1 /NFL /NDL /NJH /NJS /NP >nul");
        sb.AppendLine("if errorlevel 8 goto applyfail");
        sb.AppendLine($"rmdir /s /q {Q(staging)} 2>nul");
        sb.AppendLine($"start \"\" {Q(exe)}");
        sb.AppendLine("goto done");
        sb.AppendLine(":applyfail");
        // half-copied install: put the previous version back, then start it
        sb.AppendLine($"robocopy {Q(prev)} {Q(appDir)} /E /R:30 /W:1 /NFL /NDL /NJH /NJS /NP >nul");
        sb.AppendLine(":backupfail");
        sb.AppendLine($"start \"\" {Q(exe)}");
        sb.AppendLine(":done");
        sb.AppendLine("del /q \"%~f0\"");
        return sb.ToString();
    }

    internal static string BuildRollbackScript(string appDir, int pid)
    {
        var exe = Path.Combine(appDir, "Echo.exe");
        var prev = Path.Combine(appDir, BackupDir);

        var sb = ScriptHeader(pid);
        sb.AppendLine($"robocopy {Q(prev)} {Q(appDir)} /E /R:30 /W:1 /NFL /NDL /NJH /NJS /NP >nul");
        sb.AppendLine($"start \"\" {Q(exe)}");
        sb.AppendLine(":done");
        sb.AppendLine("del /q \"%~f0\"");
        return sb.ToString();
    }

    /// <summary>
    /// Common head: UTF-8 code page FIRST (the script is written in UTF-8 and cmd.exe reads it
    /// in the OEM code page otherwise, which broke every accented path, e.g. a profile named
    /// "Hélène"), then wait for the Echo process to be gone (bounded) instead of a fixed delay
    /// that a slow shutdown (capture being finalised) easily exceeded.
    /// </summary>
    private static StringBuilder ScriptHeader(int pid)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >nul");
        sb.AppendLine("set /a n=0");
        sb.AppendLine(":waitexit");
        sb.AppendLine($"tasklist /FI \"PID eq {pid}\" /NH 2>nul | find \" {pid} \" >nul");
        sb.AppendLine("if errorlevel 1 goto exited");
        sb.AppendLine("set /a n+=1");
        sb.AppendLine($"if %n% geq {ExitWaitSeconds} goto done");      // still running: leave everything as is
        sb.AppendLine("ping 127.0.0.1 -n 2 >nul");
        sb.AppendLine("goto waitexit");
        sb.AppendLine(":exited");
        sb.AppendLine("ping 127.0.0.1 -n 2 >nul");                      // let the last handles close
        return sb;
    }

    private static void LaunchCmd(string script, string fileName, string workingDir)
    {
        var cmdPath = Path.Combine(Path.GetTempPath(), fileName);
        File.WriteAllText(cmdPath, script, new UTF8Encoding(false));
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDir,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(cmdPath);
        Process.Start(psi);   // detached: survives our exit (not started in a job object)
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static string Q(string p) => "\"" + p.TrimEnd('\\') + "\"";

    /// <summary>
    /// Resolve a RELATIVE zip <paramref name="url"/> ("Echo-5.1.0.zip") against the
    /// version.json location, so ONE version.json works on every machine — each
    /// colleague's synced SharePoint folder has a different absolute path. Absolute
    /// http(s) / rooted / UNC urls pass through unchanged.
    /// </summary>
    internal static string ResolveUrl(string updateUrl, string url)
    {
        if (string.IsNullOrWhiteSpace(url) || IsHttp(url) || Path.IsPathRooted(url)) return url;
        try
        {
            if (IsHttp(updateUrl)) return new Uri(new Uri(updateUrl), url).ToString();
            var dir = Path.GetDirectoryName(Path.GetFullPath(updateUrl));
            return string.IsNullOrEmpty(dir) ? url : Path.Combine(dir, url);
        }
        catch { return url; }
    }

    /// <summary>"4.10.2" → [4,10,2]; compares numerically (shorter is smaller on equal prefix).</summary>
    internal static int CompareVt(string a, string b)
    {
        var va = Vt(a);
        var vb = Vt(b);
        int n = Math.Max(va.Length, vb.Length);
        for (int i = 0; i < n; i++)
        {
            int x = i < va.Length ? va[i] : 0;
            int y = i < vb.Length ? vb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    private static int[] Vt(string s)
    {
        var nums = Regex.Matches(s ?? "", @"\d+").Select(m => int.TryParse(m.Value, out var v) ? v : 0).Take(4).ToArray();
        return nums.Length > 0 ? nums : new[] { 0 };
    }

    private static string GetStr(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()
            : "";

    private static bool IsHttp(string url)
        => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static string ReadRemoteText(string url, TimeSpan timeout)
    {
        if (IsHttp(url))
        {
            using var http = new HttpClient { Timeout = timeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Echo-updater");
            return http.GetStringAsync(url).GetAwaiter().GetResult();
        }
        return File.ReadAllText(url);
    }

    private static async Task<byte[]> ReadRemoteBytesAsync(string url, TimeSpan timeout)
    {
        if (IsHttp(url))
        {
            using var http = new HttpClient { Timeout = timeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Echo-updater");
            return await http.GetByteArrayAsync(url);
        }
        return await File.ReadAllBytesAsync(url);
    }
}
