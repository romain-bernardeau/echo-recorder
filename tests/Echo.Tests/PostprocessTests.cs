using System.Text.Json;
using Echo.Ai;

namespace Echo.Tests;

/// <summary>
/// Regression tests for the processing-parity port (postprocess.py LLM pass,
/// transcribe.echo_analysis text dedup, difflib ratio). No live Mistral call: the
/// LLM caller is a canned <see cref="ChatFunc"/> delegate. Any real names are
/// &lt;REDACTED&gt;; only invented tokens (SteerCo, Coupa, placeholder strings) appear.
/// </summary>
public class PostprocessTests
{
    // ── difflib ratio (character-level, isjunk=None) ────────────────────────────

    [Fact]
    public void Difflib_Ratio_MatchesKnownPythonValues()
    {
        Assert.Equal(1.0, Difflib.Ratio("", ""));                 // _calculate_ratio(0,0) == 1.0
        Assert.Equal(1.0, Difflib.Ratio("abcd", "abcd"));
        Assert.Equal(0.0, Difflib.Ratio("abc", "xyz"));           // no common chars
        Assert.Equal(2.0 * 2 / 6, Difflib.Ratio("abc", "abd"), 6);  // "ab" matches -> 0.6667
        Assert.Equal(0.4, Difflib.Ratio("aaaaaaaaaa", "aaaabbbbbb"), 6); // longest run 4 of 20 -> 0.4
    }

    // ── chunk splitting (postprocess._windows) ──────────────────────────────────

    [Fact]
    public void Windows_SplitsByMaxSegments()
    {
        var segs = new[] { "a", "b", "c", "d", "e" };
        var w = Postprocess.Windows(segs, maxSegments: 2, maxChars: 10_000);
        Assert.Equal(3, w.Count);
        Assert.Equal(new[] { 2, 2, 1 }, w.Select(x => x.Count).ToArray());
    }

    [Fact]
    public void Windows_SplitsByMaxChars()
    {
        var segs = Enumerable.Repeat(new string('x', 10), 5).ToArray();
        var w = Postprocess.Windows(segs, maxSegments: 100, maxChars: 25);
        Assert.Equal(new[] { 2, 2, 1 }, w.Select(x => x.Count).ToArray());
    }

    [Fact]
    public void Windows_SkipsEmpty_AndKeepsOriginalSegmentIds()
    {
        var segs = new[] { "hello", "   ", "", "world" };
        var w = Postprocess.Windows(segs);
        Assert.Single(w);
        Assert.Equal(new[] { 0, 3 }, w[0].Select(t => t.Id).ToArray()); // ids 1,2 (blank) skipped
    }

    // ── window sizing keeps the LLM call count low (429-storm regression 2026-07-16) ──
    // A ~45-min meeting (~700 turns / ~40 kchars) must NOT fan out into dozens of Mistral
    // calls: with a rate-limited key that became a ~15-min 429 storm. Enlarged default
    // windows keep it to a handful of calls; the per-segment guards still bound quality.
    [Fact]
    public void Windows_DefaultSizing_KeepsCallCountLowForLongMeeting()
    {
        var segs = Enumerable.Range(0, 700).Select(i => $"Ceci est un tour de parole numero {i}.").ToArray();
        int totalChars = segs.Sum(s => s.Length);
        var w = Postprocess.Windows(segs);              // default WindowMax* (50 seg / 6000 chars)
        Assert.True(w.Count <= 16, $"{w.Count} windows for {totalChars} chars — too many LLM calls");
        // sanity: the old tiny defaults (16 seg) would have produced ~44 windows here.
        Assert.True(w.Count < segs.Length / 20);
    }

    // ── LLM guards (postprocess.apply_llm_corrections) ──────────────────────────

    private static List<RawCorrection> Corr(string json)
        => Postprocess.CorrectionsFrom(Postprocess.ParseJson(json));

    [Fact]
    public void ApplyLlm_Accepts_WhenCorrectionIntroducesGlossaryTerm()
    {
        var segs = new[] { "on utilise koupa" };
        var terms = new HashSet<string> { "coupa" };
        var corr = Corr("""{"corrections":[{"segment_id":0,"original":"on utilise koupa","corrected":"on utilise Coupa"}]}""");

        var (outp, applied, rejected) = Postprocess.ApplyLlmCorrections(segs, corr, terms);

        Assert.Equal("on utilise Coupa", outp[0]);
        Assert.Single(applied);
        Assert.Empty(rejected);
    }

