using Echo.Ai;
using Echo.Core.Models;

namespace Echo.Tests;

/// <summary>
/// Covers PER-MISSION business vocabulary: a session linked to a mission (via
/// <c>&lt;sessionDir&gt;\flag.txt</c>) layers its own file
/// (<c>&lt;vocabDir&gt;\&lt;id&gt;.txt</c>) as the HIGHEST-priority lexicon source, IN
/// ADDITION to the global client bias and the shipped system lexicon
/// (mission-specific &gt; global &gt; system). Exercises:
///   1. resolution from a fake flag.txt in a temp session dir (+ the negative cases);
///   2. a mission variant CORRECTS, a global term STILL applies ("en plus", not replacing);
///   3. priority/dedup: a term in several layers appears once, the mission spelling wins,
///      and a conflicting variant maps to the mission canonical.
/// Only invented ASCII tokens appear (Zbragflorp, Wobblenaut...); no client data.
/// </summary>
public class MissionVocabularyTests
{
    // ── resolution from flag.txt ────────────────────────────────────────────────

    [Fact]
    public void ForSession_LayersMissionFile_ResolvedFromFlagTxt()
    {
        using var ws = new Workspace();
        ws.WriteMission("m123", "zbrag|z brag=>Zbragflorp\n");
        var sd = ws.NewSession(flagId: "m123");

        var vocab = ws.BaseVocab().ForSession(sd);

        Assert.Equal("m123", Path.GetFileNameWithoutExtension(vocab.SessionMissionPath!));
        Assert.Equal("le Zbragflorp tourne", vocab.CanonicalizeDeterministic("le zbrag tourne"));
        Assert.Equal("le Zbragflorp", vocab.CanonicalizeDeterministic("le z brag"));
    }

    [Fact]
    public void ForSession_NoFlagFile_ReturnsSharedVocab_Unlayered()
    {
        using var ws = new Workspace();
        ws.WriteMission("m123", "zbrag=>Zbragflorp\n");
        var sd = ws.NewSession(flagId: null);   // no flag.txt

        var baseVocab = ws.BaseVocab();
        var vocab = baseVocab.ForSession(sd);

        Assert.Same(baseVocab, vocab);                 // identical instance, no layer added
        Assert.Null(vocab.SessionMissionPath);
        Assert.Equal("le zbrag", vocab.CanonicalizeDeterministic("le zbrag")); // untouched
    }

    [Fact]
    public void ForSession_FlagWithoutMatchingFile_ReturnsSharedVocab()
    {
        using var ws = new Workspace();
        // flag names a mission whose vocab file does NOT exist -> no layer.
        var sd = ws.NewSession(flagId: "mDOESNOTEXIST");

        var baseVocab = ws.BaseVocab();
        var vocab = baseVocab.ForSession(sd);

        Assert.Same(baseVocab, vocab);
        Assert.Null(vocab.SessionMissionPath);
    }

    [Fact]
    public void ResolveMissionVocabPath_RejectsUnsafeId()
    {
        using var ws = new Workspace();
        ws.WriteMission("m123", "x\n");
        var sd = ws.NewSession(flagId: "..\\..\\m123");   // path-traversal attempt

        Assert.Null(Vocabulary.ResolveMissionVocabPath(sd, ws.Env));
    }

    // ── "en plus": mission augments, never replaces ─────────────────────────────

    [Fact]
    public void SessionVocab_AppliesMissionAndGlobalAndSystem_Corrections()
    {
        using var ws = new Workspace(
            defaultContent: "koupa=>Coupa\n",       // system layer
            globalContent:  "wibble=>Wobblenaut\n"); // global client layer
        ws.WriteMission("mX", "zbrag=>Zbragflorp\n"); // per-mission layer
        var sd = ws.NewSession(flagId: "mX");

        var vocab = ws.BaseVocab().ForSession(sd);

        Assert.Equal("Zbragflorp", vocab.CanonicalizeDeterministic("zbrag"));   // mission
        Assert.Equal("Wobblenaut", vocab.CanonicalizeDeterministic("wibble"));  // global still applies
        Assert.Equal("Coupa", vocab.CanonicalizeDeterministic("koupa"));        // system still applies
    }

    [Fact]
    public void SessionVocab_Glossary_ContainsAllThreeLayers()
    {
        using var ws = new Workspace(
            defaultContent: "Systemterm\n",
            globalContent:  "Globalterm\n");
        ws.WriteMission("mX", "Missionterm\n");
        var sd = ws.NewSession(flagId: "mX");

        var terms = ws.BaseVocab().ForSession(sd).GlossaryTerms();

        Assert.Contains("Missionterm", terms);
        Assert.Contains("Globalterm", terms);
        Assert.Contains("Systemterm", terms);
    }

    // ── priority / dedup: mission spelling wins ─────────────────────────────────

