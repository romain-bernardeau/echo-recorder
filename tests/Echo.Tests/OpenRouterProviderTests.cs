using System.Net;
using System.Text;
using System.Text.Json;
using Echo.Ai;

namespace Echo.Tests;

/// <summary>
/// OpenRouter provider: OpenAI-compatible chat for the CR / summary / correction, public model
/// catalogue with prices and context windows, key check. All HTTP is stubbed. Pins the request
/// shape (model, attribution headers, response_format only for the CR), the retry ladder, the
/// French user messages, the context-overflow flag, the "empty answer is an error" rule and the
/// Free flag read from the catalogue data.
/// </summary>
public class OpenRouterProviderTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _script;
        public List<(string Method, string Url, string Content, string? Auth, string? Referer, string? Title)> Requests { get; } = new();
        public StubHandler(params (HttpStatusCode, string)[] script) => _script = new(script);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            string? H(string name) => request.Headers.TryGetValues(name, out var v) ? string.Join(",", v) : null;
            Requests.Add((request.Method.Method, request.RequestUri!.ToString(), content,
                request.Headers.Authorization?.ToString(), H("HTTP-Referer"), H("X-Title")));
            if (_script.Count == 0) throw new InvalidOperationException("unexpected extra request");
            var (status, body) = _script.Dequeue();
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static readonly TimeSpan[] NoWait = { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero };
    private static OpenRouterClient Client(StubHandler h, List<string>? log = null)
        => new(new HttpClient(h), retryLog: log is null ? null : log.Add, retryDelays: NoWait);

    private static readonly IReadOnlyList<ChatMessage> Msgs = new[] { new ChatMessage("system", "s"), new ChatMessage("user", "u") };
    private const string Model = "google/gemma-4-31b-it:free";
    private static string Answer(string content, string finish = "stop")
        => JsonSerializer.Serialize(new { id = "gen-1", choices = new[] { new { index = 0, finish_reason = finish, message = new { role = "assistant", content } } } });

    // ── chat ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Chat_PostsModelAndHeaders_ResponseFormatOnlyForTheCr()
    {
        var h = new StubHandler((HttpStatusCode.OK, Answer("{\"a\":1}")), (HttpStatusCode.OK, Answer("résumé")));
        var c = Client(h);

        Assert.Equal("{\"a\":1}", await c.ChatCompleteAsync("sk-or", Model, Msgs, 0.2, jsonObject: true, CancellationToken.None));
        Assert.Equal("résumé", await c.ChatCompleteAsync("sk-or", Model, Msgs, 0.2, jsonObject: false, CancellationToken.None));

        Assert.Equal(2, h.Requests.Count);
        foreach (var r in h.Requests)
        {
            Assert.Equal("POST", r.Method);
            Assert.Equal("https://openrouter.ai/api/v1/chat/completions", r.Url);
            Assert.Equal("Bearer sk-or", r.Auth);
            Assert.Equal(OpenRouterClient.Referer, r.Referer);
            Assert.Equal("Echo", r.Title);
            using var body = JsonDocument.Parse(r.Content);
            Assert.Equal(Model, body.RootElement.GetProperty("model").GetString());
            Assert.Equal(2, body.RootElement.GetProperty("messages").GetArrayLength());
        }
        using (var cr = JsonDocument.Parse(h.Requests[0].Content))
            Assert.Equal("json_object", cr.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        using (var sum = JsonDocument.Parse(h.Requests[1].Content))
            Assert.False(sum.RootElement.TryGetProperty("response_format", out _));
    }

    [Fact]
    public async Task Chat_ResponseFormatRefused_RetriesPlain()
    {
        var h = new StubHandler(
            (HttpStatusCode.BadRequest, "{\"error\":{\"code\":400,\"message\":\"response_format is not supported by this model\"}}"),
            (HttpStatusCode.OK, Answer("{\"ok\":true}")));
        var content = await Client(h).ChatCompleteAsync("k", Model, Msgs, 0.2, jsonObject: true, CancellationToken.None);
        Assert.Equal("{\"ok\":true}", content);
        Assert.Equal(2, h.Requests.Count);
        Assert.DoesNotContain("response_format", h.Requests[1].Content);
    }

    [Fact]
    public async Task Chat_429ThenSuccess_RetriesAndLogs()
    {
        var log = new List<string>();
        var h = new StubHandler(
            ((HttpStatusCode)429, "{\"error\":{\"code\":429,\"message\":\"Rate limit exceeded: free-models-per-min\"}}"),
            (HttpStatusCode.OK, Answer("ok")));
        var content = await Client(h, log).ChatCompleteAsync("k", Model, Msgs, 0.2, jsonObject: false, CancellationToken.None);
        Assert.Equal("ok", content);
        Assert.Equal(2, h.Requests.Count);
        Assert.Contains(log, l => l.StartsWith("[OpenRouter]") && l.Contains("429"));
    }

    [Fact]
    public async Task Chat_ErrorEnvelopeOnHttp200_IsRetriedLikeAnUpstreamFailure()
    {
        var h = new StubHandler(
            (HttpStatusCode.OK, "{\"error\":{\"code\":502,\"message\":\"Provider returned error\"}}"),
            (HttpStatusCode.OK, Answer("ok")));
        Assert.Equal("ok", await Client(h).ChatCompleteAsync("k", Model, Msgs, 0.2, jsonObject: false, CancellationToken.None));
        Assert.Equal(2, h.Requests.Count);
    }

    [Fact]
    public async Task Chat_401_NotRetried_UserMessageNamesTheKey()
    {
        var h = new StubHandler((HttpStatusCode.Unauthorized, "{\"error\":{\"code\":401,\"message\":\"No auth credentials found\"}}"));
        var ex = await Assert.ThrowsAsync<OpenRouterApiException>(
            () => Client(h).ChatCompleteAsync("bad", Model, Msgs, 0.2, jsonObject: false, CancellationToken.None));
        Assert.Single(h.Requests);
        Assert.Equal(401, ex.StatusCode);
        Assert.False(ex.IsRetryable);
        Assert.Contains("clé API OpenRouter refusée", ex.UserMessage);
        Assert.Contains("No auth credentials found", ex.ServerMessage);
    }

    [Fact]
    public async Task Chat_ContextOverflow_FlaggedAndNotRetriedWithoutJson()
    {
        var h = new StubHandler((HttpStatusCode.BadRequest,
            "{\"error\":{\"code\":400,\"message\":\"This endpoint's maximum context length is 262144 tokens. However, you requested about 300000 tokens.\"}}"));
        var ex = await Assert.ThrowsAsync<OpenRouterApiException>(
            () => Client(h).ChatCompleteAsync("k", Model, Msgs, 0.2, jsonObject: true, CancellationToken.None));
        Assert.True(ex.IsContextOverflow);
        Assert.Single(h.Requests);                       // a plain retry cannot shrink the prompt
        Assert.Contains("fenêtre de contexte", ex.UserMessage);
    }

    [Fact]
    public async Task Chat_EmptyAnswer_IsAnError_NotAnEmptyCr()
    {
        var h = new StubHandler((HttpStatusCode.OK, Answer("   ")));
        var ex = await Assert.ThrowsAsync<LlmEmptyResponseException>(
            () => Client(h).ChatCompleteAsync("k", Model, Msgs, 0.2, jsonObject: false, CancellationToken.None));
        Assert.False(ex.Truncated);
        Assert.Equal(LlmProvider.OpenRouter, ex.Provider);
    }

    [Fact]
    public async Task Chat_CutAtLength_IsATruncatedError()
    {
        var h = new StubHandler((HttpStatusCode.OK, Answer("{\"half\":", finish: "length")));
        var ex = await Assert.ThrowsAsync<LlmEmptyResponseException>(
            () => Client(h).ChatCompleteAsync("k", Model, Msgs, 0.2, jsonObject: false, CancellationToken.None));
        Assert.True(ex.Truncated);
    }

    // ── catalogue ───────────────────────────────────────────────────────────────

    private const string Catalogue = """
        {"data":[
          {"id":"vendor/paid-model","context_length":131072,"pricing":{"prompt":"0.00000059","completion":"0.00000079"},
           "architecture":{"output_modalities":["text"]}},
          {"id":"vendor/zero-priced","context_length":65536,"pricing":{"prompt":"0","completion":"0"},
           "architecture":{"output_modalities":["text"]}},
          {"id":"vendor/tagged:free","context_length":262144,"pricing":{"prompt":"0.0000001","completion":"0.0000002"},
           "architecture":{"output_modalities":["text"]}},
          {"id":"vendor/music-gen","context_length":1048576,"pricing":{"prompt":"0","completion":"0"},
           "architecture":{"output_modalities":["text","audio"]}},
          {"id":"vendor/router","context_length":200000,"pricing":{"prompt":"-1","completion":"-1"}}
        ]}
        """;

    [Fact]
    public async Task ListModels_IsPublic_ParsesFreeFlag_AndRemembersContextWindows()
    {
        var h = new StubHandler((HttpStatusCode.OK, Catalogue));
        var models = await Client(h).ListModelsAsync("", CancellationToken.None);

        var req = Assert.Single(h.Requests);
        Assert.Equal("GET", req.Method);
        Assert.Equal("https://openrouter.ai/api/v1/models", req.Url);
        Assert.Null(req.Auth);                                   // no key needed for the catalogue

        Assert.Equal(new[] { "vendor/music-gen", "vendor/paid-model", "vendor/router", "vendor/tagged:free", "vendor/zero-priced" },
            models.Select(m => m.Id).ToArray());
        Assert.False(models.Single(m => m.Id == "vendor/paid-model").Free);
        Assert.True(models.Single(m => m.Id == "vendor/zero-priced").Free);   // pricing "0" / "0"
        Assert.True(models.Single(m => m.Id == "vendor/tagged:free").Free);   // ":free" id
        Assert.False(models.Single(m => m.Id == "vendor/router").Free);       // -1 = variable price, not free
        Assert.False(models.Single(m => m.Id == "vendor/music-gen").Chat);    // audio output: not a chat model
        Assert.All(models, m => Assert.False(m.Transcribe));

        Assert.Equal(65536, ContextBudget.ContextTokens(LlmProvider.OpenRouter, "vendor/zero-priced"));
        Assert.Equal(32_000, ContextBudget.ContextTokens(LlmProvider.OpenRouter, "vendor/never-listed"));
    }

    [Fact]
    public async Task ListModels_HttpError_ThrowsProviderException()
    {
        var h = new StubHandler((HttpStatusCode.ServiceUnavailable, "{\"error\":{\"code\":503,\"message\":\"down\"}}"));
        var ex = await Assert.ThrowsAsync<OpenRouterApiException>(() => Client(h).ListModelsAsync("", CancellationToken.None));
        Assert.Equal(503, ex.StatusCode);
        Assert.Single(h.Requests);
    }

    // ── key check + probe ───────────────────────────────────────────────────────

    [Fact]
    public async Task ProbeKey_CallsKeyEndpoint_AndReportsStatus()
    {
        var h = new StubHandler(
            (HttpStatusCode.OK, "{\"data\":{\"label\":\"sk-or-v1-abc\",\"is_free_tier\":true}}"),
            (HttpStatusCode.Unauthorized, "{\"error\":{\"code\":401,\"message\":\"User not found.\"}}"));
        var c = Client(h);
        var ok = await c.ProbeKeyAsync("good", CancellationToken.None);
        var bad = await c.ProbeKeyAsync("bad", CancellationToken.None);

        Assert.True(ok.Ok);
        Assert.False(bad.Ok);
        Assert.Equal(401, bad.Status);
        Assert.Equal("User not found.", bad.ServerMessage);
        Assert.All(h.Requests, r => Assert.Equal("https://openrouter.ai/api/v1/key", r.Url));
        Assert.Equal("Bearer good", h.Requests[0].Auth);
    }

    [Fact]
    public async Task ProbeChatModel_OneAttempt_ReturnsStatusAsData()
    {
        var h = new StubHandler(((HttpStatusCode)429, "{\"error\":{\"code\":429,\"message\":\"Rate limit exceeded\"}}"));
        var r = await Client(h).ProbeChatModelAsync("k", Model, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(429, r.Status);
        Assert.Single(h.Requests);
        using var body = JsonDocument.Parse(h.Requests[0].Content);
        Assert.Equal(1, body.RootElement.GetProperty("max_tokens").GetInt32());
    }
}