    [Fact]
    public void ApplyLlm_Accepts_WhenPhoneticallyClose_EvenWithoutGlossaryTerm()
    {
        var segs = new[] { "bonjour tout le monde" };
        var corr = Corr("""{"corrections":[{"segment_id":0,"original":"bonjour tout le monde","corrected":"Bonjour tout le monde"}]}""");

        var (outp, applied, rejected) = Postprocess.ApplyLlmCorrections(segs, corr, new HashSet<string>());

        Assert.Equal("Bonjour tout le monde", outp[0]);
        Assert.Single(applied);
    }

    [Fact]
    public void ApplyLlm_Rejects_UnknownSegmentId_OutOfRange()
    {
        var segs = new[] { "seul segment" };
        var corr = Corr("""{"corrections":[{"segment_id":9,"original":"seul segment","corrected":"Seul segment"}]}""");

        var (outp, applied, rejected) = Postprocess.ApplyLlmCorrections(segs, corr, new HashSet<string>());

        Assert.Equal("seul segment", outp[0]);
        Assert.Empty(applied);
        Assert.Contains(rejected, r => r.Reason == "segment_id inconnu");
    }

    [Fact]
    public void ApplyLlm_Rejects_NonIntegerSegmentId()
    {
        var segs = new[] { "x" };
        var corr = Corr("""{"corrections":[{"segment_id":"abc","original":"x","corrected":"y"}]}""");

        var (_, applied, rejected) = Postprocess.ApplyLlmCorrections(segs, corr, new HashSet<string>());

        Assert.Empty(applied);
        Assert.Contains(rejected, r => r.Reason == "segment_id non entier");
    }

    [Fact]
    public void ApplyLlm_Rejects_WhenOriginalDoesNotMatchSegment()
    {
        var segs = new[] { "le texte reel" };
        var corr = Corr("""{"corrections":[{"segment_id":0,"original":"autre chose","corrected":"Autre chose"}]}""");

        var (_, applied, rejected) = Postprocess.ApplyLlmCorrections(segs, corr, new HashSet<string>());

        Assert.Empty(applied);
        Assert.Contains(rejected, r => r.Reason == "original ne correspond pas au segment");
    }

    [Fact]
    public void ApplyLlm_Rejects_NoChange()
    {
        var segs = new[] { "identique" };
        var corr = Corr("""{"corrections":[{"segment_id":0,"original":"identique","corrected":"identique"}]}""");

        var (_, applied, rejected) = Postprocess.ApplyLlmCorrections(segs, corr, new HashSet<string>());

        Assert.Empty(applied);
        Assert.Contains(rejected, r => r.Reason == "aucun changement");
    }

    [Fact]
    public void ApplyLlm_Rejects_Paraphrase_EditDistanceTooLarge()
    {
        // Introduces the term "coupa" but the whole segment is rewritten -> guard (ii)
        // (edit distance) fires BEFORE the glossary guard, so it is rejected.
        var segs = new[] { "on parle de budget" };
        var terms = new HashSet<string> { "coupa" };
        var corr = Corr("""{"corrections":[{"segment_id":0,"original":"on parle de budget","corrected":"coupa est un outil de gestion des achats totalement different"}]}""");

        var (outp, applied, rejected) = Postprocess.ApplyLlmCorrections(segs, corr, terms);

        Assert.Equal("on parle de budget", outp[0]);
        Assert.Empty(applied);
        Assert.Contains(rejected, r => r.Reason.StartsWith("distance trop grande"));
    }

    [Fact]
    public void ApplyLlm_Rejects_NeitherTermNorPhonetic_WithLoosenedEditGuard()
    {
        // With the DEFAULT thresholds this input is rejected by the edit-distance
        // guard (ratio 0.4 -> 1-ratio 0.6 > 0.40). Loosen max_edit_ratio so it passes
        // guard (ii); then guard (iii) rejects it: no glossary term AND ratio < 0.50.
        var segs = new[] { "aaaaaaaaaa" };
        var corr = Corr("""{"corrections":[{"segment_id":0,"original":"aaaaaaaaaa","corrected":"aaaabbbbbb"}]}""");

        var (_, applied, rejected) = Postprocess.ApplyLlmCorrections(
            segs, corr, new HashSet<string>(), maxEditRatio: 0.65, phoneticMin: 0.50);

        Assert.Empty(applied);
        Assert.Contains(rejected, r => r.Reason == "ni terme glossaire ni proximite phonetique");

        // Same input, default thresholds -> rejected earlier by the edit-distance guard.
        var (_, applied2, rejected2) = Postprocess.ApplyLlmCorrections(segs, corr, new HashSet<string>());
        Assert.Empty(applied2);
        Assert.Contains(rejected2, r => r.Reason.StartsWith("distance trop grande"));
    }

