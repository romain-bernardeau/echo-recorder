using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Echo.Ai;

/// <summary>
/// Raised when the Cloudflare Workers AI REST API rejects a request. Same shape as the
/// Mistral / Gemini exceptions (all derive from <see cref="LlmApiException"/>) so the
/// orchestrator handles the three providers uniformly.
/// </summary>
public sealed class CloudflareApiException : LlmApiException
{
    public CloudflareApiException(int statusCode, string? serverMessage, int attempts)
        : base($"Cloudflare Workers AI HTTP {statusCode}{(string.IsNullOrWhiteSpace(serverMessage) ? "" : ": " + serverMessage)} (after {attempts} attempt(s))")
    {
        StatusCode = statusCode;
        ServerMessage = serverMessage;
        Attempts = attempts;
    }

    public override int StatusCode { get; }
    public override string? ServerMessage { get; }
    public override int Attempts { get; }

    /// <summary>Workers AI reports an oversized prompt either as an HTTP 400/413 or as a 2xx
    /// envelope with success=false (mapped to 400 by the client), e.g. code 5021 "The estimated
    /// number of input and maximum output tokens (N) exceeded this model context window limit".</summary>
    public override bool IsContextOverflow
        => ContextOverflowFromStatus()
           || StatusCode is 400 or 413 && (ServerMessage ?? "").Contains("code 5021", StringComparison.OrdinalIgnoreCase);

    public override string UserMessage => StatusCode switch
    {
        _ when IsContextOverflow => "transcription trop longue pour le modèle Cloudflare (fenêtre de contexte dépassée). "
               + "Relancez : Echo découpe la réunion ; si cela persiste, choisissez un autre fournisseur dans Paramètres > Fournisseurs",
        429 => $"limite de l'API Cloudflare atteinte (429) malgré {Attempts} tentatives. "
               + "Attendez une minute puis relancez ; vérifiez le quota Workers AI sur dash.cloudflare.com",
        401 or 403 => $"jeton API Cloudflare refusé (HTTP {StatusCode}). Vérifiez le jeton et l'identifiant de compte dans Paramètres",
        404 => "compte ou modèle Cloudflare introuvable (HTTP 404). Vérifiez l'identifiant de compte et le nom du modèle dans Paramètres",
        400 => $"requête refusée par Cloudflare (HTTP 400){(string.IsNullOrWhiteSpace(ServerMessage) ? "" : " : " + ServerMessage)}",
        0 => $"API Cloudflare injoignable ({ServerMessage ?? "erreur réseau"}) après {Attempts} tentatives. Vérifiez la connexion Internet",
        >= 500 => $"service Cloudflare indisponible (HTTP {StatusCode}) malgré {Attempts} tentatives. Réessayez un peu plus tard",
        _ => $"requête refusée par Cloudflare (HTTP {StatusCode}){(string.IsNullOrWhiteSpace(ServerMessage) ? "" : " : " + ServerMessage)}",
    };
}

/// <summary>
/// How a Workers AI speech model wants its audio and what it answers. Workers AI does NOT
/// share one contract across its Whisper models, and picking the wrong one only shows up as
/// an HTTP 400 at the END of a meeting (2026-09-18: a recording was lost to
/// "Failed to decode audio file" because the model picker allowed <c>@cf/openai/whisper</c>,
/// which does not read base64):
///  - <see cref="AudioShape.Base64Json"/> — <c>{audio:"&lt;base64&gt;", task, language, …}</c>,
///    answers <c>segments[{start,end,text}]</c>. This is whisper-large-v3-turbo: timestamps,
///    context prompt, VAD. The one Echo is built around.
///  - <see cref="AudioShape.Binary"/> — the audio file as the raw request body, answers
///    <c>{text, words[{word,start,end}], vtt}</c> with no segments and no prompt parameter.
///    This is the classic @cf/openai/whisper and whisper-tiny-en.
/// Anything unknown is attempted as Base64Json (the modern shape) and reported as unverified.
/// </summary>
public static class CloudflareModels
{
    public enum AudioShape { Base64Json, Binary, Unknown }

    public const string Turbo = "@cf/openai/whisper-large-v3-turbo";

