using System.Net;
using System.Text;
using System.Text.Json;
using Echo.Ai;

namespace Echo.Tests;

/// <summary>
/// Workers AI does not use ONE request format for all its Whisper models, and Echo used to
/// send base64 to every one of them. On 2026-09-18 a 50-minute meeting came back as
/// "HTTP 400 AiError: Failed to decode audio file" because the model picker had let
/// <c>@cf/openai/whisper</c> be selected: that model reads the audio as the raw request body
/// and answers word timings, where <c>whisper-large-v3-turbo</c> reads base64 and answers
/// segments. These tests pin both contracts and the rebuilding of segments from words/VTT.
/// </summary>
public class CloudflareModelShapeTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _script;
        public List<(string Url, string? ContentType, byte[] Bytes, string Text)> Requests { get; } = new();
        public StubHandler(params (HttpStatusCode, string)[] script) => _script = new(script);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var bytes = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(ct);
            Requests.Add((request.RequestUri!.ToString(),
                          request.Content?.Headers.ContentType?.MediaType,
                          bytes, Encoding.UTF8.GetString(bytes)));
            var (status, body) = _script.Dequeue();
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static readonly TimeSpan[] NoWait = { TimeSpan.Zero };
    private const string Acct = "0123456789abcdef0123456789abcdef";
    private static CloudflareClient Client(StubHandler h, string? lang = null)
        => new(() => Acct, new HttpClient(h), retryDelays: NoWait, language: lang is null ? null : () => lang);

    // Real @cf/openai/whisper answer: text + per-word timings + VTT, and NO segments.
    private const string WordsOk = """
        {"result":{"text":"Bonjour à tous. On commence ?","word_count":6,
          "words":[
            {"word":"Bonjour","start":0.2,"end":0.7},{"word":"à","start":0.7,"end":0.8},
            {"word":"tous.","start":0.8,"end":1.3},
            {"word":"On","start":5.0,"end":5.2},{"word":"commence","start":5.2,"end":5.8},
            {"word":"?","start":5.8,"end":5.9}],
          "vtt":"WEBVTT\n\n00:00:00.200 --> 00:00:01.300\nBonjour à tous.\n"},
         "success":true,"errors":[],"messages":[]}
        """;

    [Theory]
    [InlineData("@cf/openai/whisper", CloudflareModels.AudioShape.Binary)]
    [InlineData("@cf/openai/whisper-tiny-en", CloudflareModels.AudioShape.Binary)]
    [InlineData("@cf/openai/whisper-large-v3-turbo", CloudflareModels.AudioShape.Base64Json)]
    [InlineData("@cf/deepgram/nova-3", CloudflareModels.AudioShape.Unknown)]
    public void ShapeOf_KnowsWhichModelsReadBase64(string model, CloudflareModels.AudioShape expected)
        => Assert.Equal(expected, CloudflareModels.ShapeOf(model));

    [Fact]
    public async Task BinaryModel_GetsTheRawFile_NotBase64()
    {
        var h = new StubHandler((HttpStatusCode.OK, WordsOk));
        var audio = new byte[] { 0xFF, 0xFB, 0x10, 0x04, 0x00 };

        var r = await Client(h).TranscribeAsync("tok", "@cf/openai/whisper", audio, "system.mp3",
                                                new[] { "Contoso" }, null, CancellationToken.None);

        var req = Assert.Single(h.Requests);
        Assert.Equal(audio, req.Bytes);                       // the file itself, byte for byte
        Assert.Equal("audio/mpeg", req.ContentType);
        Assert.DoesNotContain("audio", req.Text);             // no JSON envelope at all
        Assert.NotEmpty(r.Segments);                          // and the answer is still usable
    }

    [Fact]
    public async Task Base64Model_KeepsTheJsonPayload_AndPassesTheLanguageHint()
    {
        var h = new StubHandler((HttpStatusCode.OK, """{"result":{"text":"ok","segments":[{"start":0,"end":1,"text":"ok"}]},"success":true}"""));
        var audio = new byte[] { 1, 2, 3 };

        await Client(h, lang: "fr").TranscribeAsync("tok", CloudflareModels.Turbo, audio, "mic.mp3",
                                                    null, null, CancellationToken.None);

        var req = Assert.Single(h.Requests);
        Assert.Equal("application/json", req.ContentType);
        using var body = JsonDocument.Parse(req.Text);
        Assert.Equal(Convert.ToBase64String(audio), body.RootElement.GetProperty("audio").GetString());
        Assert.Equal("fr", body.RootElement.GetProperty("language").GetString());
        // Whisper's own guards against silence hallucination loops.
        Assert.True(body.RootElement.GetProperty("vad_filter").GetBoolean());
        Assert.False(body.RootElement.GetProperty("condition_on_previous_text").GetBoolean());
    }

    [Fact]
    public void Parse_RebuildsSegmentsFromWords_WhenTheModelHasNoSegments()
    {
        var r = WhisperResponseParser.Parse(WordsOk);

        // Two turns: the sentence-ending "tous." closes the first, and the 3.7 s silence
        // before "On" would have closed it anyway.
        Assert.Equal(2, r.Segments.Count);
        Assert.Equal("Bonjour à tous.", r.Segments[0].Text);
        Assert.Equal(0.2, r.Segments[0].Start!.Value, 3);
        Assert.Equal(1.3, r.Segments[0].End!.Value, 3);
        Assert.Equal("On commence ?", r.Segments[1].Text);
        Assert.Equal(5.0, r.Segments[1].Start!.Value, 3);
        Assert.All(r.Segments, s => Assert.Null(s.VoxSpeaker));// Whisper does not diarise
    }

    [Fact]
    public void Parse_FallsBackToVtt_WhenOnlyCuesAreReturned()
    {
        const string vttOnly = """
            {"result":{"text":"Bonjour. Deuxième phrase.",
              "vtt":"WEBVTT\n\n1\n00:00:01.500 --> 00:00:03.000\nBonjour.\n\n2\n00:01:04.250 --> 00:01:06.000\nDeuxième phrase.\n"},
             "success":true}
            """;
        var r = WhisperResponseParser.Parse(vttOnly);

        Assert.Equal(2, r.Segments.Count);
        Assert.Equal("Bonjour.", r.Segments[0].Text);
        Assert.Equal(1.5, r.Segments[0].Start!.Value, 3);
        Assert.Equal(64.25, r.Segments[1].Start!.Value, 3);     // minutes carried over correctly
    }

    [Fact]
    public void WordSegments_AreCutOnLongSilencesAndDoNotRunForever()
    {
        // 40 s of uninterrupted speech with no punctuation: must still be cut into turns.
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var words = string.Join(",", Enumerable.Range(0, 80).Select(i =>
            $"{{\"word\":\"mot{i}\",\"start\":{(i * 0.5).ToString("0.0", inv)},\"end\":{(i * 0.5 + 0.4).ToString("0.0", inv)}}}"));
        var r = WhisperResponseParser.Parse($"{{\"result\":{{\"text\":\"x\",\"words\":[{words}]}},\"success\":true}}");

        Assert.True(r.Segments.Count >= 2, "a 40 s monologue must not come back as one block");
        Assert.All(r.Segments, s => Assert.True(
            s.End!.Value - s.Start!.Value <= WhisperResponseParser.MaxWordSegmentSeconds + 1,
            "no rebuilt turn should exceed the cap"));
    }

    [Fact]
    public void SilentWav_IsAValidRiffFile()
    {
        var wav = CloudflareClient.SilentWav(0.5);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal(44 + 16000, wav.Length);                   // 0.5 s of 16 kHz mono 16-bit
    }

    [Fact]
    public async Task ProbeTranscribeModel_ReportsAnIncompatibleModelInsteadOfFailingLater()
    {
        var h = new StubHandler((HttpStatusCode.BadRequest,
            """{"success":false,"errors":[{"code":8007,"message":"AiError: Failed to decode audio file."}]}"""));

        var (ok, msg, ts) = await Client(h).ProbeTranscribeModelAsync("tok", "@cf/some/other-model", CancellationToken.None);

        Assert.False(ok);
        Assert.False(ts);
        Assert.Contains("400", msg);
    }

    [Fact]
    public async Task ProbeTranscribeModel_ConfirmsSegmentTimestamps()
    {
        var h = new StubHandler((HttpStatusCode.OK, """{"result":{"text":"","segments":[]},"success":true}"""));

        var (ok, msg, ts) = await Client(h).ProbeTranscribeModelAsync("tok", CloudflareModels.Turbo, CancellationToken.None);

        Assert.True(ok);
        Assert.True(ts);
        Assert.Contains("segment", msg);
    }
}
