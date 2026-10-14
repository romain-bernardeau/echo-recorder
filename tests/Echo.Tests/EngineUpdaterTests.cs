using Echo.Core;
using Echo.App.Engine;

namespace Echo.Tests;

/// <summary>
/// Updater: version compare, relative zip url resolution, the refusal of unsafe packages
/// (no sha256, plain http) and the generated applier script (UTF-8 code page for accented
/// paths, wait on the real Echo PID, robocopy failure → restore the previous version).
/// </summary>
public sealed class EngineUpdaterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "echo-upd-" + Guid.NewGuid().ToString("N"));
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    public EngineUpdaterTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Theory]
    [InlineData("4.10", "4.9", 1)]
    [InlineData("5.5.0", "5.5", 0)]
    [InlineData("5.5.1", "5.5.0", 1)]
    [InlineData("5.4.9", "5.5.0", -1)]
    [InlineData("v5.6.0-beta", "5.5.0", 1)]
    [InlineData("", "0", 0)]
    public void CompareVt_IsNumeric(string a, string b, int sign)
        => Assert.Equal(sign, Math.Sign(Updater.CompareVt(a, b)));

    [Fact]
    public void ResolveUrl_RelativeZip_AgainstALocalVersionJson()
    {
        var vj = Path.Combine(_dir, "MAJ", "version.json");
        Assert.Equal(Path.Combine(_dir, "MAJ", "Echo-5.6.0.zip"), Updater.ResolveUrl(vj, "Echo-5.6.0.zip"));
    }

    [Fact]
    public void ResolveUrl_RelativeZip_AgainstAnHttpsVersionJson()
        => Assert.Equal("https://example.org/echo/Echo-5.6.0.zip",
                        Updater.ResolveUrl("https://example.org/echo/version.json", "Echo-5.6.0.zip"));

    [Theory]
    [InlineData(@"\\server\share\Echo-5.6.0.zip")]
    [InlineData(@"D:\MAJ\Echo-5.6.0.zip")]
    [InlineData("https://cdn.example.org/Echo-5.6.0.zip")]
    public void ResolveUrl_AbsoluteUrls_PassThrough(string url)
        => Assert.Equal(url, Updater.ResolveUrl(@"C:\MAJ\version.json", url));

    private const string Rel = "https://github.com/romainio/echo-recorder/releases/latest/download/";

    [Fact]
    public void Package_WithoutSha_IsRefused()
    {
        Assert.NotNull(Updater.RefusedPackage(Rel + "Echo.zip", null));
        Assert.NotNull(Updater.RefusedPackage(Rel + "Echo.zip", "  "));
        Assert.NotNull(Updater.RefusedPackage(Rel + "Echo.zip", "abc"));          // not a sha256
        Assert.Null(Updater.RefusedPackage(Rel + "Echo.zip", Sha));
    }

    [Theory]
    [InlineData("http://github.com/romainio/echo-recorder/releases/latest/download/Echo.zip")]
    [InlineData("https://example.org/Echo.zip")]
    [InlineData("https://github.com/someone/echo-recorder/releases/latest/download/Echo.zip")]
    [InlineData("https://github.com/romainio/echo-recorder/releases/../../../evil/Echo.zip")]
    [InlineData("https://github.com.evil.org/romainio/echo-recorder/releases/Echo.zip")]
    [InlineData(@"C:\MAJ\Echo.zip")]
    [InlineData(@"\\srv\share\Echo.zip")]
    public void OnlyTheRepositoryReleases_AreAccepted(string url)
    {
        Assert.NotNull(Updater.RefusedPackage(url, Sha));
        Assert.NotNull(Updater.RefusedSource(url));
    }

    [Fact]
    public void TheDefaultSource_IsAccepted()
        => Assert.Null(Updater.RefusedSource(EchoConstants.DefaultUpdateUrl));
    [Fact]
    public async Task Download_WithoutSha_ThrowsBeforeReadingAnything()
    {
        var zip = Path.Combine(_dir, "Echo.zip");
        File.WriteAllBytes(zip, new byte[] { 1, 2, 3 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Updater.DownloadAndStageAsync(zip, null));
    }

    [Fact]
    public void Check_VersionJsonWithoutSha_IsNotAdvertised()
    {
        var info = Updater.Evaluate(Rel + "version.json", "{\"version\":\"99.0.0\",\"url\":\"Echo-99.0.0.zip\"}", "5.5.0");
        Assert.NotNull(info);
        Assert.False(info!.Available);
    }

    [Fact]
    public void Check_CompleteVersionJson_IsAdvertised()
    {
        var info = Updater.Evaluate(Rel + "version.json", $"{{\"version\":\"99.0.0\",\"url\":\"Echo-99.0.0.zip\",\"sha256\":\"{Sha}\"}}", "5.5.0");
        Assert.True(info!.Available);
        Assert.Equal(Rel + "Echo-99.0.0.zip", info.Url);
    }

    [Fact]
    public void Check_LocalVersionJson_IsIgnored()
    {
        var vj = Path.Combine(_dir, "version.json");
        File.WriteAllText(vj, $"{{\"version\":\"99.0.0\",\"url\":\"Echo-99.0.0.zip\",\"sha256\":\"{Sha}\"}}");
        Assert.Null(Updater.Check(vj, "5.5.0"));
    }

    [Fact]
    public void Evaluate_AbsoluteZipElsewhere_IsNotAdvertised()
    {
        var info = Updater.Evaluate(Rel + "version.json", $"{{\"version\":\"99.0.0\",\"url\":\"https://evil.example/Echo.zip\",\"sha256\":\"{Sha}\"}}", "5.5.0");
        Assert.False(info!.Available);
    }

    [Fact]
    public void ApplyScript_SetsUtf8First_WaitsForThePid_AndRestoresOnFailure()
    {
        var app = @"C:\Users\Hélène\AppData\Local\Programs\Echo";
        var script = Updater.BuildApplyScript(@"C:\Temp\echo_update", app, pid: 4242);
        var lines = script.Replace("\r", "").Split('\n');

        Assert.Equal("@echo off", lines[0]);
        Assert.Equal("chcp 65001 >nul", lines[1]);                      // before any accented path
        Assert.True(lines.Take(2).All(l => l.All(c => c < 128)));
        Assert.Contains("tasklist /FI \"PID eq 4242\"", script);
        Assert.Contains("goto waitexit", script);
        Assert.DoesNotContain("ping 127.0.0.1 -n 3", script);           // the old fixed delay
        Assert.Contains("if errorlevel 8 goto backupfail", script);
        Assert.Contains("if errorlevel 8 goto applyfail", script);
        // On a failed copy the previous version is put back before relaunching.
        int fail = Array.IndexOf(lines, ":applyfail");
        Assert.True(fail > 0);
        Assert.StartsWith($"robocopy \"{app}\\.previous\" \"{app}\"", lines[fail + 1]);
        Assert.Contains($"start \"\" \"{app}\\Echo.exe\"", script);
    }

    [Fact]
    public void RollbackScript_WaitsForThePid_ThenRestores()
    {
        var script = Updater.BuildRollbackScript(@"C:\Apps\Echo", pid: 77);
        Assert.Contains("chcp 65001 >nul", script);
        Assert.Contains("tasklist /FI \"PID eq 77\"", script);
        Assert.Contains("robocopy \"C:\\Apps\\Echo\\.previous\" \"C:\\Apps\\Echo\"", script);
    }
}
