using Echo.Ai;

namespace Echo.Tests;

/// <summary>
/// Covers the vocabulary-editor mechanism exposed by the Bridge (read_vocab /
/// write_vocab / import_vocab): the shipped default lexicon now auto-corrects the
/// "Stirco" mis-transcription, and <see cref="Vocabulary.Reload"/> re-reads the
/// mission bias file so edits/imports apply without an app restart. Only invented
/// tokens appear (SteerCo); no client data.
/// </summary>
public class VocabularyReloadTests
{
    // ── out-of-the-box fix: default lexicon canonicalises Stirco -> SteerCo ──────

    [Fact]
    public void DefaultLexicon_CanonicalizesStircoVariants_ToSteerCo()
    {
        var lex = FindDefaultLexicon();
        Assert.True(File.Exists(lex), $"shipped lexicon_default.txt not found (looked from {AppContext.BaseDirectory})");

        var noMission = Path.Combine(Path.GetTempPath(), $"nomission_{Guid.NewGuid():N}.txt");
        var vocab = new Vocabulary(defaultLexiconPath: lex, missionBiasPath: noMission, env: _ => null);

        Assert.Equal("Le SteerCo se reunit", vocab.CanonicalizeDeterministic("Le Stirco se reunit"));
        Assert.Equal("point SteerCo demain", vocab.CanonicalizeDeterministic("point stir co demain"));
        Assert.Equal("le SteerCo", vocab.CanonicalizeDeterministic("le steer co"));
    }

    // ── write/import round-trip: Reload re-reads the mission file ────────────────

    [Fact]
    public void Vocabulary_Reload_PicksUpMissionFileEdits()
    {
        var mission = Path.Combine(Path.GetTempPath(), $"mission_{Guid.NewGuid():N}.txt");
        var emptyDefault = Path.Combine(Path.GetTempPath(), $"def_{Guid.NewGuid():N}.txt");
        File.WriteAllText(mission, "");
        File.WriteAllText(emptyDefault, "");
        try
        {
            var vocab = new Vocabulary(defaultLexiconPath: emptyDefault, missionBiasPath: mission, env: _ => null);
            Assert.Equal("Stirco", vocab.CanonicalizeDeterministic("Stirco"));   // nothing loaded yet

            // Simulate write_vocab / import_vocab writing the mission bias file.
            File.WriteAllText(mission, "stirco|stir co=>SteerCo\n");
            vocab.Reload();

            Assert.Equal("SteerCo", vocab.CanonicalizeDeterministic("Stirco"));  // correction applies after reload
        }
        finally { File.Delete(mission); File.Delete(emptyDefault); }
    }

    /// <summary>Locate the shipped default lexicon: the copy beside the test assembly if
    /// present, else the source file by walking up to the repo's <c>src\Echo.Ai\Resources</c>.</summary>
    private static string FindDefaultLexicon()
    {
        var flat = Path.Combine(AppContext.BaseDirectory, "Resources", "lexicon_default.txt");
        if (File.Exists(flat)) return flat;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Echo.Ai", "Resources", "lexicon_default.txt");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return flat;
    }
}
