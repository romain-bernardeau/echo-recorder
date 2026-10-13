using Echo.App.Engine;
using Echo.Core;
using Echo.Core.Models;

namespace Echo.Tests;

/// <summary>
/// 5.7.1: every install checks the latest GitHub release for updates. Installs made from a
/// synced shared folder pointed at a local "…\MAJ\version.json", which stops working once the
/// folder is no longer synced; they are moved to the GitHub URL on load.
/// </summary>
public class GitHubUpdateTests
{
    private const string GitHub = "https://github.com/romain-bernardeau/echo-recorder/releases/latest/download/version.json";

    [Fact]
    public void NewInstall_DefaultsToGitHub() => Assert.Equal(GitHub, new AppSettings().UpdateUrl);

    [Theory]
    [InlineData(@"C:\\Users\\someone\\Shared - Echo\\MAJ\\version.json")]   // synced shared folder
    [InlineData(@"\\\\server\\share\\MAJ\\version.json")]                  // UNC path
    [InlineData("")]                                                        // updates were off
    public void LocalOrEmptySource_MovesToGitHub(string jsonEscapedUrl)
    {
        using var p = new TempProfile();
        Directory.CreateDirectory(Path.GetDirectoryName(p.SettingsFile)!);
        File.WriteAllText(p.SettingsFile, "{\"update_url\":\"" + jsonEscapedUrl + "\",\"cr_provider\":\"cloudflare\"}");

        var s = p.Store().Current;

        Assert.Equal(GitHub, s.UpdateUrl);
        Assert.Equal("cloudflare", s.CrProvider);                            // nothing else touched
    }

    [Fact]
    public void CustomHttpsSource_IsKept()
    {
        using var p = new TempProfile();
        Directory.CreateDirectory(Path.GetDirectoryName(p.SettingsFile)!);
        File.WriteAllText(p.SettingsFile, "{\"update_url\":\"https://example.com/echo/version.json\"}");

        Assert.Equal("https://example.com/echo/version.json", p.Store().Current.UpdateUrl);
    }

    [Fact]
    public void RelativeZip_ResolvesNextToTheLatestReleaseAsset()
        => Assert.Equal("https://github.com/romain-bernardeau/echo-recorder/releases/latest/download/Echo-5.7.1.zip",
                        Updater.ResolveUrl(EchoConstants.DefaultUpdateUrl, "Echo-5.7.1.zip"));
}
