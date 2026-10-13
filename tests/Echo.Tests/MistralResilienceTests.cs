using System.Net;
using System.Text;
using Echo.Ai;
using Echo.Core.Models;

namespace Echo.Tests;

/// <summary>
/// Regression tests for the transient-failure handling added after two real
/// meetings failed on a Mistral 429 with no retry (2026-07-13): backoff retries,
/// no feature-shedding on rate limits, actionable French error messages, and the
/// Summarizer's full-coverage / faithfulness behaviours. All HTTP is stubbed.
/// </summary>
public class MistralResilienceTests
{
    /// <summary>Scripted HttpMessageHandler: returns canned (status, body) responses
    /// in order and records every request (with its content read to a string).</summary>
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
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static readonly TimeSpan[] NoWait = { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero };

    private static MistralClient Client(StubHandler handler, List<string>? retryLog = null)
        => new(new HttpClient(handler), retryLog: retryLog is null ? null : retryLog.Add, retryDelays: NoWait);

    private const string ChatOk = "{\"choices\":[{\"message\":{\"content\":\"réponse\"}}]}";
    private const string VoxOk = "{\"text\":\"ok\",\"segments\":[]}";
    private static readonly List<ChatMessage> Msgs = new() { new("user", "hello") };

    // ── chat retries ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Chat_RetriesA429_ThenSucceeds()
    {
        var h = new StubHandler(((HttpStatusCode)429, "{\"message\":\"rate limited\"}"), (HttpStatusCode.OK, ChatOk));
        var log = new List<string>();
        var c = Client(h, log);

        var content = await c.ChatCompleteAsync("k", "m", Msgs, 0.2, jsonObject: false, CancellationToken.None);

        Assert.Equal("réponse", content);
        Assert.Equal(2, h.Requests.Count);
        Assert.Contains(log, l => l.Contains("429"));
    }

    [Fact]
    public async Task Chat_429Exhausted_ThrowsActionableFrenchMessage()
    {
        var h = new StubHandler(
            ((HttpStatusCode)429, ""), ((HttpStatusCode)429, ""), ((HttpStatusCode)429, ""),
            ((HttpStatusCode)429, ""), ((HttpStatusCode)429, ""));
        var c = Client(h);

        var ex = await Assert.ThrowsAsync<MistralApiException>(
            () => c.ChatCompleteAsync("k", "m", Msgs, 0.2, jsonObject: false, CancellationToken.None));

        Assert.Equal(429, ex.StatusCode);
        Assert.Equal(5, ex.Attempts);          // initial + 4 retries
        Assert.Equal(5, h.Requests.Count);
        Assert.Contains("Mistral", ex.UserMessage);
        Assert.Contains("429", ex.UserMessage);
    }

    [Fact]
    public async Task Chat_JsonMode_FallsBackToPlain_OnlyOn400()
    {
        // 400 on the json_object request -> retried WITHOUT response_format, no backoff retry.
        var h = new StubHandler((HttpStatusCode.BadRequest, "{\"message\":\"response_format not supported\"}"),
                                (HttpStatusCode.OK, ChatOk));
        var c = Client(h);

        var content = await c.ChatCompleteAsync("k", "m", Msgs, 0.2, jsonObject: true, CancellationToken.None);

        Assert.Equal("réponse", content);
        Assert.Equal(2, h.Requests.Count);
        Assert.Contains("response_format", h.Requests[0].Content);
        Assert.DoesNotContain("response_format", h.Requests[1].Content);
    }

    [Fact]
    public async Task Chat_JsonMode_DoesNotFallBack_OnRateLimit()
    {
        // A 429 must NOT strip JSON mode: every attempt keeps response_format and the
        // rate-limit error surfaces once retries are exhausted.
        var h = new StubHandler(
            ((HttpStatusCode)429, ""), ((HttpStatusCode)429, ""), ((HttpStatusCode)429, ""),
            ((HttpStatusCode)429, ""), ((HttpStatusCode)429, ""));
        var c = Client(h);

        var ex = await Assert.ThrowsAsync<MistralApiException>(
            () => c.ChatCompleteAsync("k", "m", Msgs, 0.2, jsonObject: true, CancellationToken.None));

        Assert.Equal(429, ex.StatusCode);
        Assert.Equal(5, h.Requests.Count);
        Assert.All(h.Requests, r => Assert.Contains("response_format", r.Content));
    }

    [Fact]
    public async Task Chat_401_NoRetry_KeyMessage()
    {
        var h = new StubHandler((HttpStatusCode.Unauthorized, "{\"message\":\"Unauthorized\"}"));
        var c = Client(h);

        var ex = await Assert.ThrowsAsync<MistralApiException>(
            () => c.ChatCompleteAsync("k", "m", Msgs, 0.2, jsonObject: false, CancellationToken.None));

        Assert.Equal(401, ex.StatusCode);
        Assert.Single(h.Requests);             // permanent error: no retry
        Assert.Contains("clé API", ex.UserMessage);
    }

    // ── transcription ladder vs retry ───────────────────────────────────────────