    [Fact]
    public void SessionVocab_DedupsCanonical_MissionSpellingWins()
    {
        using var ws = new Workspace(globalContent: "Acmecorp\n");
        ws.WriteMission("mX", "ACMECORP\n");   // same term, different spelling, higher priority
        var sd = ws.NewSession(flagId: "mX");

        var terms = ws.BaseVocab().ForSession(sd).GlossaryTerms();

        Assert.Contains("ACMECORP", terms);        // mission spelling kept
        Assert.DoesNotContain("Acmecorp", terms);  // global spelling dropped
        Assert.Single(terms.Where(t => string.Equals(t, "acmecorp", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void SessionVocab_ConflictingVariant_MapsToMissionCanonical()
    {
        // Same variant "xyz" points to different canonicals in the mission and global layers;
        // the higher-priority mission layer must win (VariantMap is first-wins by priority).
        using var ws = new Workspace(globalContent: "xyz=>GlobalCanon\n");
        ws.WriteMission("mX", "xyz=>MissionCanon\n");
        var sd = ws.NewSession(flagId: "mX");

        var vocab = ws.BaseVocab().ForSession(sd);

        Assert.Equal("le MissionCanon final", vocab.CanonicalizeDeterministic("le xyz final"));
    }

    // ── Voxtral context-bias list ───────────────────────────────────────────────

    [Fact]
    public void BuildContextBias_IncludesMissionTerms_MissionRankedFirst()
    {
        using var ws = new Workspace(
            defaultContent: "Systemterm\n",
            globalContent:  "Globalterm\n");
        ws.WriteMission("mX", "Missionterm\n");
        var sd = ws.NewSession(flagId: "mX");

        var bias = ws.BaseVocab().ForSession(sd)
            .BuildContextBias(new AppSettings(), Array.Empty<string>(), missionHint: null);

        Assert.Contains("Missionterm", bias);
        Assert.Contains("Globalterm", bias);
        Assert.Contains("Systemterm", bias);
        Assert.True(bias.ToList().IndexOf("Missionterm") < bias.ToList().IndexOf("Globalterm"),
            "mission term must outrank the global term in the bias list");
    }

    [Fact]
    public void LayerMissionBias_PrependsMissionTerms_ButLeavesSharedVocabUntouched()
    {
        using var ws = new Workspace();
        ws.WriteMission("mX", "Missionterm\n");
        var sd = ws.NewSession(flagId: "mX");

        var baseVocab = ws.BaseVocab();
        var caller = new[] { "Existingterm" };

        // Shared (non-session) vocab: caller bias returned unchanged (same reference).
        Assert.Same(caller, baseVocab.LayerMissionBias(caller));

        // Session-scoped vocab: mission term prepended (highest priority), caller kept.
        var layered = baseVocab.ForSession(sd).LayerMissionBias(caller);
        Assert.Equal("Missionterm", layered[0]);
        Assert.Contains("Existingterm", layered);
    }

    // ── workspace helper ─────────────────────────────────────────────────────────

    /// <summary>Isolated temp workspace: a shipped-system lexicon, a global client bias file,
    /// a per-mission vocab dir (exposed via ECHO_VOCAB_DIR), and throwaway session dirs.</summary>
    private sealed class Workspace : IDisposable
    {
        public string Root { get; }
        public string DefaultLexicon { get; }
        public string GlobalBias { get; }
        public string VocabDir { get; }

        public Workspace(string defaultContent = "", string globalContent = "")
        {
            Root = Path.Combine(Path.GetTempPath(), $"echo_mv_{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            DefaultLexicon = Path.Combine(Root, "lexicon_default.txt");
            File.WriteAllText(DefaultLexicon, defaultContent);
            GlobalBias = Path.Combine(Root, "context_bias.txt");
            File.WriteAllText(GlobalBias, globalContent);
            VocabDir = Path.Combine(Root, "vocab");
            Directory.CreateDirectory(VocabDir);
        }

        /// <summary>Env reader routing ECHO_VOCAB_DIR to this workspace's vocab dir (else null).</summary>
        public Func<string, string?> Env => k => k == Vocabulary.EnvVocabDir ? VocabDir : null;

        public Vocabulary BaseVocab()
            => new Vocabulary(defaultLexiconPath: DefaultLexicon, missionBiasPath: GlobalBias, env: Env);

        public string WriteMission(string id, string content)
        {
            var p = Path.Combine(VocabDir, id + ".txt");
            File.WriteAllText(p, content);
            return p;
        }

        public string NewSession(string? flagId)
        {
            var sd = Path.Combine(Root, $"sess_{Guid.NewGuid():N}");
            Directory.CreateDirectory(sd);
            if (flagId is not null) File.WriteAllText(Path.Combine(sd, "flag.txt"), flagId);
            return sd;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch { /* best effort */ }
        }
    }
}
