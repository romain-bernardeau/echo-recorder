namespace Echo.Tests;

/// <summary>
/// 5.6.2: a free Mistral key gets 0 req/min on mistral-small/medium (2026-10-13), so existing
/// installs move once to the free default (ministral-8b-latest). The migration is one-shot:
/// a paid user who then picks mistral-small again keeps it.
/// </summary>
public class FreeModelsMigrationTests
{
    [Fact]
    public void PaidMistralModels_MovedOnceToFreeDefault()
    {
        using var p = new TempProfile();
        Directory.CreateDirectory(Path.GetDirectoryName(p.SettingsFile)!);
        File.WriteAllText(p.SettingsFile,
            "{\"mistral_cr_model\":\"mistral-small-latest\",\"postprocess_model\":\"mistral-large-latest\",\"cr_provider\":\"cloudflare\"}");

        var s = p.Store().Current;

        Assert.Equal("ministral-8b-latest", s.MistralCrModel);
        Assert.Equal("ministral-8b-latest", s.PostprocessModel);
        Assert.Equal("cloudflare", s.CrProvider);                      // unrelated choices untouched
        Assert.Equal(2, s.ModelsMigration);
    }

    [Fact]
    public void AfterMigration_UserCanPickSmallAgain()
    {
        using var p = new TempProfile();
        var store = p.Store();                                         // fresh install: defaults + marker
        store.Set("mistral_cr_model", "mistral-small-latest");

        var reloaded = p.Store().Current;

        Assert.Equal("mistral-small-latest", reloaded.MistralCrModel);
        Assert.Equal(2, reloaded.ModelsMigration);
    }
}
