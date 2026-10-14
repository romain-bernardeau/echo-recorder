using Echo.App;

namespace Echo.Tests;

/// <summary>
/// 5.7.2: the Help buttons open the Markdown help on GitHub (help/README*.md) in the app
/// language, on the requested section. Tab ids of the former local help page still work.
/// </summary>
public class HelpUrlTests
{
    private const string Base = "https://github.com/romainio/echo-recorder/blob/main/help/";

    [Theory]
    [InlineData("fr", Base + "README.fr.md")]
    [InlineData("es", Base + "README.es.md")]
    [InlineData("en", Base + "README.md")]
    [InlineData("de", Base + "README.md")]   // no page in that language: English
    [InlineData(null, Base + "README.md")]
    public void PageFollowsTheAppLanguage(string? lang, string expected)
        => Assert.Equal(expected, Bridge.HelpUrl(lang, null));

    [Theory]
    [InlineData("cle-api", "api-keys")]
    [InlineData("prise-en-main", "getting-started")]
    [InlineData("enregistrer", "recording")]
    [InlineData("apres", "after-the-meeting")]
    [InlineData("depannage", "troubleshooting")]
    [InlineData("mistral", "mistral")]
    [InlineData("cloudflare", "cloudflare")]
    [InlineData("gemini", "gemini")]
    [InlineData("openrouter", "openrouter")]
    [InlineData("#Settings", "settings")]
    public void LegacyAndNewAnchorsMapToSectionIds(string anchor, string id)
        => Assert.Equal(Base + "README.fr.md#" + id, Bridge.HelpUrl("fr", anchor));

    [Theory]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("api-keys\"><script>")]
    public void UnknownAnchor_OpensTheTopOfThePage(string anchor)
        => Assert.Equal(Base + "README.md", Bridge.HelpUrl("en", anchor));
}