    [Fact]
    public void ApplyLlm_AppliesOnlyFirstCorrectionPerSegment()
    {
        var segs = new[] { "on utilise koupa" };
        var terms = new HashSet<string> { "coupa" };
        var corr = Corr("""
        {"corrections":[
          {"segment_id":0,"original":"on utilise koupa","corrected":"on utilise Coupa"},
          {"segment_id":0,"original":"on utilise koupa","corrected":"on utilise COUPA"}
        ]}
        """);

        var (outp, applied, rejected) = Postprocess.ApplyLlmCorrections(segs, corr, terms);

        Assert.Equal("on utilise Coupa", outp[0]);     // first valid wins
        Assert.Single(applied);
        Assert.Contains(rejected, r => r.Reason == "segment deja corrige");
    }

    // ── JSON parsing (postprocess._parse_json / _corrections_from) ──────────────

    [Fact]
    public void ParseJson_SalvagesEmbeddedObject_AndAbortsOnGarbage()
    {
        var el = Postprocess.ParseJson("voici le resultat : {\"corrections\": []} merci");
        Assert.Equal(JsonValueKind.Object, el.ValueKind);
        Assert.Throws<PostprocessAbort>(() => Postprocess.ParseJson("pas de json ici"));
    }

    [Fact]
    public void CorrectionsFrom_HandlesBareListAndWrappedObject()
    {
        Assert.Single(Corr("""[{"segment_id":0,"original":"a","corrected":"b"}]"""));
        Assert.Single(Corr("""{"corrections":[{"segment_id":1,"original":"a","corrected":"b"}]}"""));
        Assert.Empty(Corr("""{"other":123}"""));
    }

    // ── echo dedup thresholds (transcribe.echo_analysis) ────────────────────────

    private static EchoDedup.EchoSeg Seg(string origin, double s, double e, string text) => new(origin, s, e, text);

    [Fact]
    public void EchoDedup_FlagsOverlappingSimilarMicSegment()
    {
        var merged = new List<EchoDedup.EchoSeg>
        {
            Seg("remote", 0, 4, "bonjour tout le monde"),
            Seg("local",  0, 4, "bonjour tout le monde"),   // echo of the remote line
        };
        var (ratio, idx) = EchoDedup.Analyze(merged);
        Assert.Equal(new[] { 1 }, idx.ToArray());
        Assert.Equal(1.0, ratio, 6); // all local duration is echo
    }

    [Fact]
    public void EchoDedup_IgnoresDissimilarText()
    {
        var merged = new List<EchoDedup.EchoSeg>
        {
            Seg("remote", 0, 4, "bonjour tout le monde"),
            Seg("local",  0, 4, "au revoir a bientot"),
        };
        var (ratio, idx) = EchoDedup.Analyze(merged);
        Assert.Empty(idx);
        Assert.Equal(0.0, ratio, 6);
    }

    [Fact]
    public void EchoDedup_IgnoresNonOverlappingSegments()
    {
        var merged = new List<EchoDedup.EchoSeg>
        {
            Seg("remote", 0, 4, "meme texte exactement"),
            Seg("local", 10, 14, "meme texte exactement"),  // identical but no temporal overlap
        };
        var (_, idx) = EchoDedup.Analyze(merged);
        Assert.Empty(idx);
    }

    [Fact]
    public void EchoDedup_RatioIsShareOfMicDurationInEcho()
    {
        var merged = new List<EchoDedup.EchoSeg>
        {
            Seg("remote", 0, 2, "hello world"),
            Seg("local",  0, 2, "hello world"),                 // 2s echo
            Seg("local",  5, 7, "contenu totalement different"), // 2s, no match
        };
        var (ratio, idx) = EchoDedup.Analyze(merged);
        Assert.Equal(new[] { 1 }, idx.ToArray());
        Assert.Equal(0.5, ratio, 6); // 2s echo / 4s total local
    }

    // ── deterministic journal (Vocabulary.ApplyVariantCorrections) ──────────────