    private static readonly HashSet<string> BinaryModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "@cf/openai/whisper", "@cf/openai/whisper-tiny-en",
    };

    private static readonly HashSet<string> Base64Models = new(StringComparer.OrdinalIgnoreCase)
    {
        Turbo,
    };

    public static AudioShape ShapeOf(string? model)
    {
        var m = (model ?? "").Trim();
        if (BinaryModels.Contains(m)) return AudioShape.Binary;
        if (Base64Models.Contains(m)) return AudioShape.Base64Json;
        return AudioShape.Unknown;
    }

    /// <summary>True when the model returns real segment timestamps. The binary-shape models
    /// only give word timings, from which Echo rebuilds segments — usable, but coarser.</summary>
    public static bool HasNativeSegments(string? model) => ShapeOf(model) == AudioShape.Base64Json;

    /// <summary>Short French verdict shown next to the model picker, so an incompatible choice
    /// is caught at click time instead of after a 50-minute meeting.</summary>
    public static string Verdict(string? model) => ShapeOf(model) switch
    {
        AudioShape.Base64Json => "horodatage par segment, vocabulaire métier transmis au modèle",
        AudioShape.Binary => "pas d'horodatage natif : Echo le reconstruit mot à mot, et le vocabulaire métier n'est pas transmis",
        _ => "modèle non reconnu : Echo l'appellera comme whisper-large-v3-turbo, sans garantie",
    };
}

/// <summary>
/// Parser for the Workers AI Whisper response
/// (<c>{ result: { text, segments:[{start,end,text,no_speech_prob,...}], transcription_info:{language} } }</c>).
/// Mapped onto <see cref="VoxtralResult"/> with no speaker id (Whisper does not diarise), so
/// the downstream attribution falls back to the track origin. Segments that Whisper itself
/// flags as probable non-speech, and the well-known subtitle-credit hallucinations, are dropped.
/// </summary>
public static class WhisperResponseParser
{
    /// <summary>Above this no_speech_prob a segment is treated as silence hallucination.</summary>
    public const double NoSpeechDrop = 0.85;

    private static readonly string[] JunkMarkers =
    {
        "sous-titres réalisés", "sous-titrage société radio-canada", "amara.org",
        "subtitles by the amara", "thank you for watching", "merci d'avoir regardé",
    };

    public static VoxtralResult Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        // The REST envelope is { result: {...}, success, errors, messages }; a bare body is accepted too.
        var res = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Object ? r : root;

        string? text = Str(res, "text");
        string? language = null;
        if (res.TryGetProperty("transcription_info", out var info) && info.ValueKind == JsonValueKind.Object)
            language = Str(info, "language");

