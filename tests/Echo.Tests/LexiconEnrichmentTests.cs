using Echo.Ai;

namespace Echo.Tests;

/// <summary>
/// Covers the SAFE mis-transcription variants added to the shipped generic lexicon
/// (<c>lexicon_default.txt</c>) so at-risk procurement/finance/consulting terms are
/// now DETERMINISTICALLY corrected, not merely biased. Three angles:
///   1. positive: added variants, embedded in a sentence, canonicalise;
///   2. guard: common French words / lookalikes MUST pass through untouched
///      (a blind whole-word replace would otherwise silently rewrite good text);
///   3. deliberate non-enrichment: a term left BARE because its mishearing is a real word.
/// Only invented or generic tokens appear here (SteerCo/Coupa/S2C/EBITDA/Kraljic...);
/// no client data. The one accented char we need is built from its code point so the
/// source stays pure ASCII (the rest of this test project avoids accented literals too).
/// </summary>
public class LexiconEnrichmentTests
{
    /// <summary>'e' with acute accent (U+00E9), built from the code point to keep the source ASCII.</summary>
    private const char EAcute = (char)0x00E9;

    // ── positive: added variants get corrected to their canonical ───────────────

    [Theory]
    // S2C / S2P letter-spelling mishearings (the "essai to see" failure mode is
    // documented in the lexicon header; S2P is its direct analogue).
    [InlineData("on lance le essai to see demain", "on lance le S2C demain")]
    [InlineData("le chantier essai to pi avance", "le chantier S2P avance")]
    // Tool name Coupa -> "koupa" (the k-spelling the repo's own postprocess tests use).
    [InlineData("on deploie koupa cette annee", "on deploie Coupa cette annee")]
    // Foreign name Kraljic, reliably mangled by a French ASR.
    [InlineData("la matrice kraljik est utile", "la matrice Kraljic est utile")]
    [InlineData("appliquer kraljique au portefeuille", "appliquer Kraljic au portefeuille")]
    // Finance acronym: French schwa-final rendering of capex.
    [InlineData("le capexe est valide", "le capex est valide")]
    // e-* agglutinations (hyphen dropped, "e" heard as "e"/"i").
    [InlineData("faire du esourcing en interne", "faire du e-sourcing en interne")]
    [InlineData("une plateforme de iprocurement", "une plateforme de e-procurement")]
    public void AddedVariants_AreCanonicalised(string input, string expected)
        => Assert.Equal(expected, Vocab().CanonicalizeDeterministic(input));

    /// <summary>EBITDA's only distinct variant is the French-accented "ebitda" (with an
    /// acute e); assert it canonicalises. Built from the code point to keep source ASCII.</summary>
    [Fact]
    public void EbitdaAccentedVariant_IsCanonicalised()
    {
        var input = $"un {EAcute}bitda solide ce trimestre";
        Assert.Equal("un EBITDA solide ce trimestre", Vocab().CanonicalizeDeterministic(input));
    }

    // ── guard: legitimate words / lookalikes MUST NOT be rewritten ──────────────

    [Theory]
    // "essai" alone is an everyday French word - only the full "essai to see" run maps.
    [InlineData("un essai concluant ce matin")]
    // COPIL left BARE on purpose: "copilote" is a real, business-used French word.
    [InlineData("le copilote du projet est absent")]
    // "action" must survive the e-auction variants ("eauction"/"iauction").
    [InlineData("une action corrective immediate")]
    // Word-boundary safety: "esourcing" must not fire inside "resourcing".
    [InlineData("le resourcing des equipes se poursuit")]
    // S2P's multi-word variant must not fire on the bare maths constant "pi".
    [InlineData("la valeur de pi en mathematiques")]
    // "capex" (the canonical itself) is untouched; only "capexe" is a variant.
    [InlineData("le capex annuel du groupe")]
    public void CommonWords_PassThroughUnchanged(string input)
        => Assert.Equal(input, Vocab().CanonicalizeDeterministic(input));

    /// <summary>Ivalua is left BARE on purpose: its natural mishearing "evalua" (with an
    /// acute e) is a real French verb form, so it must pass through untouched. Guards the
    /// deliberate decision NOT to enrich terms whose mishearing collides with a real word.</summary>
    [Fact]
    public void EvaluaRealWord_PassesThroughUnchanged()
    {
        var input = $"il {EAcute}valua les offres recues";
        Assert.Equal(input, Vocab().CanonicalizeDeterministic(input));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    /// <summary>A Vocabulary reading the SHIPPED generic lexicon we enriched, with an
    /// empty (non-existent) mission bias file and no env, so only the default lexicon
    /// drives the deterministic pass.</summary>
    private static Vocabulary Vocab()
    {
        var lex = SourceLexicon();
        Assert.True(File.Exists(lex), $"shipped lexicon_default.txt not found from {AppContext.BaseDirectory}");
        var noMission = Path.Combine(Path.GetTempPath(), $"nomission_{Guid.NewGuid():N}.txt");
        return new Vocabulary(defaultLexiconPath: lex, missionBiasPath: noMission, env: _ => null);
    }

    /// <summary>Locate the versioned SOURCE lexicon by walking up to
    /// <c>src\Echo.Ai\Resources\lexicon_default.txt</c> (so we exercise the file we edit,
    /// never a stale build copy); fall back to the copy beside the test assembly.</summary>
    private static string SourceLexicon()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Echo.Ai", "Resources", "lexicon_default.txt");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "Resources", "lexicon_default.txt");
    }
}