    [Fact]
    public void Vocabulary_ApplyVariantCorrections_ReturnsCorrectedTextAndJournal()
    {
        var vocab = NewVocab("stir co=>SteerCo\nCoupa\n");
        var (corrected, journal) = vocab.ApplyVariantCorrections(new[] { "le stir co tourne", "rien ici" });

        Assert.Equal("le SteerCo tourne", corrected[0]);
        Assert.Equal("rien ici", corrected[1]);
        Assert.Single(journal);
        Assert.Equal(0, journal[0].SegmentId);
        Assert.Equal("SteerCo", journal[0].Term);
    }

    // ── RunAsync orchestration (postprocess.run_postprocess) ────────────────────

    [Fact]
    public async Task RunAsync_DeterministicThenGuardedLlm_AppliesBoth_AndWritesLog()
    {
        var vocab = NewVocab("stir co=>SteerCo\nCoupa\n");
        var sd = NewSessionDir();
        try
        {
            // Canned LLM: correct "koupa" -> "Coupa" in segment 0 (introduces a glossary term).
            ChatFunc chat = (_, _) => Task.FromResult(
                """{"corrections":[{"segment_id":0,"original":"on utilise koupa","corrected":"on utilise Coupa"}]}""");
            var pp = new Postprocess(chat);

            var (corrected, stats) = await pp.RunAsync(
                new[] { "on utilise koupa", "le stir co tourne" }, vocab, sd,
                attendees: Array.Empty<string>(), meetingName: "Reunion", enableLlm: true);

            Assert.Equal("on utilise Coupa", corrected[0]);   // LLM applied
            Assert.Equal("le SteerCo tourne", corrected[1]);  // deterministic applied
            Assert.Equal(1, stats.Deterministic);
            Assert.True(stats.LlmRan);
            Assert.Equal(1, stats.LlmApplied);
            Assert.True(File.Exists(Echo.Core.Storage.SessionLayout.Find(sd, "postprocess_log.json")));
        }
        finally { Directory.Delete(sd, true); }
    }

    [Fact]
    public async Task RunAsync_SkipsLlm_WhenDisabled()
    {
        var vocab = NewVocab("stir co=>SteerCo\n");
        var sd = NewSessionDir();
        try
        {
            var pp = new Postprocess((_, _) => Task.FromResult("SHOULD NOT BE CALLED"));
            var (corrected, stats) = await pp.RunAsync(
                new[] { "le stir co" }, vocab, sd, Array.Empty<string>(), "R", enableLlm: false);

            Assert.Equal("le SteerCo", corrected[0]);      // deterministic still runs
            Assert.False(stats.LlmRan);
            Assert.Contains("desactive", stats.LlmSkippedReason);
        }
        finally { Directory.Delete(sd, true); }
    }

    [Fact]
    public async Task RunAsync_SkipsLlm_WhenNoChatClient()
    {
        var vocab = NewVocab("Coupa\n");
        var sd = NewSessionDir();
        try
        {
            var pp = new Postprocess(chat: null);
            var (_, stats) = await pp.RunAsync(new[] { "texte" }, vocab, sd, Array.Empty<string>(), "R", enableLlm: true);
            Assert.False(stats.LlmRan);
            Assert.Contains("cle API", stats.LlmSkippedReason);
        }
        finally { Directory.Delete(sd, true); }
    }

    [Fact]
    public async Task RunAsync_AbortsLlm_KeepingDeterministic_OnMalformedJson()
    {
        var vocab = NewVocab("stir co=>SteerCo\n");
        var sd = NewSessionDir();
        try
        {
            var pp = new Postprocess((_, _) => Task.FromResult("this is not json at all"));
            var (corrected, stats) = await pp.RunAsync(
                new[] { "le stir co" }, vocab, sd, Array.Empty<string>(), "R", enableLlm: true);

            Assert.Equal("le SteerCo", corrected[0]);   // deterministic preserved
            Assert.False(stats.LlmRan);
            Assert.Contains("inexploitable", stats.LlmSkippedReason);
        }
        finally { Directory.Delete(sd, true); }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static Vocabulary NewVocab(string lexiconContents)
    {
        var lex = Path.Combine(Path.GetTempPath(), $"lex_{Guid.NewGuid():N}.txt");
        File.WriteAllText(lex, lexiconContents);
        var noMission = Path.Combine(Path.GetTempPath(), $"nomission_{Guid.NewGuid():N}.txt");
        return new Vocabulary(defaultLexiconPath: lex, missionBiasPath: noMission, env: _ => null);
    }

    private static string NewSessionDir()
    {
        var sd = Path.Combine(Path.GetTempPath(), $"echo_sess_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sd);
        return sd;
    }
}
