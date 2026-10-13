using System.Net;
using System.Text;
using System.Text.Json;
using Echo.Ai;
using Echo.Core.Models;

namespace Echo.Tests;

/// <summary>
/// Cloudflare Workers AI provider (v5.3): Whisper large v3 turbo for transcription (sent in
/// pieces), OpenAI-compatible chat, models/search catalogue. All HTTP is stubbed. The point
/// is that CloudflareClient is a faithful third drop-in beside Mistral and Gemini: same
/// VoxtralResult / ChatMessage contracts, same retry-on-transient behaviour, and the v4
/// envelope's <c>success:false</c> is treated as a failure even on HTTP 200.
/// </summary>
public class CloudflareProviderTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _script;
        public List<(string Url, string Content, string? Auth)> Requests { get; } = new();
        public StubHandler(params (HttpStatusCode, string)[] script) => _script = new(script);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!.ToString(), content, request.Headers.Authorization?.ToString()));
            if (_script.Count == 0) throw new InvalidOperationException("unexpected extra request");
            var (status, body) = _script.Dequeue();
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static readonly TimeSpan[] NoWait = { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero };
    private const string Acct = "0123456789abcdef0123456789abcdef";
    private static CloudflareClient Client(StubHandler h, string? acct = Acct, List<string>? log = null)
        => new(() => acct, new HttpClient(h), retryLog: log is null ? null : log.Add, retryDelays: NoWait);

    private const string WhisperOk = """
        {"result":{"text":"Bonjour à tous. Sous-titres réalisés par la communauté d'Amara.org","word_count":3,
          "transcription_info":{"language":"fr","language_probability":0.98,"duration":12.5},
          "segments":[
            {"start":0.0,"end":2.4,"text":" Bonjour à tous.","no_speech_prob":0.01,"avg_logprob":-0.2},
            {"start":2.4,"end":4.0,"text":"","no_speech_prob":0.1},
            {"start":4.0,"end":6.0,"text":"Sous-titres réalisés par la communauté d'Amara.org","no_speech_prob":0.2},
            {"start":6.0,"end":9.0,"text":"On commence ?","no_speech_prob":0.95},
            {"start":9.0,"end":12.5,"text":"Deuxième phrase.","no_speech_prob":0.05}
          ]},
         "success":true,"errors":[],"messages":[]}
        """;

    // ── transcription ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Transcribe_PostsBase64ToAccountRunUrl_WithInitialPrompt_AndParsesSegments()
    {
        var h = new StubHandler((HttpStatusCode.OK, WhisperOk));
        var audio = new byte[] { 1, 2, 3, 4 };
        var r = await Client(h).TranscribeAsync("tok", "@cf/openai/whisper-large-v3-turbo", audio, "mic.mp3",
            new[] { "Contoso", "COPIL", "S2C" }, null, CancellationToken.None);

        var req = Assert.Single(h.Requests);
        Assert.Equal($"https://api.cloudflare.com/client/v4/accounts/{Acct}/ai/run/@cf/openai/whisper-large-v3-turbo", req.Url);
        Assert.Equal("Bearer tok", req.Auth);
        using var body = JsonDocument.Parse(req.Content);
        Assert.Equal(Convert.ToBase64String(audio), body.RootElement.GetProperty("audio").GetString());
        Assert.Equal("transcribe", body.RootElement.GetProperty("task").GetString());
        Assert.Contains("Contoso, COPIL, S2C", body.RootElement.GetProperty("initial_prompt").GetString());

        Assert.Equal("fr", r.Language);
        // empty, subtitle-credit hallucination and no_speech > 0.85 segments dropped; text trimmed; no speaker.
        Assert.Equal(new[] { "Bonjour à tous.", "Deuxième phrase." }, r.Segments.Select(s => s.Text).ToArray());
        Assert.Equal(0.0, r.Segments[0].Start);
        Assert.Equal(12.5, r.Segments[1].End);
        Assert.All(r.Segments, s => Assert.Null(s.VoxSpeaker));
    }

    [Fact]
    public async Task Transcribe_NoBias_OmitsInitialPrompt()
    {
        var h = new StubHandler((HttpStatusCode.OK, WhisperOk));
        await Client(h).TranscribeAsync("tok", "@cf/openai/whisper-large-v3-turbo", new byte[] { 9 }, "a.mp3", null, null, CancellationToken.None);
        Assert.DoesNotContain("initial_prompt", h.Requests[0].Content);
    }

    [Fact]
    public async Task Transcribe_BlankAccountId_Throws404Shaped_WithoutCallingNetwork()
    {
        var h = new StubHandler();
        var ex = await Assert.ThrowsAsync<CloudflareApiException>(() =>
            Client(h, acct: "  ").TranscribeAsync("tok", "m", new byte[] { 1 }, "a.mp3", null, null, CancellationToken.None));
        Assert.Equal(404, ex.StatusCode);
        Assert.Contains("compte", ex.UserMessage);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task Transcribe_Http200_SuccessFalse_IsAFailure_NotRetried()
    {
        const string bad = """{"result":null,"success":false,"errors":[{"code":7009,"message":"Upstream service unavailable"}],"messages":[]}""";
        var h = new StubHandler((HttpStatusCode.OK, bad));
        var ex = await Assert.ThrowsAsync<CloudflareApiException>(() =>
            Client(h).TranscribeAsync("tok", "m", new byte[] { 1 }, "a.mp3", null, null, CancellationToken.None));
        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("Upstream service unavailable", ex.ServerMessage);
        Assert.Contains("7009", ex.ServerMessage);
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task Transcribe_Retries429_ThenSucceeds()
    {
        var h = new StubHandler(((HttpStatusCode)429, """{"success":false,"errors":[{"code":10000,"message":"rate"}]}"""), (HttpStatusCode.OK, WhisperOk));
        var log = new List<string>();
        var r = await Client(h, log: log).TranscribeAsync("tok", "m", new byte[] { 1 }, "a.mp3", null, null, CancellationToken.None);
        Assert.Equal(2, h.Requests.Count);
        Assert.Equal(2, r.Segments.Count);
        Assert.Contains(log, l => l.Contains("[Cloudflare]") && l.Contains("429"));
    }

    [Fact]
    public async Task Transcribe_401_NotRetried_UserMessageMentionsToken()
    {
        var h = new StubHandler((HttpStatusCode.Unauthorized, """{"success":false,"errors":[{"code":10000,"message":"Authentication error"}]}"""));
        var ex = await Assert.ThrowsAsync<CloudflareApiException>(() =>
            Client(h).TranscribeAsync("tok", "m", new byte[] { 1 }, "a.mp3", null, null, CancellationToken.None));
        Assert.Equal(401, ex.StatusCode);
        Assert.Single(h.Requests);
        Assert.Contains("jeton", ex.UserMessage);
        Assert.False(ex.IsRetryable);
    }

    [Fact]
    public void Whisper_TextOnlyBody_BecomesOneUntimedSegment()
    {
        var r = WhisperResponseParser.Parse("""{"result":{"text":"juste du texte"},"success":true}""");
        var s = Assert.Single(r.Segments);
        Assert.Equal("juste du texte", s.Text);
        Assert.Null(s.Start);
    }

    [Fact]
    public void Client_IsChunked_At10Minutes()
    {
        var c = Client(new StubHandler());
        Assert.IsAssignableFrom<IChunkedTranscribe>(c);
        Assert.Equal(600, ((IChunkedTranscribe)c).ChunkSeconds);
    }

    // ── chat (OpenAI-compatible) ─────────────────────────────────────────────────

    private static readonly List<ChatMessage> Msgs = new() { new("system", "sys"), new("user", "hello") };
    private const string ChatOk = """{"id":"x","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"réponse"},"finish_reason":"stop"}]}""";

    [Fact]
    public async Task Chat_PostsToV1ChatCompletions_WithResponseFormat_AndReturnsContent()
    {
        var h = new StubHandler((HttpStatusCode.OK, ChatOk));
        var content = await Client(h).ChatCompleteAsync("tok", "@cf/meta/llama-3.3-70b-instruct-fp8-fast", Msgs, 0.1, jsonObject: true, CancellationToken.None);
        Assert.Equal("réponse", content);
        var req = Assert.Single(h.Requests);
        Assert.Equal($"https://api.cloudflare.com/client/v4/accounts/{Acct}/ai/v1/chat/completions", req.Url);
        using var body = JsonDocument.Parse(req.Content);
        Assert.Equal("@cf/meta/llama-3.3-70b-instruct-fp8-fast", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("json_object", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal(2, body.RootElement.GetProperty("messages").GetArrayLength());
        // Without an explicit max_tokens Workers AI stops at 256 tokens and the minutes JSON is cut.
        Assert.Equal(CloudflareClient.ChatMaxTokens, body.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task Chat_JsonMode_FallsBackPlain_On400()
    {
        var h = new StubHandler((HttpStatusCode.BadRequest, """{"success":false,"errors":[{"code":3030,"message":"response_format not supported"}]}"""), (HttpStatusCode.OK, ChatOk));
        var content = await Client(h).ChatCompleteAsync("tok", "m", Msgs, 0.1, jsonObject: true, CancellationToken.None);
        Assert.Equal("réponse", content);
        Assert.Equal(2, h.Requests.Count);
        Assert.Contains("response_format", h.Requests[0].Content);
        Assert.DoesNotContain("response_format", h.Requests[1].Content);
    }

    [Fact]
    public void Chat_ExtractsNativeResultResponseShapeToo()
    {
        Assert.Equal("ok", CloudflareClient.ExtractChatContent("""{"result":{"response":"ok"},"success":true}"""));
        Assert.Equal("", CloudflareClient.ExtractChatContent("""{"result":{},"success":true}"""));
    }

    // ── catalogue + probes ───────────────────────────────────────────────────────

    private const string ModelsPage = """
        {"result":[
          {"id":"1","name":"@cf/openai/whisper-large-v3-turbo","task":{"name":"Automatic Speech Recognition"}},
          {"id":"2","name":"@cf/meta/llama-3.3-70b-instruct-fp8-fast","task":{"name":"Text Generation"}},
          {"id":"3","name":"@cf/baai/bge-large-en-v1.5","task":{"name":"Text Embeddings"}},
          {"id":"4","name":"@cf/meta/m2m100-1.2b","task":{"name":"Translation"}},
          {"id":"5","name":"@cf/openai/whisper","task":{"name":"Automatic Speech Recognition"}}
        ],"success":true,"errors":[],"messages":[]}
        """;

    [Fact]
    public async Task ListModels_FiltersByTask_SortsAndStopsAfterShortPage()
    {
        var h = new StubHandler((HttpStatusCode.OK, ModelsPage));
        var models = await Client(h).ListModelsAsync("tok", CancellationToken.None);
        Assert.Single(h.Requests);
        Assert.Contains($"/accounts/{Acct}/ai/models/search?per_page=100&page=1", h.Requests[0].Url);
        Assert.Equal(new[] { "@cf/meta/llama-3.3-70b-instruct-fp8-fast", "@cf/openai/whisper", "@cf/openai/whisper-large-v3-turbo" },
            models.Select(m => m.Id).ToArray());
        Assert.True(models.Single(m => m.Id.EndsWith("turbo")).Transcribe);
        Assert.False(models.Single(m => m.Id.EndsWith("turbo")).Chat);
        Assert.True(models.Single(m => m.Id.Contains("llama")).Chat);
        Assert.False(models.Single(m => m.Id.Contains("llama")).Transcribe);
    }

    [Fact]
    public async Task ListModels_403_Throws()
    {
        var h = new StubHandler((HttpStatusCode.Forbidden, """{"success":false,"errors":[{"code":10000,"message":"Authentication error"}]}"""));
        var ex = await Assert.ThrowsAsync<CloudflareApiException>(() => Client(h).ListModelsAsync("tok", CancellationToken.None));
        Assert.Equal(403, ex.StatusCode);
    }

    [Fact]
    public async Task ProbeAccount_ReportsStatusAsData()
    {
        var ok = await Client(new StubHandler((HttpStatusCode.OK, ModelsPage))).ProbeAccountAsync("tok", CancellationToken.None);
        Assert.True(ok.Ok);
        var nf = await Client(new StubHandler((HttpStatusCode.NotFound, """{"success":false,"errors":[{"code":7003,"message":"Could not route to /accounts/x"}]}""")))
            .ProbeAccountAsync("tok", CancellationToken.None);
        Assert.False(nf.Ok);
        Assert.Equal(404, nf.Status);
        var blank = await Client(new StubHandler(), acct: "").ProbeAccountAsync("tok", CancellationToken.None);
        Assert.False(blank.Ok);
        Assert.Equal(404, blank.Status);
    }

    [Fact]
    public async Task ProbeChatModel_OneAttempt_NoRetry()
    {
        var h = new StubHandler(((HttpStatusCode)429, """{"success":false,"errors":[{"code":10000,"message":"busy"}]}"""));
        var r = await Client(h).ProbeChatModelAsync("tok", "m", CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(429, r.Status);
        Assert.Single(h.Requests);
    }

    // ── provider helpers ─────────────────────────────────────────────────────────

    [Fact]
    public void LlmProvider_KnowsCloudflare()
    {
        Assert.True(LlmProvider.IsCloudflare(" Cloudflare "));
        Assert.Equal("cloudflare", LlmProvider.Normalize("CLOUDFLARE"));
        Assert.Equal("gemini", LlmProvider.Normalize("gemini"));    // offered again (5.6.1 had retired it)
        Assert.Equal("mistral", LlmProvider.Normalize("openai"));   // unknown → historical default
        Assert.Equal("Cloudflare", LlmProvider.DisplayName("cloudflare"));
        Assert.Equal(new[] { "mistral", "gemini", "cloudflare", "openrouter" }, LlmProvider.All.ToArray());
    }

    [Fact]
    public void Settings_CloudflareKeys_RoundTrip_WithDefaults()
    {
        var s = JsonSerializer.Deserialize<AppSettings>("{}")!;
        Assert.Equal("", s.CloudflareAccountId);
        Assert.Equal("@cf/openai/whisper-large-v3-turbo", s.CloudflareTranscribeModel);
        Assert.Equal("@cf/meta/llama-3.3-70b-instruct-fp8-fast", s.CloudflareCrModel);
        s.CloudflareAccountId = Acct;
        s.TranscriptionProvider = "cloudflare";
        var json = JsonSerializer.Serialize(s);
        Assert.Contains("\"cloudflare_account_id\":\"" + Acct + "\"", json);
        var back = JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.Equal(Acct, back.CloudflareAccountId);
        Assert.Equal("cloudflare", back.TranscriptionProvider);
    }

    // ── chunk splitting (AudioPrep.SplitWav, pure PCM slicing) ──────────────────

    private static string WriteWav(string path, int rate, int seconds)
    {
        using var fs = new FileStream(path, FileMode.Create);
        var bw = new BinaryWriter(fs);
        int dataLen = rate * 2 * seconds;
        bw.Write("RIFF"u8.ToArray()); bw.Write(36 + dataLen); bw.Write("WAVE"u8.ToArray());
        bw.Write("fmt "u8.ToArray()); bw.Write(16); bw.Write((short)1); bw.Write((short)1); bw.Write(rate);
        bw.Write(rate * 2); bw.Write((short)2); bw.Write((short)16);
        bw.Write("data"u8.ToArray()); bw.Write(dataLen);
        var data = new byte[dataLen];
        // sample value = second index, so each piece can be checked for its origin.
        for (int sec = 0; sec < seconds; sec++)
            for (int i = 0; i < rate; i++) { data[(sec * rate + i) * 2] = (byte)sec; }
        bw.Write(data);
        return path;
    }

    [Fact]
    public void SplitWav_ShortTrack_ReturnsOriginalUntouched()
    {
        var dir = Directory.CreateTempSubdirectory("echo-split-");
        try
        {
            var wav = WriteWav(Path.Combine(dir.FullName, "t.wav"), 1000, 30);
            var pieces = AudioPrep.SplitWav(wav, 600);
            var only = Assert.Single(pieces);
            Assert.Equal(wav, only.Path);
            Assert.Equal(0.0, only.StartSeconds);
            Assert.Single(Directory.GetFiles(dir.FullName));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void SplitWav_LongTrack_ConsecutivePieces_TailMergedIntoLast()
    {
        var dir = Directory.CreateTempSubdirectory("echo-split-");
        try
        {
            // 130 s at 1 kHz, 60 s pieces, min tail 20 s → 60 + 70 (a lone 10 s tail is absorbed).
            var wav = WriteWav(Path.Combine(dir.FullName, "mic_16k.wav"), 1000, 130);
            var pieces = AudioPrep.SplitWav(wav, 60, minTailSeconds: 20);
            Assert.Equal(2, pieces.Count);
            Assert.Equal(0.0, pieces[0].StartSeconds);
            Assert.Equal(60.0, pieces[1].StartSeconds);
            Assert.EndsWith("mic_16k_c0.wav", pieces[0].Path);
            Assert.EndsWith("mic_16k_c1.wav", pieces[1].Path);
            Assert.Equal(60.0, AudioPrep.WavDuration(pieces[0].Path), 3);
            Assert.Equal(70.0, AudioPrep.WavDuration(pieces[1].Path), 3);
            // Second piece starts with second #60's samples.
            var bytes = File.ReadAllBytes(pieces[1].Path);
            Assert.Equal(60, bytes[44]);

            // 180 s exactly → 3 equal pieces.
            var wav2 = WriteWav(Path.Combine(dir.FullName, "sys_16k.wav"), 1000, 180);
            var p2 = AudioPrep.SplitWav(wav2, 60, minTailSeconds: 20);
            Assert.Equal(new[] { 0.0, 60.0, 120.0 }, p2.Select(p => p.StartSeconds).ToArray());
        }
        finally { dir.Delete(true); }
    }
}
