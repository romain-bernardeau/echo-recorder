using System.Text;
using System.Text.Json;
using Echo.Ai;
using Echo.Core.Models;

namespace Echo.Tests;

/// <summary>
/// Regression tests for the riskiest behaviours ported from the Python reference
/// (minutes.py / transcribe.py / lexicon.py / postprocess.py / echo_ui.py). No live
/// Mistral call is made — only pure, deterministic helpers are exercised.
/// </summary>
public class EchoAiTests
{
    // ── .eml byte fidelity (write_eml): X-Unsent, CRLF headers, no \r\r\n, UTF-8 no BOM ──

    [Fact]
    public void WriteEml_IsByteFaithful()
    {
        var path = Path.Combine(Path.GetTempPath(), $"echo_test_{Guid.NewGuid():N}.eml");
        try
        {
            MinutesGenerator.WriteEml(path, "Subject É", "<html><body>Été</body></html>", "Jane <j@x.com>");
            var bytes = File.ReadAllBytes(path);

            // No UTF-8 BOM.
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

            var text = Encoding.UTF8.GetString(bytes);
            Assert.StartsWith("X-Unsent: 1\r\n", text);
            // Non-ASCII subject: RFC 2047 encoded-word (raw 8-bit headers were mis-decoded by some clients).
            Assert.Contains("Subject: =?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes("Subject É")) + "?=\r\n", text);
            Assert.Contains("From: Jane <j@x.com>\r\n", text);
            Assert.Contains("Content-Type: text/html; charset=\"utf-8\"\r\n", text);
            // Exactly one blank line (CRLF CRLF) separates headers from the HTML body.
            Assert.Contains("Content-Transfer-Encoding: 8bit\r\n\r\n<html>", text);
            // Windows newline translation must NOT have doubled the CR.
            Assert.DoesNotContain("\r\r\n", text);
        }
        finally { File.Delete(path); }
    }

    // ── HTML body (build_html): navy header, next-steps table + TBC in detailed; omitted in general ──

    [Fact]
    public void BuildHtml_DetailedHasNavyTableAndTbc_GeneralOmitsThem()
    {
        var data = new MinutesData { Subject = "S", Context = "Ctx" };
        data.KeyElements.Add(new MinItem("Budget", "confirmé", ""));
        data.NextSteps.Add(new NextStep("Envoyer le RFP", "", "", "")); // empty owner/deadline -> TBC

        var detailed = MinutesGenerator.BuildHtml(data, general: false, new AppSettings());
        Assert.Contains("#0D2F4B", detailed);
        Assert.Contains("<table", detailed);
        Assert.Contains("<td style=\"padding:7px 10px; border:1px solid #d0d0d0;\">TBC</td>", detailed);
        Assert.Contains("Envoyer le RFP", detailed);

        var general = MinutesGenerator.BuildHtml(data, general: true, new AppSettings());
        Assert.DoesNotContain("<table", general);
        Assert.Contains("Budget", general); // key elements still present
    }

    [Fact]
    public void SummarySubject_SubstitutesMinutesAndCompteRendu()
    {
        Assert.Equal("Weekly | Sync : Summary (1 Jan)", MinutesGenerator.SummarySubject("Weekly | Sync : Minutes (1 Jan)"));
        Assert.Equal("Point : Synthèse", MinutesGenerator.SummarySubject("Point : Compte rendu"));
    }

    // ── _plain normalisation (used by .md and .docx): strip **, dash->comma, HTML-unescape ──

    [Theory]
    [InlineData("**Budget**", "Budget")]
    [InlineData("a—b", "a, b")]
    [InlineData("a–b", "a, b")]
    [InlineData("d&rsquo;eau", "d’eau")]
    [InlineData("  spaced  ", "spaced")]
    public void Plain_NormalisesLikePython(string input, string expected)
        => Assert.Equal(expected, MinutesGenerator.Plain(input));

    // ── cr_type is driven by the template base (parity with tray_app._template_run_args) ──

    [Theory]
    [InlineData("general", "general")]
    [InlineData("detailed", "detailed")]
    [InlineData("GENERAL", "general")]
    [InlineData(null, "detailed")]
    [InlineData("", "detailed")]
    public void CrTypeForBase_MapsTemplateBase(string? baseVal, string expected)
        => Assert.Equal(expected, MinutesGenerator.CrTypeForBase(baseVal));

    // ── minutes JSON validation (_validate_minutes): hard-fail schema, no degraded CR ──

    [Fact]
    public void ValidateMinutes_AcceptsMinimalValid()
    {
        using var doc = JsonDocument.Parse(
            """{"context":"c","key_elements":[{"title":"t"}],"next_steps":[{"action":"a"}]}""");
        Assert.Empty(MinutesGenerator.ValidateMinutes(doc.RootElement));
    }

    [Fact]
    public void ValidateMinutes_RejectsMissingContextAndActionAndNonListPoa()
    {
        using var doc = JsonDocument.Parse(
            """{"key_elements":[{"title":"t"}],"next_steps":[{"owner":"o"}],"points_of_arbitration":"nope"}""");
        var errs = MinutesGenerator.ValidateMinutes(doc.RootElement);
        Assert.Contains(errs, e => e.Contains("context"));
        Assert.Contains(errs, e => e.Contains("next_steps"));
        Assert.Contains(errs, e => e.Contains("points_of_arbitration"));
    }

    [Fact]
    public void ParseJsonObject_SalvagesJsonWrappedInProse()
    {
        var el = MinutesGenerator.ParseJsonObject("Here you go:\n{\"context\":\"x\"}\nThanks!");
        Assert.Equal(JsonValueKind.Object, el.ValueKind);
        Assert.Equal("x", el.GetProperty("context").GetString());
    }

    // ── Summarizer defensive unwrap (heals {"content": …} that leaked into resume.md) ──

    [Fact]
    public void UnwrapJsonContent_UnwrapsSingleStringObject_KeepsProseAndMultiProp()
    {
        Assert.Equal("hello", Summarizer.UnwrapJsonContent("""{"content":"hello"}"""));
        Assert.Equal("- a\n- b", Summarizer.UnwrapJsonContent("- a\n- b"));
        Assert.Equal("""{"a":"1","b":"2"}""", Summarizer.UnwrapJsonContent("""{"a":"1","b":"2"}"""));
    }

    // ── [MM:SS] timestamp format (fmt_ts): zero-padded MM:SS below 1 h, H:MM:SS above, ??:?? for null ──

    [Theory]
    [InlineData(0.0, "00:00")]
    [InlineData(65.0, "01:05")]
    [InlineData(4500.0, "1:15:00")]   // from 1 h on: H:MM:SS (the old "125:30" broke the CR and the UI)
    public void FmtTs_FormatsMinutesSeconds(double sec, string expected)
        => Assert.Equal(expected, Transcriber.FmtTs(sec));

    [Fact]
    public void FmtTs_NullIsQuestionMarks() => Assert.Equal("??:??", Transcriber.FmtTs(null));

    // ── file-prefix sanitisation (short_filebase): illegal chars, 40-char cap, fallback ──

    [Fact]
    public void ShortFilebase_TruncatesAndSanitises()
    {
        Assert.Equal("Transcript", Transcriber.ShortFilebase(""));
        Assert.Equal("Weekly Sync", Transcriber.ShortFilebase("Weekly:/ Sync"));  // illegal chars removed
        var longName = new string('a', 60);
        Assert.Equal(40, Transcriber.ShortFilebase(longName).Length);
        Assert.Equal("abcd", Transcriber.ShortFilebase("abcd , efgh", max: 6)); // cut to "abcd ," then trailing " ,;-" stripped
    }

    // ── context_bias normalisation (load_context_bias): '_' join, <3 drop, dedup, cap 100 ──

    [Fact]
    public void Normalize_JoinsTokensDropsShortDedupsAndCaps()
    {
        var outp = Vocabulary.Normalize(new[] { "Pyramid Principle", "AB", "abc", "ABC", "x", "Claire MOREAU" });
        Assert.Contains("Pyramid_Principle", outp);      // multi-word kept as ONE token
        Assert.Contains("Claire_MOREAU", outp);
        Assert.Contains("abc", outp);
        Assert.DoesNotContain("AB", outp);               // < 3 chars dropped
        Assert.DoesNotContain("x", outp);
        Assert.Single(outp.Where(t => t.Equals("abc", StringComparison.OrdinalIgnoreCase))); // case-insensitive dedup

        var many = Enumerable.Range(0, 150).Select(i => $"term{i:D3}");
        Assert.Equal(100, Vocabulary.Normalize(many).Count);   // hard cap 100
    }

    // ── lexicon parser (lexicon.py): variant=>canonical, bare term, comments, dedup ──

    [Fact]
    public void Lexicon_ParseLine_HandlesMappingBareAndComment()
    {
        Assert.Null(Lexicon.ParseLine("# comment"));
        Assert.Null(Lexicon.ParseLine(""));
        var bare = Lexicon.ParseLine("SteerCo");
        Assert.Equal("SteerCo", bare!.Value.Canonical);
        Assert.Empty(bare.Value.Variants);
        var map = Lexicon.ParseLine("stirco|steer co=>SteerCo");
        Assert.Equal("SteerCo", map!.Value.Canonical);
        Assert.Equal(new[] { "stirco", "steer co" }, map.Value.Variants);
    }

    [Fact]
    public void Lexicon_LoadEntries_MergesAndDropsVariantEqualToCanonical()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lex_{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "# header\nsteer co|steerco=>SteerCo\nstirco=>SteerCo\nCoupa\n");
        try
        {
            var entries = Lexicon.LoadEntries(new[] { path });
            var steerco = entries.Single(e => e.Canonical == "SteerCo");
            Assert.Contains("stirco", steerco.Variants);          // accumulated from the 2nd line
            Assert.Contains("steer co", steerco.Variants);
            Assert.DoesNotContain("steerco", steerco.Variants);   // variant == canonical (case-insensitive) dropped
            Assert.Contains(Lexicon.BiasTerms(entries), t => t == "Coupa");
            Assert.Equal("SteerCo", Lexicon.VariantMap(entries)["stirco"]);
        }
        finally { File.Delete(path); }
    }

