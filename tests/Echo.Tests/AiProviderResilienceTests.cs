using System.Net;
using System.Text;
using System.Text.Json;
using Echo.Ai;

namespace Echo.Tests;

/// <summary>
/// Provider error handling added after the 2026-09-25 incidents: a 403 "subscription tier"
/// (mistral-large-latest on the user's key) was shown as "clé API Mistral refusée" and failed
/// every CR; voxtral-small-latest answered 400 "Invalid model". Also: context-overflow
/// detection, empty / truncated answers, and the language / vocabulary sent to the engines.
/// All HTTP is stubbed.
/// </summary>
public class AiProviderResilienceTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _script;
        public List<(string Url, string Content)> Requests { get; } = new();
        public StubHandler(params (HttpStatusCode, string)[] script) => _script = new(script);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!.ToString(), content));
            if (_script.Count == 0) throw new InvalidOperationException("unexpected extra request");
            var (status, body) = _script.Dequeue();
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static readonly TimeSpan[] NoWait = { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero };
    private static readonly List<ChatMessage> Msgs = new() { new("user", "hello") };
    private const string ChatOk = "{\"choices\":[{\"message\":{\"content\":\"réponse\"},\"finish_reason\":\"stop\"}]}";
    private const string Tier403 = "{\"object\":\"error\",\"message\":\"This model is not available in your subscription tier\",\"type\":\"invalid_request_error\"}";

    // ── Mistral 403 "offre" vs key ──────────────────────────────────────────────

    [Fact]
    public async Task Mistral_403Tier_RetriesOnFreeModel_AndTellsTheCaller()
    {
        var h = new StubHandler((HttpStatusCode.Forbidden, Tier403), (HttpStatusCode.OK, ChatOk));
        var log = new List<string>();
        var c = new MistralClient(new HttpClient(h), retryLog: log.Add, retryDelays: NoWait);
        ModelFallback? seen = null;
        c.ModelFallback += f => seen = f;

        var content = await c.ChatCompleteAsync("k", "mistral-large-latest", Msgs, 0.2, jsonObject: true, CancellationToken.None);

        Assert.Equal("réponse", content);
        Assert.Equal(2, h.Requests.Count);
        Assert.Contains("\"mistral-large-latest\"", h.Requests[0].Content);
        Assert.Contains("\"ministral-8b-latest\"", h.Requests[1].Content);
        Assert.Contains("response_format", h.Requests[1].Content);          // CR keeps its JSON mode
        Assert.Contains("[Mistral] modèle mistral-large-latest hors offre, repli sur ministral-8b-latest", log);
        Assert.Equal(new ModelFallback("mistral", "mistral-large-latest", "ministral-8b-latest", "subscription_tier"), seen);
        Assert.Equal(seen, c.LastFallback);
    }

    [Fact]
    public async Task Mistral_403Tier_WholeChainRefused_EachModelTriedOnce_TierMessageNotKeyMessage()
    {
        var h = new StubHandler((HttpStatusCode.Forbidden, Tier403), (HttpStatusCode.Forbidden, Tier403),
                                (HttpStatusCode.Forbidden, Tier403));
        var c = new MistralClient(new HttpClient(h), retryDelays: NoWait);

        var ex = await Assert.ThrowsAsync<MistralApiException>(
            () => c.ChatCompleteAsync("k", "mistral-small-latest", Msgs, 0.2, jsonObject: false, CancellationToken.None));

        Assert.Equal(3, h.Requests.Count);                                   // small, ministral-8b, nemo
        Assert.Contains("\"ministral-8b-latest\"", h.Requests[1].Content);
        Assert.Contains("\"open-mistral-nemo\"", h.Requests[2].Content);
        Assert.True(ex.IsTierRestricted);
        Assert.Equal("Le modèle open-mistral-nemo n'est pas inclus dans l'offre de votre clé Mistral. "
                     + "Choisissez un autre modèle dans Paramètres > Fournisseurs.", ex.UserMessage);
        Assert.DoesNotContain("clé API Mistral refusée", ex.UserMessage);
    }

    /// <summary>Real case 2026-10-13: mistral-small-latest answered 429 with
    /// x-ratelimit-limit-req-minute: 0 on every call. That is a plan restriction, not a burst:
    /// no retry schedule, straight to the next model of the chain.</summary>
    [Fact]
    public async Task Mistral_429ZeroAllowance_IsTierRestricted_NoRetry_FallsBackToNextModel()
    {
        var h = new HeaderStub();
        var log = new List<string>();
        var c = new MistralClient(new HttpClient(h), retryLog: log.Add, retryDelays: NoWait);

        var content = await c.ChatCompleteAsync("k", "mistral-small-latest", Msgs, 0.2, jsonObject: false, CancellationToken.None);

        Assert.Equal("réponse", content);
        Assert.Equal(2, h.Models.Count);                                     // no retry on the 0-allowance model
        Assert.Equal("mistral-small-latest", h.Models[0]);
        Assert.Equal("ministral-8b-latest", h.Models[1]);
        Assert.Contains("[Mistral] modèle mistral-small-latest hors offre, repli sur ministral-8b-latest", log);
    }

    [Fact]
    public void Mistral_429ZeroAllowance_IsNotTransient_ButAPlain429Is()
    {
        Assert.False(new MistralApiException(429, "Rate limit exceeded", 1, "mistral-small-latest", zeroQuota: true).IsRetryable);
        Assert.True(new MistralApiException(429, "Rate limit exceeded", 1, "mistral-small-latest", zeroQuota: true).IsTierRestricted);
        Assert.True(new MistralApiException(429, "Rate limit exceeded", 5).IsRetryable);
        Assert.False(new MistralApiException(429, "Rate limit exceeded", 5).IsTierRestricted);
    }

    /// <summary>small → 429 with a 0 req/min allowance; any other model → 200.</summary>
    private sealed class HeaderStub : HttpMessageHandler
    {
        public List<string> Models { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            var model = JsonDocument.Parse(body).RootElement.GetProperty("model").GetString()!;
            Models.Add(model);
            if (model == "mistral-small-latest")
            {
                var r = new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("{\"message\":\"Rate limit exceeded\"}") };
                r.Headers.Add("x-ratelimit-limit-req-minute", "0");
                return r;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ChatOk, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Mistral_Real403KeyRefusal_NoFallback_KeyMessage()
    {
        var h = new StubHandler((HttpStatusCode.Forbidden, "{\"message\":\"Forbidden\"}"));
        var c = new MistralClient(new HttpClient(h), retryDelays: NoWait);

        var ex = await Assert.ThrowsAsync<MistralApiException>(
            () => c.ChatCompleteAsync("k", "mistral-large-latest", Msgs, 0.2, jsonObject: false, CancellationToken.None));

        Assert.Single(h.Requests);
        Assert.False(ex.IsTierRestricted);
        Assert.Contains("clé API Mistral refusée", ex.UserMessage);
    }

    [Fact]
    public async Task Voxtral_InvalidModel_RetriesOnceOnMini_WithoutBurningTheLadder()
    {
        var h = new StubHandler(
            (HttpStatusCode.BadRequest, "{\"message\":\"Invalid model: voxtral-small-latest\",\"type\":\"invalid_model\"}"),
            (HttpStatusCode.OK, "{\"text\":\"ok\",\"segments\":[]}"));
        var log = new List<string>();
        var c = new MistralClient(new HttpClient(h), retryLog: log.Add, retryDelays: NoWait);

        var res = await c.TranscribeAsync("k", "voxtral-small-latest", new byte[] { 1 }, "a.mp3", new[] { "SteerCo" }, null, CancellationToken.None);

        Assert.Equal("ok", res.Text);
        Assert.Equal(2, h.Requests.Count);                       // no bias/diarize shedding on a model error
        Assert.Contains("voxtral-mini-latest", h.Requests[1].Content);
        Assert.Contains("diarize", h.Requests[1].Content);
        Assert.Equal("voxtral-mini-latest", c.LastFallback!.UsedModel);
        Assert.Contains(log, l => l.Contains("repli sur voxtral-mini-latest"));
    }

    // ── context overflow ────────────────────────────────────────────────────────

    [Fact]
    public void IsContextOverflow_RecognisesEachProvidersWording()
    {
        Assert.True(new MistralApiException(400, "Prompt contains 140000 tokens and 0 draft tokens, too large for model with 131072 maximum context length", 1).IsContextOverflow);
        Assert.True(new GeminiApiException(400, "The input token count (1200000) exceeds the maximum number of tokens allowed (1048576).", 1).IsContextOverflow);
        Assert.True(new CloudflareApiException(400, "AiError: The estimated number of input and maximum output tokens (31000) exceeded this model context window limit (24000). (code 5021)", 1).IsContextOverflow);
        Assert.False(new MistralApiException(400, "response_format not supported", 1).IsContextOverflow);
        Assert.False(new MistralApiException(429, "context length", 1).IsContextOverflow);   // a 429 is a rate limit
        Assert.Contains("trop longue", new CloudflareApiException(400, "context window limit", 1).UserMessage);
    }

    [Fact]
    public async Task Mistral_ContextOverflow_InJsonMode_IsNotRetriedPlain()
    {
        var h = new StubHandler((HttpStatusCode.BadRequest, "{\"message\":\"too large for model with 131072 maximum context length\"}"));
        var c = new MistralClient(new HttpClient(h), retryDelays: NoWait);

        var ex = await Assert.ThrowsAsync<MistralApiException>(
            () => c.ChatCompleteAsync("k", "mistral-large-latest", Msgs, 0.2, jsonObject: true, CancellationToken.None));

        Assert.True(ex.IsContextOverflow);
        Assert.Single(h.Requests);                                 // same prompt without JSON mode would overflow too
    }

    // ── empty / truncated answers ──────────────────────────────────────────────

    [Fact]
    public async Task Mistral_FinishReasonLength_IsATruncatedError()
    {
        var h = new StubHandler((HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"content\":\"- début\"},\"finish_reason\":\"length\"}]}"));
        var c = new MistralClient(new HttpClient(h), retryDelays: NoWait);

        var ex = await Assert.ThrowsAsync<LlmEmptyResponseException>(
            () => c.ChatCompleteAsync("k", "m", Msgs, 0.2, jsonObject: false, CancellationToken.None));
        Assert.True(ex.Truncated);
        Assert.Contains("tronquée", ex.UserMessage);
    }

    [Fact]
    public async Task Gemini_SafetyBlock_And_MaxTokens_AreExplicitErrors()
    {
        var blocked = "{\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}";
        var cut = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"- a\"}]},\"finishReason\":\"MAX_TOKENS\"}]}";
        var h = new StubHandler((HttpStatusCode.OK, blocked), (HttpStatusCode.OK, cut));
        var g = new GeminiClient(new HttpClient(h), retryDelays: NoWait);

        var e1 = await Assert.ThrowsAsync<LlmEmptyResponseException>(
            () => g.ChatCompleteAsync("k", "gemini-x", Msgs, 0.2, jsonObject: false, CancellationToken.None));
        Assert.False(e1.Truncated);
        Assert.Contains("SAFETY", e1.UserMessage);
        var e2 = await Assert.ThrowsAsync<LlmEmptyResponseException>(
            () => g.ChatCompleteAsync("k", "gemini-x", Msgs, 0.2, jsonObject: false, CancellationToken.None));
        Assert.True(e2.Truncated);
    }

    [Fact]
    public void Cloudflare_UnknownShape_And_Length_AreExplicitErrors()
    {
        Assert.Throws<LlmEmptyResponseException>(() => CloudflareClient.CheckChatAnswer("{\"result\":{\"usage\":{}},\"success\":true}", "m"));
        var cut = Assert.Throws<LlmEmptyResponseException>(() => CloudflareClient.CheckChatAnswer(
            "{\"choices\":[{\"message\":{\"content\":\"x\"},\"finish_reason\":\"length\"}]}", "m"));
        Assert.True(cut.Truncated);
        CloudflareClient.CheckChatAnswer("{\"result\":{\"response\":\"ok\"}}", "m");   // native shape accepted
    }

    // ── language and vocabulary sent to the engines ────────────────────────────

    [Fact]
    public void Whisper_Auto_ImposesNoLanguage_AndPromptHasRealSpaces()
    {
        var auto = CloudflareClient.BuildWhisperPayload(new byte[] { 1 }, new[] { "Claire_MOREAU", "Pyramid_Principle" }, "auto");
        Assert.False(auto.ContainsKey("language"));
        Assert.Equal("Vocabulaire : Claire MOREAU, Pyramid Principle.", auto["initial_prompt"]);
        Assert.Equal("fr", CloudflareClient.BuildWhisperPayload(new byte[] { 1 }, null, "FR")["language"]);
    }

    [Fact]
    public void Gemini_TranscriptionPrompt_SpacesBack_AndAutoImposesNoLanguage()
    {
        var auto = GeminiClient.TranscriptionPrompt(new[] { "Claire_MOREAU" }, "auto");
        Assert.Contains("Claire MOREAU", auto);
        Assert.DoesNotContain("_", auto.Split("orthographier")[1]);
        Assert.DoesNotContain("français", auto);
        Assert.Contains("français", GeminiClient.TranscriptionPrompt(null, "fr"));
    }

    [Fact]
    public void LayerSessionBias_AddsParticipants_FirstAfterMission_Deduped()
    {
        var lex = Path.Combine(Path.GetTempPath(), $"lex_{Guid.NewGuid():N}.txt");
        File.WriteAllText(lex, "SteerCo\n");
        var v = new Vocabulary(defaultLexiconPath: lex, missionBiasPath: Path.Combine(Path.GetTempPath(), $"nm_{Guid.NewGuid():N}.txt"), env: _ => null);

        var bias = v.LayerSessionBias(new[] { "SteerCo", "Alice_Martin" }, new[] { "Alice Martin", "Bob Durand" });

        Assert.Equal(new[] { "Alice_Martin", "Bob_Durand", "SteerCo" }, bias);
    }

    // ── .eml headers ────────────────────────────────────────────────────────────

    [Fact]
    public void Eml_Subject_MultiLineAndAccents_IsOneEncodedHeader()
    {
        var path = Path.Combine(Path.GetTempPath(), $"echo_ai_{Guid.NewGuid():N}.eml");
        try
        {
            var subject = "Revue budgétaire | Comité\r\nBcc: victim@example.com\nsuite très longue pour forcer le pliage de l'en-tête encodé";
            MinutesGenerator.WriteEml(path, subject, "<p>x</p>", "Hélène Dupré <h@x.com>", "Zoé Martin <z@x.com>; plain@x.com");
            var text = Encoding.UTF8.GetString(File.ReadAllBytes(path));
            var head = text[..text.IndexOf("\r\n\r\n", StringComparison.Ordinal)];

            // No injected header: every header line starts with a known name or is a folded continuation.
            foreach (var line in head.Split("\r\n"))
                Assert.True(line.StartsWith(' ') || line.Split(':')[0] is "X-Unsent" or "Subject" or "From" or "To"
                            or "MIME-Version" or "Content-Type" or "Content-Transfer-Encoding", line);
            Assert.DoesNotContain("\nBcc:", head);
            Assert.All(head.Split("\r\n"), l => Assert.True(l.Length <= 998));
            Assert.All(head, ch => Assert.True(ch < 0x80));          // headers are pure ASCII now

            var subjLine = head.Split("\r\nFrom:")[0];
            Assert.StartsWith("X-Unsent: 1\r\nSubject: =?utf-8?B?", subjLine);
            Assert.Equal(subject.Replace("\r\n", " ").Replace("\n", " "), DecodeWords(subjLine.Split("Subject: ")[1]));
            Assert.Contains("From: =?utf-8?B?", head);
            Assert.Contains(" <h@x.com>\r\n", head);
            Assert.Contains("To: =?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes("Zoé Martin")) + "?= <z@x.com>, plain@x.com", head);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Minimal RFC 2047 B-decoder for the assertions.</summary>
    private static string DecodeWords(string folded)
    {
        var sb = new StringBuilder();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(folded, @"=\?utf-8\?B\?([^?]*)\?="))
            sb.Append(Encoding.UTF8.GetString(Convert.FromBase64String(m.Groups[1].Value)));
        return sb.ToString();
    }
}
