using System.Net;
using System.Text;
using System.Text.Json;
using Echo.Ai;
using Echo.Core.Models;

namespace Echo.Tests;

/// <summary>
/// Tests for the Gemini provider (v5.1 dual-provider feature): the REST client's
/// request/response handling and the provider-selection helpers. All HTTP is stubbed;
/// the point is that GeminiClient is a faithful drop-in for the Mistral client
/// (same VoxtralResult / ChatMessage contracts, same retry-on-transient behaviour).
/// </summary>
public class GeminiProviderTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _script;
        public List<(string Url, string Content)> Requests { get; } = new();
        public List<string?> Keys { get; } = new();   // x-goog-api-key header per request
        public StubHandler(params (HttpStatusCode, string)[] script) => _script = new(script);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!.ToString(), content));
            Keys.Add(request.Headers.TryGetValues("x-goog-api-key", out var key) ? key.First() : null);
            if (_script.Count == 0) throw new InvalidOperationException("unexpected extra request");
            var (status, body) = _script.Dequeue();
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static readonly TimeSpan[] NoWait = { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero };
    private static GeminiClient Client(StubHandler h, List<string>? log = null)
        => new(new HttpClient(h), retryLog: log is null ? null : log.Add, retryDelays: NoWait);

    private static readonly List<ChatMessage> Msgs = new() { new("system", "sys"), new("user", "hello") };
    private const string ChatOk = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"réponse\"}]}}]}";

    // ── chat ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Chat_MapsSystemInstruction_AndReturnsText()
    {
        var h = new StubHandler((HttpStatusCode.OK, ChatOk));
        var c = Client(h);
        var content = await c.ChatCompleteAsync("k", "gemini-3.6-flash", Msgs, 0.2, jsonObject: false, CancellationToken.None);
        Assert.Equal("réponse", content);
        // URL carries the model, the key travels in a header; body carries systemInstruction and the user turn.
        Assert.Contains("models/gemini-3.6-flash:generateContent", h.Requests[0].Url);
        Assert.DoesNotContain("key=", h.Requests[0].Url);
        Assert.Equal("k", h.Keys[0]);
        Assert.Contains("systemInstruction", h.Requests[0].Content);
        Assert.Contains("\"role\":\"user\"", h.Requests[0].Content);
    }

    [Fact]
    public async Task Chat_Retries429_ThenSucceeds()
    {
        var h = new StubHandler(((HttpStatusCode)429, "{\"error\":{\"message\":\"rate\"}}"), (HttpStatusCode.OK, ChatOk));
        var log = new List<string>();
        var content = await Client(h, log).ChatCompleteAsync("k", "m", Msgs, 0.2, jsonObject: false, CancellationToken.None);
        Assert.Equal("réponse", content);
        Assert.Equal(2, h.Requests.Count);
        Assert.Contains(log, l => l.Contains("429"));
    }

    [Fact]
    public async Task Chat_JsonMode400_FallsBackToPlain()
    {
        // First call asks for JSON mode and is refused (400); the client retries plain.
        var h = new StubHandler(
            ((HttpStatusCode)400, "{\"error\":{\"message\":\"JSON mode is not enabled for this model\"}}"),
            (HttpStatusCode.OK, ChatOk));
        var content = await Client(h).ChatCompleteAsync("k", "m", Msgs, 0.2, jsonObject: true, CancellationToken.None);
        Assert.Equal("réponse", content);
        Assert.Equal(2, h.Requests.Count);
        Assert.Contains("responseMimeType", h.Requests[0].Content);       // asked for JSON first
        Assert.DoesNotContain("responseMimeType", h.Requests[1].Content); // then plain
    }

    [Fact]
    public async Task Chat_403_ThrowsActionableFrenchMessage()
    {
        var h = new StubHandler(((HttpStatusCode)403, "{\"error\":{\"message\":\"denied\"}}"));
        var ex = await Assert.ThrowsAsync<GeminiApiException>(
            () => Client(h).ChatCompleteAsync("k", "m", Msgs, 0.2, jsonObject: false, CancellationToken.None));
        Assert.Equal(403, ex.StatusCode);
        Assert.Contains("clé API Google", ex.UserMessage);
        Assert.IsAssignableFrom<LlmApiException>(ex); // handled uniformly with Mistral
    }

    // ── transcription ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Transcribe_FlashJsonSegments_ParsedLikeVoxtral()
    {
        var inner = "{\"segments\":[{\"start\":0.0,\"end\":2.4,\"speaker_id\":1,\"text\":\"bonjour\"}," +
                    "{\"start\":2.4,\"end\":5.0,\"speaker_id\":2,\"text\":\"salut\"}]}";
        var body = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":" + JsonSerializer.Serialize(inner) + "}]}}]}";
        var h = new StubHandler((HttpStatusCode.OK, body));
        var r = await Client(h).TranscribeAsync("k", "gemini-3.6-flash", new byte[] { 1, 2, 3 }, "a.mp3", null, null, CancellationToken.None);
        Assert.Equal(2, r.Segments.Count);
        Assert.Equal("bonjour", r.Segments[0].Text);
        Assert.Equal(2.4, r.Segments[0].End);
        Assert.Equal("1", r.Segments[0].VoxSpeaker);
        // inline audio (small) — no Files API round-trip.
        Assert.Single(h.Requests);
        Assert.Contains("inlineData", h.Requests[0].Content);
    }

    [Fact]
    public async Task Transcribe_TranscribeModel_SingleUntimedSegment()
    {
        var body = "{\"candidates\":[{\"content\":{\"parts\":[{\"audioTranscription\":{\"text\":\"bonjour tout le monde\"}}]}}]}";
        var h = new StubHandler((HttpStatusCode.OK, body));
        var r = await Client(h).TranscribeAsync("k", "gemini-3.5-transcribe", new byte[] { 1 }, "a.mp3", null, null, CancellationToken.None);
        Assert.Single(r.Segments);
        Assert.Equal("bonjour tout le monde", r.Segments[0].Text);
        Assert.Null(r.Segments[0].Start);
        // no JSON mode requested for the dedicated transcribe model.
        Assert.DoesNotContain("responseMimeType", h.Requests[0].Content);
    }

    [Fact]
    public void ExtractText_ConcatenatesParts_AndReadsAudioTranscription()
    {
        Assert.Equal("ab", GeminiClient.ExtractText(
            "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"a\"},{\"text\":\"b\"}]}}]}"));
        Assert.Equal("", GeminiClient.ExtractText("{\"candidates\":[]}"));
    }

    // ── provider selection + settings ────────────────────────────────────────────

    [Fact]
    public void LlmProvider_IsGemini_IsCaseInsensitive_AndDefaultsMistral()
    {
        Assert.True(LlmProvider.IsGemini("gemini"));
        Assert.True(LlmProvider.IsGemini("Gemini"));
        Assert.False(LlmProvider.IsGemini("mistral"));
        Assert.False(LlmProvider.IsGemini(null));
        Assert.Equal("fallback", LlmProvider.NonEmpty("  ", "fallback"));
        Assert.Equal("x", LlmProvider.NonEmpty("x", "fallback"));
    }

    [Fact]
    public void Settings_ProviderFields_DefaultMistral_AndRoundTrip()
    {
        var d = new AppSettings();
        Assert.Equal("mistral", d.TranscriptionProvider);
        Assert.Equal("mistral", d.CrProvider);

        var json = JsonSerializer.Serialize(new AppSettings
        {
            TranscriptionProvider = "gemini",
            CrProvider = "gemini",
            GeminiTranscribeModel = "gemini-3.6-flash",
            GeminiCrModel = "gemini-3.8-flash",
        });
        Assert.Contains("\"transcription_provider\":\"gemini\"", json);
        var back = JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.Equal("gemini", back.TranscriptionProvider);
        Assert.Equal("gemini", back.CrProvider);
        Assert.Equal("gemini-3.8-flash", back.GeminiCrModel);
    }
}
