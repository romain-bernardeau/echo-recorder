using System.Net;
using System.Text;
using System.Text.Json;
using Echo.Ai;

namespace Echo.Tests;

/// <summary>
/// Pins the 2026-09-17 Gemini fixes: a 47 min track made the flash model emit a JSON cut at
/// the output ceiling ("Expected end of string, but instead reached end of data"), which
/// failed the whole transcription as a non-retryable error. Now: 15 min pieces, an explicit
/// maxOutputTokens, and a truncated answer is repaired to its last complete segment.
/// </summary>
public class GeminiChunkTests
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
            var (status, body) = _script.Dequeue();
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static string Wrap(string modelText)
        => JsonSerializer.Serialize(new { candidates = new[] { new { content = new { parts = new[] { new { text = modelText } } } } } });

    [Fact]
    public void Gemini_IsChunked_At15Minutes()
    {
        var c = new GeminiClient(new HttpClient(new StubHandler()));
        Assert.Equal(900, ((IChunkedTranscribe)c).ChunkSeconds);
    }

    [Fact]
    public void Repair_CutsAtLastCompleteSegment()
    {
        var cut = """{"segments":[{"start":0.0,"end":1.5,"speaker_id":1,"text":"Bonjour"},{"start":1.5,"end":3.0,"speaker_id":2,"text":"Salut"},{"start":3.0,"end":4.2,"speaker_id":1,"text":"On comm""";
        var fixedJson = GeminiClient.RepairTruncatedSegmentsJson(cut);
        Assert.NotNull(fixedJson);
        var parsed = VoxtralResponseParser.Parse(fixedJson!);
        Assert.Equal(new[] { "Bonjour", "Salut" }, parsed.Segments.Select(s => s.Text).ToArray());
    }

    [Fact]
    public void Repair_ReturnsNull_WhenNothingComplete()
    {
        Assert.Null(GeminiClient.RepairTruncatedSegmentsJson("""{"segments":[{"start":0.0,"end":1.5,"text":"Bonj"""));
        Assert.Null(GeminiClient.RepairTruncatedSegmentsJson("pas du json"));
        Assert.Null(GeminiClient.RepairTruncatedSegmentsJson(""));
    }

    [Fact]
    public async Task Transcribe_TruncatedAnswer_IsRepaired_AndMaxOutputTokensSent()
    {
        var cut = """{"segments":[{"start":0.0,"end":1.5,"speaker_id":1,"text":"Bonjour"},{"start":1.5,"end":3.0,"speaker_id":2,"text":"Sal""";
        var h = new StubHandler((HttpStatusCode.OK, Wrap(cut)));
        var log = new List<string>();
        var r = await new GeminiClient(new HttpClient(h)).TranscribeAsync("k", "gemini-3.6-flash", new byte[] { 1, 2 }, "mic.mp3", null,
            new Progress<string>(log.Add), CancellationToken.None);
        var seg = Assert.Single(r.Segments);
        Assert.Equal("Bonjour", seg.Text);
        Assert.Equal("1", seg.VoxSpeaker);
        using var body = JsonDocument.Parse(h.Requests[0].Content);
        Assert.Equal(65536, body.RootElement.GetProperty("generationConfig").GetProperty("maxOutputTokens").GetInt32());
    }
}