        var segments = new List<VoxSegment>();
        if (res.TryGetProperty("segments", out var segs) && segs.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in segs.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object) continue;
                var t = (Str(s, "text") ?? "").Trim();
                if (t.Length == 0) continue;
                if (Num(s, "no_speech_prob") is { } nsp && nsp > NoSpeechDrop) continue;
                if (IsJunk(t)) continue;
                segments.Add(new VoxSegment(Num(s, "start"), Num(s, "end"), t, null));
            }
        }
        else if (res.TryGetProperty("words", out var words) && words.ValueKind == JsonValueKind.Array && words.GetArrayLength() > 0)
        {
            // Binary-shape models (@cf/openai/whisper) answer word timings, no segments.
            // Rebuild speech turns from the words so the rest of the pipeline (alignment
            // with the other track, echo de-duplication, "listen to this passage") works.
            segments.AddRange(SegmentsFromWords(words));
        }
        else if (res.TryGetProperty("vtt", out var vtt) && vtt.ValueKind == JsonValueKind.String)
        {
            segments.AddRange(SegmentsFromVtt(vtt.GetString() ?? ""));
        }
        else if (!string.IsNullOrWhiteSpace(text))
        {
            segments.Add(new VoxSegment(null, null, text!.Trim(), null));
        }
        return new VoxtralResult(text, language, segments, json);
    }

    /// <summary>A rebuilt turn is closed on sentence-ending punctuation, on a silence longer
    /// than <see cref="WordGapSeconds"/>, or once it reaches <see cref="MaxWordSegmentSeconds"/>.</summary>
    public const double WordGapSeconds = 0.8;
    public const double MaxWordSegmentSeconds = 15.0;

    internal static List<VoxSegment> SegmentsFromWords(JsonElement words)
    {
        var made = new List<VoxSegment>();
        var buf = new StringBuilder();
        double? start = null, last = null;

        void Flush(double? end)
        {
            var t = buf.ToString().Trim();
            buf.Clear();
            if (t.Length == 0 || IsJunk(t)) { start = null; return; }
            made.Add(new VoxSegment(start, end, t, null));
            start = null;
        }

        foreach (var w in words.EnumerateArray())
        {
            if (w.ValueKind != JsonValueKind.Object) continue;
            var word = (Str(w, "word") ?? "").Trim();
            if (word.Length == 0) continue;
            var ws = Num(w, "start");
            var we = Num(w, "end");

            // A long silence before this word, or a turn already long enough, closes the previous one.
            if (start is not null && ws is { } s0
                && ((last is { } l0 && s0 - l0 > WordGapSeconds) || s0 - start.Value >= MaxWordSegmentSeconds))
                Flush(last);

            start ??= ws;
            if (buf.Length > 0) buf.Append(' ');
            buf.Append(word);
            last = we ?? ws ?? last;

            if (word.EndsWith('.') || word.EndsWith('!') || word.EndsWith('?')) Flush(last);
        }
        Flush(last);
        return made;
    }

    /// <summary>WebVTT fallback: cues "00:00:01.000 --> 00:00:04.500" + text lines.</summary>
    internal static List<VoxSegment> SegmentsFromVtt(string vtt)
    {
        var made = new List<VoxSegment>();
        double? s = null, e = null;
        var buf = new StringBuilder();

        void Flush()
        {
            var t = buf.ToString().Trim();
            buf.Clear();
            if (t.Length > 0 && !IsJunk(t)) made.Add(new VoxSegment(s, e, t, null));
            s = e = null;
        }

        foreach (var raw in vtt.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) { if (buf.Length > 0) Flush(); continue; }
            if (line.Equals("WEBVTT", StringComparison.OrdinalIgnoreCase)) continue;
            var arrow = line.IndexOf("-->", StringComparison.Ordinal);
            if (arrow > 0)
            {
                if (buf.Length > 0) Flush();
                s = VttTime(line[..arrow]);
                e = VttTime(line[(arrow + 3)..]);
                continue;
            }
            if (s is null && e is null && buf.Length == 0) continue;   // cue identifier line
            if (buf.Length > 0) buf.Append(' ');
            buf.Append(line);
        }
        Flush();
        return made;
    }

    /// <summary>"[hh:]mm:ss.mmm" (trailing cue settings ignored) → seconds.</summary>
    internal static double? VttTime(string s)
    {
        s = s.Trim();
        var sp = s.IndexOf(' ');
        if (sp > 0) s = s[..sp];
        var parts = s.Split(':');
        if (parts.Length is < 2 or > 3) return null;
        double total = 0;
        foreach (var p in parts)
        {
            if (!double.TryParse(p.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out var v)) return null;
            total = total * 60 + v;
        }
        return total;
    }

    public static bool IsJunk(string text)
    {
        var low = text.ToLowerInvariant();
        foreach (var m in JunkMarkers) if (low.Contains(m)) return true;
        return false;
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
}

/// <summary>
/// Thin REST wrapper over Cloudflare Workers AI
/// (<c>https://api.cloudflare.com/client/v4/accounts/{account}/ai/...</c>).
///  - transcription: <c>POST /ai/run/@cf/openai/whisper-large-v3-turbo</c>, JSON body with the
///    base64 audio (+ initial_prompt built from the vocabulary). Implements
///    <see cref="IChunkedTranscribe"/>: the Transcriber sends ~10 min pieces.
///  - chat: OpenAI-compatible <c>POST /ai/v1/chat/completions</c>.
///  - catalogue: <c>GET /ai/models/search</c>.
/// The account id is not a secret: it is read from settings through <paramref name="accountId"/>
/// at call time, so a change in the Settings page applies immediately.
/// Same retry-on-transient ladder as the other clients.
/// </summary>
public sealed class CloudflareClient : ITranscribeClient, IChatClient, IModelCatalog, IChunkedTranscribe
{
    public const string DefaultBaseUrl = "https://api.cloudflare.com/client/v4";