    // ── Voxtral response parsing: speaker_id precedence, start_time fallback, empty drop ──

    [Fact]
    public void VoxtralParser_PrefersSpeakerId_AndFallsBackOnStartTime()
    {
        const string json = """
        {"language":"fr","segments":[
          {"start_time":1.0,"end_time":2.0,"text":"bonjour","speaker_id":3,"speaker":"9"},
          {"start":2.0,"end":3.0,"text":"   ","speaker_id":1},
          {"start":3.0,"end":4.0,"text":"ok","speaker":"lbl"}
        ]}
        """;
        var res = VoxtralResponseParser.Parse(json);
        Assert.Equal("fr", res.Language);
        Assert.Equal(2, res.Segments.Count);                 // empty-text segment dropped
        Assert.Equal(1.0, res.Segments[0].Start);            // start_time fallback
        Assert.Equal("3", res.Segments[0].VoxSpeaker);       // speaker_id wins over speaker
        Assert.Equal("lbl", res.Segments[1].VoxSpeaker);     // falls back to speaker
    }

    // ── timeline remap maths (build_mapping / remap_time) underpinning attribution ──

    [Fact]
    public void RemapTime_MapsTrimmedBackToRealTrackTime()
    {
        var (mapping, kept) = AudioPrep.BuildMapping(new[] { (2.0, 5.0), (10.0, 12.0) });
        Assert.Equal(5.0, kept);
        Assert.Equal(2.0, AudioPrep.RemapTime(0.0, mapping)!.Value, 3);   // start of first kept span
        Assert.Equal(3.0, AudioPrep.RemapTime(1.0, mapping)!.Value, 3);
        Assert.Equal(11.0, AudioPrep.RemapTime(4.0, mapping)!.Value, 3);  // into the second span
    }
}