    [Fact]
    public async Task Transcription_LadderDegradesOn422_NotOnRateLimit()
    {
        // 422 on the full call (bias + diarize) -> retry without bias -> then without diarize.
        var h = new StubHandler(
            (HttpStatusCode.UnprocessableEntity, "{\"message\":\"context_bias rejected\"}"),
            (HttpStatusCode.UnprocessableEntity, "{\"message\":\"diarize rejected\"}"),
            (HttpStatusCode.OK, VoxOk));
        var c = Client(h);

        var res = await c.TranscribeAsync("k", "m", new byte[] { 1, 2 }, "a.wav",
            new[] { "SteerCo" }, null, CancellationToken.None);

        Assert.Equal("ok", res.Text);
        Assert.Equal(3, h.Requests.Count);
        Assert.Contains("context_bias", h.Requests[0].Content);   // full
        Assert.DoesNotContain("context_bias", h.Requests[1].Content); // no bias, still diarize
        Assert.Contains("diarize", h.Requests[1].Content);
        Assert.DoesNotContain("diarize", h.Requests[2].Content);  // plain
    }

    [Fact]
    public async Task Transcription_429_RetriesWithoutShedding_ThenSurfaces()
    {
        var h = new StubHandler(
            ((HttpStatusCode)429, ""), ((HttpStatusCode)429, ""), ((HttpStatusCode)429, ""),
            ((HttpStatusCode)429, ""), ((HttpStatusCode)429, ""));
        var c = Client(h);

        var ex = await Assert.ThrowsAsync<MistralApiException>(() => c.TranscribeAsync(
            "k", "m", new byte[] { 1 }, "a.wav", new[] { "SteerCo" }, null, CancellationToken.None));

        Assert.Equal(429, ex.StatusCode);
        Assert.Equal(5, h.Requests.Count);
        // A rate limit must never silently drop diarization/bias.
        Assert.All(h.Requests, r => Assert.Contains("diarize", r.Content));
        Assert.All(h.Requests, r => Assert.Contains("context_bias", r.Content));
    }

    [Fact]
    public void ExtractServerMessage_ReadsCommonShapes()
    {
        Assert.Equal("boom", MistralClient.ExtractServerMessage("{\"message\":\"boom\"}"));
        Assert.Equal("inner", MistralClient.ExtractServerMessage("{\"error\":{\"message\":\"inner\"}}"));
        Assert.Equal("det", MistralClient.ExtractServerMessage("{\"detail\":\"det\"}"));
        Assert.Null(MistralClient.ExtractServerMessage(""));
    }

    // ── summarizer: coverage + faithfulness guard ───────────────────────────────

    private static string NewSession(string transcript)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"echo_sum_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "R - Transcript.txt"), transcript, new UTF8Encoding(false));
        return dir;
    }

    [Fact]
    public async Task Summarizer_TooShortTranscript_WritesHonestNote_WithoutAnyApiCall()
    {
        var dir = NewSession("Bonjour. Personne n'est venu.");
        try
        {
            var h = new StubHandler(); // any request would throw "unexpected extra request"
            var s = new Summarizer(Client(h));

            var path = await s.SummarizeAsync(dir, "k", new AppSettings(), CancellationToken.None);

            Assert.Empty(h.Requests);
            Assert.Contains("pas assez d'échanges", File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Summarizer_LongTranscript_SingleCall_SendsTheWholeMeeting()
    {
        // 64 k chars (a real case): the old 20 k cap summarized only the first
        // third of the meeting. Must now go out in ONE full call. ASCII text and a
        // single-line end marker on purpose: the JSON body escapes non-ASCII and
        // newlines, which would defeat a raw multi-line Contains().
        var transcript = string.Join("\n", Enumerable.Repeat("[00:01] A : point discute en reunion.", 1600))
                         + "\n[01:35] B : cloture de la reunion marker0042.";
        Assert.InRange(transcript.Length, 20_001, Summarizer.SingleCallCap);
        var dir = NewSession(transcript);
        try
        {
            var h = new StubHandler((HttpStatusCode.OK, ChatOk));
            var s = new Summarizer(Client(h));

            await s.SummarizeAsync(dir, "k", new AppSettings(), CancellationToken.None);

            var sent = h.Requests.Single().Content;
            Assert.Contains("marker0042", sent); // the END of the meeting was sent too
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Summarizer_VeryLongTranscript_MapReduces()
    {
        var transcript = string.Join("\n", Enumerable.Repeat("[00:01] A : tres longue reunion vraiment.", 6000)); // ~252 k, ASCII (JSON-escape safe)
        Assert.True(transcript.Length > Summarizer.SingleCallCap);
        int parts = Summarizer.SplitAtLines(transcript, Summarizer.ChunkChars).Count;
        var dir = NewSession(transcript);
        try
        {
            var script = Enumerable.Repeat((HttpStatusCode.OK, ChatOk), parts + 1).ToArray();
            var h = new StubHandler(script);
            var s = new Summarizer(Client(h));

            var path = await s.SummarizeAsync(dir, "k", new AppSettings(), CancellationToken.None);

            Assert.Equal(parts + 1, h.Requests.Count); // N part-summaries + 1 merge
            Assert.Contains("partiels successifs", h.Requests[^1].Content); // merge prompt (ASCII slice)
            Assert.Equal("réponse", File.ReadAllText(path).Trim());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void SplitAtLines_RespectsMaxAndKeepsAllContent()
    {
        var text = string.Join("\n", Enumerable.Range(0, 500).Select(i => $"ligne {i} {new string('x', 40)}"));
        var parts = Summarizer.SplitAtLines(text, 1000);

        Assert.All(parts, p => Assert.True(p.Length <= 1000));
        Assert.Equal(text.Replace("\n", ""), string.Concat(parts).Replace("\n", ""));
    }
}