    /// <summary>Whisper pieces: 10 min of 16 kHz mono MP3 at 64 kb/s is about 5 MB of audio
    /// (7 MB once base64-encoded), far below the request ceiling, and short enough to keep
    /// Whisper's timestamps accurate.</summary>
    public const int DefaultChunkSeconds = 600;

    /// <summary>Whisper's initial_prompt is a short context, not a lexicon: keep the head of the bias list.</summary>
    public const int MaxPromptTerms = 40;

    /// <summary>Workers AI caps a chat answer at 256 tokens unless max_tokens is sent (seen live
    /// 2026-09-17: the minutes JSON came back cut after 256 tokens, "unreadable JSON"). A CR is
    /// 3 to 5 k tokens; 8 k leaves room for long meetings within the model's context.</summary>
    public const int ChatMaxTokens = 8192;

    private static readonly TimeSpan[] DefaultRetryDelays =
    {
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60),
    };
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(120);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly Func<string?> _accountId;
    /// <summary>ISO language hinted to Whisper ("fr", "en"…) read at call time; null lets the
    /// model detect it. Fed from the app's CR language so a French meeting is not decoded as
    /// English on a noisy passage.</summary>
    private readonly Func<string?>? _language;
    private readonly Action<string>? _retryLog;
    private readonly IReadOnlyList<TimeSpan> _retryDelays;

    public int ChunkSeconds { get; init; } = DefaultChunkSeconds;

    public CloudflareClient(Func<string?> accountId, HttpClient? http = null, string? baseUrl = null,
        Action<string>? retryLog = null, IReadOnlyList<TimeSpan>? retryDelays = null,
        Func<string?>? language = null)
    {
        _accountId = accountId;
        _language = language;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
        _retryLog = retryLog;
        _retryDelays = retryDelays ?? DefaultRetryDelays;
    }

    /// <summary>Base of every account-scoped call; throws a 404-shaped exception when the id is blank
    /// so the user gets "vérifiez l'identifiant de compte" instead of a raw URL error.</summary>
    private string AccountBase()
    {
        var id = (_accountId() ?? "").Trim();
        if (id.Length == 0) throw new CloudflareApiException(404, "identifiant de compte Cloudflare absent", 1);
        return $"{_baseUrl}/accounts/{Uri.EscapeDataString(id)}/ai";
    }

    // ── transcription (Whisper) ──────────────────────────────────────────────────

    public async Task<VoxtralResult> TranscribeAsync(
        string apiKey, string model, byte[] audioBytes, string fileName,
        IReadOnlyList<string>? contextBias, IProgress<string>? log, CancellationToken ct)
    {
        var url = $"{AccountBase()}/run/{model.TrimStart('/')}";
        var binary = CloudflareModels.ShapeOf(model) == CloudflareModels.AudioShape.Binary;
        if (binary)
            log?.Report($"[Cloudflare] {model} n'accepte pas le base64 : envoi du fichier brut "
                        + "(horodatage reconstruit mot à mot, vocabulaire métier non transmis).");
        var body = await SendWithRetryAsync(
            () => binary
                ? BinaryPost(url, apiKey, audioBytes, fileName)
                : JsonPost(url, apiKey, BuildWhisperPayload(audioBytes, contextBias, _language?.Invoke())),
            $"transcription {fileName}", ct);
        return WhisperResponseParser.Parse(body);
    }


    /// <summary>Whisper request body for the base64 models (whisper-large-v3-turbo): audio,
    /// transcribe task, optional language hint, optional initial_prompt made of the vocabulary
    /// terms. <c>condition_on_previous_text=false</c> is Whisper's own guard against the
    /// repetition loops it falls into on long silences.</summary>
    public static Dictionary<string, object?> BuildWhisperPayload(
        byte[] audioBytes, IReadOnlyList<string>? contextBias, string? language = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["audio"] = Convert.ToBase64String(audioBytes),
            ["task"] = "transcribe",
            ["vad_filter"] = true,
            ["condition_on_previous_text"] = false,
        };
        // "auto" (the setting's default) means: impose nothing, Whisper detects the language.
        var lang = (language ?? "").Trim().ToLowerInvariant();
        if (lang.Length > 0 && lang != "auto") payload["language"] = lang;
        var prompt = BuildInitialPrompt(contextBias);
        if (prompt is not null) payload["initial_prompt"] = prompt;
        return payload;
    }

    /// <summary>Request for the binary-shape models (@cf/openai/whisper, whisper-tiny-en):
    /// the audio file IS the body. Sending them base64 returns
    /// "Failed to decode audio file" (HTTP 400).</summary>
    private static HttpRequestMessage BinaryPost(string url, string apiKey, byte[] audioBytes, string fileName)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(audioBytes) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue(ContentTypeFor(fileName));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return req;
    }

    /// <summary>
    /// Send half a second of silence to <paramref name="model"/>, exactly the way a real
    /// recording would be sent, and report what came back. This is the guard rail the app was
    /// missing: an incompatible transcription model used to be discovered by an HTTP 400 at the
    /// end of a meeting, with the recording left untranscribed. Costs a fraction of a neuron.
    /// </summary>
    public async Task<(bool Ok, string Message, bool Timestamps)> ProbeTranscribeModelAsync(
        string apiKey, string model, CancellationToken ct)
    {
        var url = $"{AccountBase()}/run/{model.TrimStart('/')}";
        var audio = SilentWav(seconds: 0.5);
        var binary = CloudflareModels.ShapeOf(model) == CloudflareModels.AudioShape.Binary;
        string body;
        try
        {
            body = await SendWithRetryAsync(
                () => binary
                    ? BinaryPost(url, apiKey, audio, "probe.wav")
                    : JsonPost(url, apiKey, BuildWhisperPayload(audio, null)),
                $"probe {model}", ct);
        }
        catch (CloudflareApiException ex)
        {
            return (false, ex.UserMessage, false);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var res = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Object ? r : root;
            bool hasSegments = res.TryGetProperty("segments", out var sg) && sg.ValueKind == JsonValueKind.Array;
            bool hasWords = res.TryGetProperty("words", out var w) && w.ValueKind == JsonValueKind.Array;
            bool hasVtt = res.TryGetProperty("vtt", out var v) && v.ValueKind == JsonValueKind.String;
            if (hasSegments) return (true, "compatible : horodatage par segment", true);
            if (hasWords || hasVtt) return (true, "compatible : horodatage reconstruit mot à mot", true);
            if (res.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                return (true, "accepté, mais sans horodatage : la transcription arrivera en un seul bloc", false);
            return (false, "réponse inattendue de ce modèle (ni segments ni texte)", false);
        }
        catch { return (false, "réponse illisible de ce modèle", false); }
    }

    /// <summary>A valid 16 kHz mono PCM WAV of pure silence — enough for a model to accept or
    /// reject the request format, which is all the probe needs to know.</summary>
    internal static byte[] SilentWav(double seconds)
    {
        const int rate = 16000;
        int samples = Math.Max(1, (int)(rate * seconds));
        int data = samples * 2;
        var buf = new byte[44 + data];
        void Ascii(int at, string s) { for (int i = 0; i < s.Length; i++) buf[at + i] = (byte)s[i]; }
        void I32(int at, int v) => BitConverter.GetBytes(v).CopyTo(buf, at);
        void I16(int at, short v) => BitConverter.GetBytes(v).CopyTo(buf, at);
        Ascii(0, "RIFF"); I32(4, 36 + data); Ascii(8, "WAVE");
        Ascii(12, "fmt "); I32(16, 16); I16(20, 1); I16(22, 1);
        I32(24, rate); I32(28, rate * 2); I16(32, 2); I16(34, 16);
        Ascii(36, "data"); I32(40, data);
        return buf;                                   // samples left at zero = silence
    }

    internal static string ContentTypeFor(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".m4a" or ".mp4" => "audio/mp4",
            ".ogg" => "audio/ogg",
            ".flac" => "audio/flac",
            _ => "application/octet-stream",
        };

    public static string? BuildInitialPrompt(IReadOnlyList<string>? contextBias)
    {
        if (contextBias is not { Count: > 0 }) return null;
        // The shared bias is Voxtral-shaped ("Claire_MOREAU"); Whisper reads its prompt as text.
        var terms = contextBias.Select(Vocabulary.ToPromptTerm).Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxPromptTerms).ToList();
        if (terms.Count == 0) return null;
        return "Vocabulaire : " + string.Join(", ", terms) + ".";
    }

    // ── chat (OpenAI-compatible) ─────────────────────────────────────────────────

    public async Task<string> ChatCompleteAsync(
        string apiKey, string model, IReadOnlyList<ChatMessage> messages,
        double temperature, bool jsonObject, CancellationToken ct)
    {
        if (!jsonObject)
            return await SendChat(apiKey, model, messages, temperature, jsonObject: false, ct);
        try
        {
            return await SendChat(apiKey, model, messages, temperature, jsonObject: true, ct);
        }
        catch (CloudflareApiException e) when (e.StatusCode is 400 or 422 && !e.IsContextOverflow)
        {
            // Not every Workers AI model accepts response_format: retry plain (same as Mistral).
            return await SendChat(apiKey, model, messages, temperature, jsonObject: false, ct);
        }
    }

    private async Task<string> SendChat(string apiKey, string model, IReadOnlyList<ChatMessage> messages,
        double temperature, bool jsonObject, CancellationToken ct)
    {
        var url = $"{AccountBase()}/v1/chat/completions";
        var body = await SendWithRetryAsync(
            () => JsonPost(url, apiKey, BuildChatPayload(model, messages, temperature, jsonObject)),
            $"chat {model}", ct);
        CheckChatAnswer(body, model);
        return ExtractChatContent(body);
    }

    /// <summary>Refuse a cut answer (finish_reason "length") and a body of unknown shape (no
    /// choices, no result.response): both used to come back as "" and be written as an empty
    /// summary.</summary>
    internal static void CheckChatAnswer(string body, string model)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var res = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Object ? r : root;
            JsonElement choices = default;
            bool hasChoices = (root.TryGetProperty("choices", out choices) || res.TryGetProperty("choices", out choices))
                              && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0;
            if (hasChoices)
            {
                if (choices[0].TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String
                    && string.Equals(fr.GetString(), "length", StringComparison.OrdinalIgnoreCase))
                    throw new LlmEmptyResponseException(LlmProvider.Cloudflare, model, truncated: true, "length");
                return;
            }
            if (res.TryGetProperty("response", out var resp) && resp.ValueKind == JsonValueKind.String) return;
        }
        catch (JsonException) { /* fall through: unreadable */ }
        throw new LlmEmptyResponseException(LlmProvider.Cloudflare, model, truncated: false, "format de réponse inconnu");
    }

    public static Dictionary<string, object?> BuildChatPayload(string model, IReadOnlyList<ChatMessage> messages, double temperature, bool jsonObject)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["temperature"] = temperature,
            ["max_tokens"] = MaxTokensFor(model, messages),
            ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
        };
        if (jsonObject) payload["response_format"] = new { type = "json_object" };
        return payload;
    }

    /// <summary>Workers AI counts max_tokens INSIDE the context window ("estimated number of
    /// input and maximum output tokens exceeded"): asking 8 k of answer on top of a 20 k prompt
    /// gets the request refused. Shrink the ceiling to what is left, never below 1 k.</summary>
    internal static int MaxTokensFor(string model, IReadOnlyList<ChatMessage> messages)
    {
        int window = ContextBudget.ContextTokens(LlmProvider.Cloudflare, model);
        int left = window - ContextBudget.EstimateTokens(messages) - 256;
        return Math.Clamp(left, 1_024, ChatMaxTokens);
    }

    /// <summary>choices[0].message.content (OpenAI shape); tolerates the native
    /// <c>{result:{response}}</c> shape too.</summary>
    public static string ExtractChatContent(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.TryGetProperty("choices", out var ch) && ch.ValueKind == JsonValueKind.Array && ch.GetArrayLength() > 0)
        {
            var msg = ch[0];
            if (msg.TryGetProperty("message", out var m) && m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                return c.GetString() ?? "";
        }
        if (root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Object)
        {
            if (r.TryGetProperty("response", out var resp) && resp.ValueKind == JsonValueKind.String) return resp.GetString() ?? "";
            if (r.TryGetProperty("choices", out var rch) && rch.ValueKind == JsonValueKind.Array && rch.GetArrayLength() > 0
                && rch[0].TryGetProperty("message", out var rm) && rm.TryGetProperty("content", out var rc) && rc.ValueKind == JsonValueKind.String)
                return rc.GetString() ?? "";
        }
        return "";
    }

    // ── catalogue + probe (IModelCatalog) ───────────────────────────────────────

    /// <summary>GET /ai/models/search, paged (per_page 100), single attempt per page. Throws a
    /// <see cref="CloudflareApiException"/> on HTTP error / network failure.</summary>
    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(string apiKey, CancellationToken ct)
    {
        var all = new Dictionary<string, ModelInfo>(StringComparer.OrdinalIgnoreCase);
        var baseUrl = AccountBase();
        for (int page = 1; page <= 10; page++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/models/search?per_page=100&page={page}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            HttpResponseMessage resp;
            try { resp = await _http.SendAsync(req, ct); }
            catch (HttpRequestException net) { throw new CloudflareApiException(0, net.Message, 1); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new CloudflareApiException(0, "délai dépassé (timeout)", 1); }
            string body;
            using (resp)
            {
                body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode || !EnvelopeOk(body))
                    throw new CloudflareApiException(resp.IsSuccessStatusCode ? 400 : (int)resp.StatusCode, ExtractServerMessage(body), 1);
            }
            var pageModels = ParseModelPage(body);
            foreach (var m in pageModels) all.TryAdd(m.Id, m);
            if (pageModels.Count < 100) break;
        }
        return all.Values.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>result[].name with task.name: "Automatic Speech Recognition" → Transcribe,
    /// "Text Generation" → Chat. Other tasks (embeddings, image, translation…) are skipped.</summary>
    public static IReadOnlyList<ModelInfo> ParseModelPage(string json)
    {
        var list = new List<ModelInfo>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("result", out var res) || res.ValueKind != JsonValueKind.Array) return list;
        foreach (var m in res.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object) continue;
            var name = m.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? (n.GetString() ?? "").Trim() : "";
            if (name.Length == 0) continue;
            string task = "";
            if (m.TryGetProperty("task", out var t) && t.ValueKind == JsonValueKind.Object
                && t.TryGetProperty("name", out var tn) && tn.ValueKind == JsonValueKind.String)
                task = tn.GetString() ?? "";
            bool asr = task.Contains("Speech Recognition", StringComparison.OrdinalIgnoreCase)
                       || name.Contains("whisper", StringComparison.OrdinalIgnoreCase);
            bool chat = task.Contains("Text Generation", StringComparison.OrdinalIgnoreCase);
            if (!asr && !chat) continue;
            list.Add(new ModelInfo(name, Chat: chat && !asr, Transcribe: asr));
        }
        return list;
    }

    /// <summary>One minimal chat completion (max_tokens 1), ONE attempt, no retry; returns the status.</summary>
    public async Task<ModelProbeResult> ProbeChatModelAsync(string apiKey, string model, CancellationToken ct)
    {
        try
        {
            var payload = new
            {
                model,
                messages = new[] { new { role = "user", content = "ping" } },
                max_tokens = 1,
            };
            using var req = JsonPost($"{AccountBase()}/v1/chat/completions", apiKey, payload);
            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            bool ok = resp.IsSuccessStatusCode && EnvelopeOk(body);
            return new ModelProbeResult(ok, ok ? (int)resp.StatusCode : (resp.IsSuccessStatusCode ? 400 : (int)resp.StatusCode),
                ok ? null : ExtractServerMessage(body));
        }
        catch (CloudflareApiException e) { return new ModelProbeResult(false, e.StatusCode, e.ServerMessage); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new ModelProbeResult(false, 0, ex is TaskCanceledException ? "délai dépassé (timeout)" : ex.Message);
        }
    }

    /// <summary>GET /accounts/{id}/ai/models/search?per_page=1: the cheapest authenticated,
    /// account-scoped call. Validates the token AND the account id together. Never throws.</summary>
    public async Task<ModelProbeResult> ProbeAccountAsync(string apiKey, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{AccountBase()}/models/search?per_page=1");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            bool ok = resp.IsSuccessStatusCode && EnvelopeOk(body);
            return new ModelProbeResult(ok, ok ? 200 : (resp.IsSuccessStatusCode ? 400 : (int)resp.StatusCode), ok ? null : ExtractServerMessage(body));
        }
        catch (CloudflareApiException e) { return new ModelProbeResult(false, e.StatusCode, e.ServerMessage); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new ModelProbeResult(false, 0, ex is TaskCanceledException ? "délai dépassé (timeout)" : ex.Message);
        }
    }

    // ── plumbing ─────────────────────────────────────────────────────────────────

    private static HttpRequestMessage JsonPost(string url, string apiKey, object payload)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return req;
    }

    /// <summary>The v4 envelope carries <c>success</c>; a 2xx with success=false is still a failure.</summary>
    internal static bool EnvelopeOk(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.False) return false;
            return true;
        }
        catch { return false; }
    }

    /// <summary>errors[0].message (v4 envelope) → error.message (OpenAI shape) → raw, truncated.</summary>
    internal static string? ExtractServerMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Truncate(body);
            if (root.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0)
            {
                var e0 = errs[0];
                if (e0.ValueKind == JsonValueKind.Object && e0.TryGetProperty("message", out var em) && em.ValueKind == JsonValueKind.String)
                {
                    var code = e0.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number ? $" (code {c.GetRawText()})" : "";
                    return Truncate(em.GetString() + code);
                }
                if (e0.ValueKind == JsonValueKind.String) return Truncate(e0.GetString());
            }
            if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object
                && e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                return Truncate(m.GetString());
            if (root.TryGetProperty("message", out var mm) && mm.ValueKind == JsonValueKind.String) return Truncate(mm.GetString());
            return Truncate(body);
        }
        catch { return Truncate(body); }
    }

    private static string? Truncate(string? s)
    {
        s = s?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        return s.Length <= 300 ? s : s[..300] + "…";
    }

    private async Task<string> SendWithRetryAsync(Func<HttpRequestMessage> makeRequest, string opLabel, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            TimeSpan delay;
            string why;
            try
            {
                using var req = makeRequest();
                using var resp = await _http.SendAsync(req, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (resp.IsSuccessStatusCode && EnvelopeOk(body)) return body;

                // A 2xx carrying success=false is a rejected request (bad input / model), not transient.
                int status = resp.IsSuccessStatusCode ? 400 : (int)resp.StatusCode;
                var serverMsg = ExtractServerMessage(body);
                if (!LlmApiException.IsRetryableStatus(status) || attempt >= _retryDelays.Count)
                    throw new CloudflareApiException(status, serverMsg, attempt + 1);
                delay = RetryDelay(resp, _retryDelays[attempt]);
                why = $"HTTP {status}" + (status == 429 ? " (limite de débit)" : "");
            }
            catch (HttpRequestException net)
            {
                if (attempt >= _retryDelays.Count) throw new CloudflareApiException(0, net.Message, attempt + 1);
                delay = _retryDelays[attempt];
                why = "erreur réseau";
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                if (attempt >= _retryDelays.Count) throw new CloudflareApiException(0, "délai dépassé (timeout)", attempt + 1);
                delay = _retryDelays[attempt];
                why = "délai dépassé";
            }

            _retryLog?.Invoke($"[Cloudflare] {opLabel} : {why}, nouvelle tentative dans {delay.TotalSeconds:0} s "
                              + $"({attempt + 1}/{_retryDelays.Count}).");
            await Task.Delay(delay, ct);
        }
    }

    private static TimeSpan RetryDelay(HttpResponseMessage resp, TimeSpan scheduled)
    {
        TimeSpan? ra = null;
        var h = resp.Headers.RetryAfter;
        if (h?.Delta is { } d) ra = d;
        else if (h?.Date is { } when) ra = when - DateTimeOffset.UtcNow;
        if (ra is not { } r || r <= scheduled) return scheduled;
        return r > MaxRetryAfter ? MaxRetryAfter : r;
    }
}
