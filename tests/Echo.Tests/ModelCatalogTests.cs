using System.Net;
using System.Text;
using System.Text.Json;
using Echo.Ai;
using Echo.Core.Abstractions;
using Echo.Core.Models;

namespace Echo.Tests;

/// <summary>
/// Model catalogue + probe (Settings "Providers &amp; models" card) and the per-task model
/// settings added after a free-tier Mistral key failed live (2026-09-16: 429 on Voxtral,
/// 403 "not available in your subscription tier" on the hard-coded mistral-large-latest CR
/// model). All HTTP is stubbed with the same scripted handler as the other provider tests.
/// </summary>
public class ModelCatalogTests
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
    private static MistralClient Mistral(StubHandler h) => new(new HttpClient(h), retryDelays: NoWait);
    private static GeminiClient Gemini(StubHandler h) => new(new HttpClient(h), retryDelays: NoWait);

    // ── Mistral /v1/models ───────────────────────────────────────────────────────

    private const string MistralModels = @"{""object"":""list"",""data"":[
        {""id"":""mistral-large-latest"",""object"":""model"",""capabilities"":{""completion_chat"":true,""function_calling"":true}},
        {""id"":""voxtral-mini-2602"",""object"":""model"",""capabilities"":{""completion_chat"":false}},
        {""id"":""mistral-small-latest"",""object"":""model"",""capabilities"":{""completion_chat"":true}},
        {""id"":""mistral-small-latest"",""object"":""model"",""capabilities"":{""completion_chat"":true}},
        {""id"":""mistral-embed"",""object"":""model""},
        {""id"":""voxtral-small-latest"",""object"":""model"",""capabilities"":{""completion_chat"":true}}
    ]}";

    [Fact]
    public async Task Mistral_ListModels_ParsesCapabilities_MarksVoxtral_DedupesAndSorts()
    {
        var h = new StubHandler((HttpStatusCode.OK, MistralModels));
        var models = await Mistral(h).ListModelsAsync("k", CancellationToken.None);

        Assert.Single(h.Requests);
        Assert.EndsWith("/v1/models", h.Requests[0].Url);

        // duplicate mistral-small-latest collapsed; sorted by id.
        Assert.Equal(new[] { "mistral-embed", "mistral-large-latest", "mistral-small-latest", "voxtral-mini-2602", "voxtral-small-latest" },
            models.Select(m => m.Id).ToArray());

        var vox = models.Single(m => m.Id == "voxtral-mini-2602");
        Assert.True(vox.Transcribe);
        Assert.False(vox.Chat);

        var small = models.Single(m => m.Id == "mistral-small-latest");
        Assert.True(small.Chat);
        Assert.False(small.Transcribe);

        // no capabilities block: chat inferred by family (embed is not chat).
        Assert.False(models.Single(m => m.Id == "mistral-embed").Chat);

        // a voxtral model is a transcription model, never offered as a CR model, even if the
        // server flags completion_chat.
        var voxSmall = models.Single(m => m.Id == "voxtral-small-latest");
        Assert.True(voxSmall.Transcribe);
        Assert.False(voxSmall.Chat);
    }

    [Fact]
    public async Task Mistral_ListModels_401_ThrowsWithStatus_NoRetry()
    {
        var h = new StubHandler(((HttpStatusCode)401, "{\"message\":\"Unauthorized\"}"));
        var ex = await Assert.ThrowsAsync<MistralApiException>(() => Mistral(h).ListModelsAsync("bad", CancellationToken.None));
        Assert.Equal(401, ex.StatusCode);
        Assert.Single(h.Requests);   // interactive call: one attempt only
    }

    // ── Gemini /v1beta/models (paged) ────────────────────────────────────────────

    [Fact]
    public async Task Gemini_ListModels_FollowsNextPageToken_StripsPrefix_FiltersFamilies()
    {
        var page1 = @"{""models"":[
            {""name"":""models/gemini-3.6-flash"",""supportedGenerationMethods"":[""generateContent"",""countTokens""]},
            {""name"":""models/text-embedding-004"",""supportedGenerationMethods"":[""embedContent""]},
            {""name"":""models/imagen-4.0-generate"",""supportedGenerationMethods"":[""predict""]}
        ],""nextPageToken"":""tok2""}";
        var page2 = @"{""models"":[
            {""name"":""models/gemini-3.5-transcribe"",""supportedGenerationMethods"":[""generateContent""]},
            {""name"":""models/gemini-3.6-pro"",""supportedGenerationMethods"":[""generateContent""]},
            {""name"":""models/veo-3.0-generate"",""supportedGenerationMethods"":[""predictLongRunning""]},
            {""name"":""models/gemini-3.6-flash"",""supportedGenerationMethods"":[""generateContent""]}
        ]}";
        var h = new StubHandler((HttpStatusCode.OK, page1), (HttpStatusCode.OK, page2));

        var models = await Gemini(h).ListModelsAsync("k", CancellationToken.None);

        Assert.Equal(2, h.Requests.Count);
        Assert.DoesNotContain("pageToken=", h.Requests[0].Url);
        Assert.Contains("pageToken=tok2", h.Requests[1].Url);
        Assert.Contains("key=k", h.Requests[1].Url);

        // "models/" prefix stripped, embeddings/imagen/veo dropped, duplicate across pages collapsed, sorted.
        Assert.Equal(new[] { "gemini-3.5-transcribe", "gemini-3.6-flash", "gemini-3.6-pro" }, models.Select(m => m.Id).ToArray());

        var flash = models.Single(m => m.Id == "gemini-3.6-flash");
        Assert.True(flash.Chat);
        Assert.True(flash.Transcribe);   // flash/pro accept audio

        // dedicated transcribe model: audio yes, CR chat no.
        var tr = models.Single(m => m.Id == "gemini-3.5-transcribe");
        Assert.True(tr.Transcribe);
        Assert.False(tr.Chat);
    }

    [Fact]
    public void Gemini_ParseModelPage_ReturnsNullToken_OnLastPage()
    {
        var models = GeminiClient.ParseModelPage(@"{""models"":[{""name"":""models/gemini-3.6-flash"",""supportedGenerationMethods"":[""generateContent""]}]}", out var next);
        Assert.Null(next);
        Assert.Single(models);
    }

    // ── one-shot probes ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Mistral_ProbeChatModel_403_ReturnsNotOk_WithStatus_SingleAttempt()
    {
        var h = new StubHandler(((HttpStatusCode)403, "{\"message\":\"This model is not available in your subscription tier\"}"));
        var r = await Mistral(h).ProbeChatModelAsync("k", "mistral-large-latest", CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Equal(403, r.Status);
        Assert.Contains("subscription tier", r.ServerMessage);
        Assert.Single(h.Requests);   // no retry ladder on a probe

        // minimal payload: the requested model, one user "ping", max_tokens 1.
        using var body = JsonDocument.Parse(h.Requests[0].Content);
        Assert.Equal("mistral-large-latest", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(1, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal("ping", body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Mistral_ProbeChatModel_429_ReturnsNotOk_WithoutRetrying()
    {
        // The regular chat path retries a 429; the probe must NOT (it reports the saturation).
        var h = new StubHandler(((HttpStatusCode)429, "{\"message\":\"Not enough capacity\"}"));
        var r = await Mistral(h).ProbeChatModelAsync("k", "voxtral-mini-2602", CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(429, r.Status);
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task Mistral_ProbeChatModel_200_ReturnsOk()
    {
        var h = new StubHandler((HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"content\":\"pong\"}}]}"));
        var r = await Mistral(h).ProbeChatModelAsync("k", "mistral-small-latest", CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal(200, r.Status);
        Assert.Null(r.ServerMessage);
    }

    [Fact]
    public async Task Gemini_ProbeChatModel_403_ReturnsNotOk_WithStatus()
    {
        var h = new StubHandler(((HttpStatusCode)403, "{\"error\":{\"message\":\"denied\"}}"));
        var r = await Gemini(h).ProbeChatModelAsync("k", "gemini-3.6-pro", CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(403, r.Status);
        Assert.Equal("denied", r.ServerMessage);
        Assert.Single(h.Requests);
        Assert.Contains("models/gemini-3.6-pro:generateContent", h.Requests[0].Url);
        Assert.Contains("maxOutputTokens", h.Requests[0].Content);
    }

    // ── settings: mistral_cr_model drives the CR and the summary ─────────────────

    [Fact]
    public void Settings_NewKeys_HaveHistoricalDefaults_AndRoundTrip()
    {
        var d = new AppSettings();
        Assert.Equal("ministral-8b-latest", d.MistralCrModel);     // free-plan default since 5.6.2
        Assert.Equal("ministral-8b-latest", d.PostprocessModel);
        Assert.Equal("voxtral-mini-2602", d.MistralTranscribeModel);
        Assert.True(d.AutoFallbackProvider);

        var json = "{\"mistral_cr_model\":\"mistral-small-latest\",\"mistral_transcribe_model\":\"voxtral-small-latest\",\"auto_fallback_provider\":false}";
        var s = JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.Equal("mistral-small-latest", s.MistralCrModel);
        Assert.Equal("voxtral-small-latest", s.MistralTranscribeModel);
        Assert.False(s.AutoFallbackProvider);
        var back = JsonSerializer.Serialize(s);
        Assert.Contains("\"mistral_cr_model\":\"mistral-small-latest\"", back);
    }

    private static string NewSession(string transcript)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"echo_models_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "R - Transcript.txt"), transcript, new UTF8Encoding(false));
        return dir;
    }

    private static string ChatReply(string content)
        => "{\"choices\":[{\"message\":{\"content\":" + JsonSerializer.Serialize(content) + "}}]}";

    private static string ModelOf(string requestBody)
        => JsonDocument.Parse(requestBody).RootElement.GetProperty("model").GetString() ?? "";

    [Fact]
    public async Task Summarizer_UsesMistralCrModel_NotPostprocessModel()
    {
        var transcript = string.Join("\n", Enumerable.Repeat("[00:01] A : point discute en reunion.", 40));
        var dir = NewSession(transcript);
        try
        {
            var h = new StubHandler((HttpStatusCode.OK, ChatReply("- resume")));
            var settings = new AppSettings { MistralCrModel = "mistral-small-latest", PostprocessModel = "mistral-large-latest" };

            await new Summarizer(Mistral(h)).SummarizeAsync(dir, "k", settings, CancellationToken.None);

            Assert.Equal("mistral-small-latest", ModelOf(h.Requests.Single().Content));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private const string ValidMinutesJson =
        "{\"lang\":\"fr\",\"subject\":\"Projet | Point : Compte rendu (16 septembre 2026)\",\"context\":\"Point d'avancement.\"," +
        "\"key_elements\":[{\"title\":\"Sujet\",\"description\":\"Discute.\"}],\"points_of_arbitration\":[],\"next_steps\":[]}";

    private static MinutesRequest Minutes(string dir, AppSettings settings)
        => new(dir, "k", settings, new CrTemplate { Id = "detailed", Name = "Détaillé", Base = "detailed", Builtin = true });

    private static Vocabulary NoVocab()
        => new(defaultLexiconPath: Path.Combine(Path.GetTempPath(), "echo_models_no_lexicon.txt"),
               missionBiasPath: Path.Combine(Path.GetTempPath(), "echo_models_no_bias.txt"), env: _ => null);

    [Fact]
    public async Task MinutesGenerator_UsesMistralCrModel_FromSettings()
    {
        var dir = NewSession("[00:01] A : bonjour a tous, on demarre le point.\n[00:05] B : ok.");
        try
        {
            var h = new StubHandler((HttpStatusCode.OK, ChatReply(ValidMinutesJson)));
            var gen = new MinutesGenerator(Mistral(h), vocab: NoVocab(), env: _ => null);
            var settings = new AppSettings { MistralCrModel = "mistral-small-latest", CrFormats = new() { "md" } };

            var result = await gen.GenerateAsync(Minutes(dir, settings), null, CancellationToken.None);

            Assert.Equal("mistral-small-latest", ModelOf(h.Requests.Single().Content));
            Assert.Contains(result.OutputFiles, f => f.EndsWith(" - Compte rendu.md"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task MinutesGenerator_EnvVar_StillOverridesTheSetting()
    {
        var dir = NewSession("[00:01] A : bonjour a tous, on demarre le point.\n[00:05] B : ok.");
        try
        {
            var h = new StubHandler((HttpStatusCode.OK, ChatReply(ValidMinutesJson)));
            var gen = new MinutesGenerator(Mistral(h), vocab: NoVocab(), env: k => k == "MINUTES_MODEL" ? "mistral-medium-latest" : null);
            var settings = new AppSettings { MistralCrModel = "mistral-small-latest", CrFormats = new() { "md" } };

            await gen.GenerateAsync(Minutes(dir, settings), null, CancellationToken.None);

            Assert.Equal("mistral-medium-latest", ModelOf(h.Requests.Single().Content));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task MinutesGenerator_BlankSetting_FallsBackToDefaultModel()
    {
        var dir = NewSession("[00:01] A : bonjour a tous, on demarre le point.\n[00:05] B : ok.");
        try
        {
            var h = new StubHandler((HttpStatusCode.OK, ChatReply(ValidMinutesJson)));
            var gen = new MinutesGenerator(Mistral(h), vocab: NoVocab(), env: _ => null);
            var settings = new AppSettings { MistralCrModel = "  ", CrFormats = new() { "md" } };

            await gen.GenerateAsync(Minutes(dir, settings), null, CancellationToken.None);

            Assert.Equal(MinutesGenerator.DefaultModel, ModelOf(h.Requests.Single().Content));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
