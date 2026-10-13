using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Echo.Ai;

/// <summary>
/// Raised when the Google Generative Language (Gemini) API rejects a request.
/// Same shape as <see cref="MistralApiException"/> (both derive from
/// <see cref="LlmApiException"/>) so callers handle either provider uniformly.
/// </summary>
public sealed class GeminiApiException : LlmApiException
{
    public GeminiApiException(int statusCode, string? serverMessage, int attempts)
        : base($"Gemini API HTTP {statusCode}{(string.IsNullOrWhiteSpace(serverMessage) ? "" : ": " + serverMessage)} (after {attempts} attempt(s))")
    {
        StatusCode = statusCode;
        ServerMessage = serverMessage;
        Attempts = attempts;
    }

    public override int StatusCode { get; }
    public override string? ServerMessage { get; }
    public override int Attempts { get; }

    public override bool IsContextOverflow => ContextOverflowFromStatus();

    public override string UserMessage => StatusCode switch
    {
        _ when IsContextOverflow => "transcription trop longue pour le modèle Gemini (fenêtre de contexte dépassée). "
               + "Choisissez un autre modèle dans Paramètres > Fournisseurs",
        429 => $"limite de l'API Gemini atteinte (429, trop de requêtes) malgré {Attempts} tentatives. "
               + "Attendez une minute puis relancez ; vérifiez le quota de la clé sur aistudio.google.com",
        401 or 403 => $"clé API Google refusée (HTTP {StatusCode}). Vérifiez la clé Gemini dans Paramètres",
        404 => "modèle Gemini introuvable (HTTP 404). Choisissez un autre modèle dans Paramètres > Fournisseurs",
        400 => $"requête refusée par l'API Gemini (HTTP 400){(string.IsNullOrWhiteSpace(ServerMessage) ? "" : " : " + ServerMessage)}",
        0 => $"API Gemini injoignable ({ServerMessage ?? "erreur réseau"}) après {Attempts} tentatives. Vérifiez la connexion Internet",
        >= 500 => $"service Gemini indisponible (HTTP {StatusCode}) malgré {Attempts} tentatives. Réessayez un peu plus tard",
        _ => $"requête refusée par l'API Gemini (HTTP {StatusCode}){(string.IsNullOrWhiteSpace(ServerMessage) ? "" : " : " + ServerMessage)}",
    };
}

/// <summary>
/// Thin REST wrapper over the Google AI Studio (Gemini) generateContent + Files API.
/// Mirrors <see cref="MistralClient"/>: same retry-on-transient ladder, same
/// <see cref="ChatMessage"/>/<see cref="VoxtralResult"/> contracts, so it is a drop-in
/// <see cref="IChatClient"/> + <see cref="ITranscribeClient"/>.
///
/// Transcription: a flash model is prompted to return the SAME JSON shape Voxtral does
/// ({segments:[{start,end,speaker_id,text}]}), parsed by <see cref="VoxtralResponseParser"/>,
/// so everything downstream (diarisation, attribution, output) is unchanged. A dedicated
/// "*-transcribe" model (which returns audioTranscription.text without timestamps) is also
/// supported as a single untimed segment.
/// </summary>
public sealed class GeminiClient : ITranscribeClient, IChatClient, IModelCatalog, IChunkedTranscribe
{
    public const string DefaultBaseUrl = "https://generativelanguage.googleapis.com";

    /// <summary>Transcription pieces of 15 min. A 47 min track (2026-09-17) made the
    /// flash model emit a JSON of 6 000+ lines that was cut at the output-token ceiling
    /// ("Expected end of string, but instead reached end of data"): bounding the input bounds
    /// the output, and per-piece timestamps drift far less than over an hour.</summary>
    public const int DefaultChunkSeconds = 900;

    /// <summary>Explicit output ceiling for the transcription call (the model default is lower).</summary>
    public const int TranscriptionMaxOutputTokens = 65536;

    public int ChunkSeconds { get; init; } = DefaultChunkSeconds;

    /// <summary>Requests above this many bytes of audio go through the Files API instead of
    /// inline base64 (a generateContent request is capped near 20 MB total).</summary>
    private const int InlineAudioLimit = 15 * 1024 * 1024;

    private static readonly TimeSpan[] DefaultRetryDelays =
    {
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60),
    };
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(120);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly Action<string>? _retryLog;
    private readonly IReadOnlyList<TimeSpan> _retryDelays;
    /// <summary>Spoken language of the meeting ("fr", "en", or "auto"/null = let the model
    /// detect), read at call time; only shapes the transcription prompt.</summary>
    private readonly Func<string?>? _language;

    public GeminiClient(HttpClient? http = null, string? baseUrl = null,
        Action<string>? retryLog = null, IReadOnlyList<TimeSpan>? retryDelays = null,
        Func<string?>? language = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
        _retryLog = retryLog;
        _retryDelays = retryDelays ?? DefaultRetryDelays;
        _language = language;
    }

    // ── chat (CR / summary / postprocess) ────────────────────────────────────────

    public async Task<string> ChatCompleteAsync(
        string apiKey, string model, IReadOnlyList<ChatMessage> messages,
        double temperature, bool jsonObject, CancellationToken ct)
    {
        if (!jsonObject)
            return ExtractChatText(await SendChat(apiKey, model, messages, temperature, jsonObject: false, ct), model);
        try
        {
            return ExtractChatText(await SendChat(apiKey, model, messages, temperature, jsonObject: true, ct), model);
        }
        catch (GeminiApiException e) when (e.StatusCode == 400 && !e.IsContextOverflow)
        {
            // Some models reject responseMimeType=application/json ("JSON mode is not enabled").
            // Fall back to a plain call — the caller (minutes) already extracts the first {...}.
            return ExtractChatText(await SendChat(apiKey, model, messages, temperature, jsonObject: false, ct), model);
        }
    }

    /// <summary>
    /// Chat answer text, refusing the two silent failures of generateContent: an answer cut at
    /// the output ceiling (finishReason MAX_TOKENS) and a blocked one (promptFeedback.blockReason,
    /// finishReason SAFETY / RECITATION / PROHIBITED_CONTENT… with no text). Both used to come
    /// back as "" or half a JSON and end up as an empty resume.md.
    /// </summary>
    internal static string ExtractChatText(string body, string model)
    {
        var text = ExtractText(body);
        string? finish = null, block = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("candidates", out var cands) && cands.ValueKind == JsonValueKind.Array
                && cands.GetArrayLength() > 0 && cands[0].TryGetProperty("finishReason", out var fr)
                && fr.ValueKind == JsonValueKind.String)
                finish = fr.GetString();
            if (root.TryGetProperty("promptFeedback", out var pf) && pf.ValueKind == JsonValueKind.Object
                && pf.TryGetProperty("blockReason", out var br) && br.ValueKind == JsonValueKind.String)
                block = br.GetString();
        }
        catch (JsonException) { /* unreadable body: judged on the text alone */ }

        if (string.Equals(finish, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase))
            throw new LlmEmptyResponseException(LlmProvider.Gemini, model, truncated: true, finish);
        if (text.Trim().Length == 0 && (block is not null || (finish is not null && !string.Equals(finish, "STOP", StringComparison.OrdinalIgnoreCase))))
            throw new LlmEmptyResponseException(LlmProvider.Gemini, model, truncated: false,
                "blocage " + (block ?? finish));
        return text;
    }

    private Task<string> SendChat(string apiKey, string model, IReadOnlyList<ChatMessage> messages,
        double temperature, bool jsonObject, CancellationToken ct)
    {
        var (system, contents) = MapMessages(messages);
        var payload = BuildGeneratePayload(contents, temperature, jsonObject, system);
        return SendWithRetryAsync(
            () => JsonPost($"{_baseUrl}/v1beta/models/{model}:generateContent?key={Uri.EscapeDataString(apiKey)}", payload),
            $"chat {model}", ct);
    }

    /// <summary>Gemini uses systemInstruction + contents[{role:user|model, parts}]. Our
    /// ChatMessage roles are "system"/"user"/"assistant"; concatenate system messages into
    /// systemInstruction, map assistant→model, everything else→user.</summary>
    private static (string? System, List<object> Contents) MapMessages(IReadOnlyList<ChatMessage> messages)
    {
        var sys = new StringBuilder();
        var contents = new List<object>();
        foreach (var m in messages)
        {
            if (string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
            {
                if (sys.Length > 0) sys.Append("\n\n");
                sys.Append(m.Content);
                continue;
            }
            var role = string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "model" : "user";
            contents.Add(new { role, parts = new object[] { new { text = m.Content } } });
        }
        return (sys.Length > 0 ? sys.ToString() : null, contents);
    }

    private static Dictionary<string, object?> BuildGeneratePayload(
        List<object> contents, double temperature, bool jsonObject, string? system)
    {
        var genCfg = new Dictionary<string, object?> { ["temperature"] = temperature };
        if (jsonObject) genCfg["responseMimeType"] = "application/json";
        var payload = new Dictionary<string, object?>
        {
            ["contents"] = contents,
            ["generationConfig"] = genCfg,
        };
        if (system is not null)
            payload["systemInstruction"] = new { parts = new object[] { new { text = system } } };
        return payload;
    }

    /// <summary>Concatenate candidates[0].content.parts[*].text (empty when blocked/no text).</summary>
    internal static string ExtractText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("candidates", out var cands) || cands.GetArrayLength() == 0)
                return "";
            var sb = new StringBuilder();
            var content = cands[0];
            if (content.TryGetProperty("content", out var c) && c.TryGetProperty("parts", out var parts))
                foreach (var p in parts.EnumerateArray())
                {
                    if (p.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                        sb.Append(t.GetString());
                    else if (p.TryGetProperty("audioTranscription", out var at)
                             && at.TryGetProperty("text", out var att) && att.ValueKind == JsonValueKind.String)
                        sb.Append(att.GetString());
                }
            return sb.ToString();
        }
        catch { return ""; }
    }

    // ── transcription ────────────────────────────────────────────────────────────

    public async Task<VoxtralResult> TranscribeAsync(
        string apiKey, string model, byte[] audioBytes, string fileName,
        IReadOnlyList<string>? contextBias, IProgress<string>? log, CancellationToken ct)
    {
        var mime = MimeFor(fileName);
        bool transcribeModel = model.Contains("transcribe", StringComparison.OrdinalIgnoreCase);

        // Audio part: inline for small files, Files API for large ones.
        object audioPart;
        string? uploadedFileName = null;
        if (audioBytes.Length <= InlineAudioLimit)
        {
            audioPart = new { inlineData = new { mimeType = mime, data = Convert.ToBase64String(audioBytes) } };
        }
        else
        {
            log?.Report($"  [gemini] envoi via Files API ({audioBytes.Length / (1024 * 1024)} Mo).");
            var (uri, name) = await UploadFileAsync(apiKey, audioBytes, mime, fileName, ct);
            uploadedFileName = name;
            audioPart = new { fileData = new { mimeType = mime, fileUri = uri } };
        }

        try
        {
            var parts = new List<object> { audioPart };
            if (!transcribeModel) parts.Add(new { text = TranscriptionPrompt(contextBias, _language?.Invoke()) });

            var genCfg = new Dictionary<string, object?> { ["temperature"] = 0 };
            if (!transcribeModel)
            {
                genCfg["responseMimeType"] = "application/json";
                genCfg["maxOutputTokens"] = TranscriptionMaxOutputTokens;
            }
            var payload = new Dictionary<string, object?>
            {
                ["contents"] = new object[] { new { role = "user", parts = parts.ToArray() } },
                ["generationConfig"] = genCfg,
            };

            var body = await SendWithRetryAsync(
                () => JsonPost($"{_baseUrl}/v1beta/models/{model}:generateContent?key={Uri.EscapeDataString(apiKey)}", payload),
                $"transcription {fileName}", ct);

            var text = ExtractText(body);
            if (transcribeModel)
            {
                // No timestamps/diarisation: one untimed segment carrying the whole transcript.
                var t = text.Trim();
                var segs = t.Length == 0 ? new List<VoxSegment>() : new List<VoxSegment> { new(null, null, t, null) };
                return new VoxtralResult(t, null, segs, body);
            }
            // Flash JSON path: same shape as Voxtral → reuse its parser. A response cut at the
            // output ceiling is repaired to its last complete segment rather than lost.
            VoxtralResult parsed;
            try { parsed = VoxtralResponseParser.Parse(text.Trim().Length > 0 ? text : "{}"); }
            catch (JsonException)
            {
                var repaired = RepairTruncatedSegmentsJson(text);
                if (repaired is null) throw;
                log?.Report("  [gemini] réponse JSON tronquée par le modèle : segments complets récupérés.");
                parsed = VoxtralResponseParser.Parse(repaired);
            }
            if (parsed.Segments.Count == 0 && text.Trim().Length > 0 && !text.TrimStart().StartsWith('{'))
                // model answered prose despite the JSON ask: keep it as one segment.
                return new VoxtralResult(text, null, new List<VoxSegment> { new(null, null, text.Trim(), null) }, body);
            return parsed;
        }
        finally
        {
            if (uploadedFileName is not null) await TryDeleteFileAsync(apiKey, uploadedFileName, ct);
        }
    }

    /// <summary>
    /// Salvage a <c>{"segments":[ ... </c> answer cut mid-way: keep everything up to the last
    /// complete segment object and close the array and the object. Returns null when no
    /// complete segment can be found (caller rethrows the original parse error).
    /// </summary>
    public static string? RepairTruncatedSegmentsJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.TrimStart();
        int arr = t.IndexOf('[');
        if (arr < 0) return null;
        // Last "}" that closes a segment object: scan backwards for a '}' followed (after
        // whitespace) by ',' or end/'{' context; the simple robust rule is "last '}' whose next
        // non-space char is ',' or nothing", then try to parse; step back on failure.
        int cut = t.LastIndexOf('}');
        for (int guard = 0; cut > arr && guard < 50; guard++)
        {
            var candidate = t[..(cut + 1)] + "]}";
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                if (doc.RootElement.TryGetProperty("segments", out var segs) && segs.ValueKind == JsonValueKind.Array && segs.GetArrayLength() > 0)
                    return candidate;
                return null;
            }
            catch (JsonException)
            {
                cut = t.LastIndexOf('}', cut - 1);
            }
        }
        return null;
    }

    internal static string TranscriptionPrompt(IReadOnlyList<string>? contextBias, string? language = null)
    {
        var sb = new StringBuilder();
        var lang = (language ?? "").Trim().ToLowerInvariant();
        // "auto" (or nothing) imposes no language: the model keeps whatever is spoken.
        sb.Append(lang switch
        {
            "fr" => "Transcris fidèlement et INTÉGRALEMENT cet audio. La réunion se tient en français ; garde tel quel tout passage dans une autre langue, sans le traduire. ",
            "en" => "Transcris fidèlement et INTÉGRALEMENT cet audio. La réunion se tient en anglais ; garde tel quel tout passage dans une autre langue, sans le traduire. ",
            _ => "Transcris fidèlement et INTÉGRALEMENT cet audio, dans la langue effectivement parlée, sans jamais traduire. ",
        });
        sb.Append("Découpe par tours de parole. ");
        sb.Append("Réponds UNIQUEMENT par un objet JSON de la forme ");
        sb.Append("{\"segments\":[{\"start\":<secondes depuis le début, nombre>,\"end\":<secondes, nombre>,\"speaker_id\":<entier stable par locuteur, ou null>,\"text\":<verbatim>}]}. ");
        sb.Append("N'ajoute aucun texte hors du JSON. Ne résume pas, ne reformule pas, garde le verbatim.");
        if (contextBias is { Count: > 0 })
        {
            // The shared bias list is Voxtral-shaped ("Claire_MOREAU", no spaces allowed there):
            // a prompt reads natural text, so the spaces come back.
            var terms = string.Join(", ", contextBias.Select(Vocabulary.ToPromptTerm)
                .Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(60));
            sb.Append(" Termes métier/noms propres à orthographier exactement ainsi : ").Append(terms).Append('.');
        }
        return sb.ToString();
    }

    private static string MimeFor(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".mp3" => "audio/mp3",
            ".wav" => "audio/wav",
            ".m4a" or ".aac" => "audio/aac",
            ".ogg" or ".opus" => "audio/ogg",
            ".flac" => "audio/flac",
            _ => "audio/mpeg",
        };
    }

    // ── Files API (resumable upload) ─────────────────────────────────────────────

    private async Task<(string Uri, string Name)> UploadFileAsync(
        string apiKey, byte[] bytes, string mime, string displayName, CancellationToken ct)
    {
        // 1) start a resumable upload session.
        var startUrl = $"{_baseUrl}/upload/v1beta/files?key={Uri.EscapeDataString(apiKey)}";
        string uploadUrl;
        using (var start = new HttpRequestMessage(HttpMethod.Post, startUrl))
        {
            start.Headers.TryAddWithoutValidation("X-Goog-Upload-Protocol", "resumable");
            start.Headers.TryAddWithoutValidation("X-Goog-Upload-Command", "start");
            start.Headers.TryAddWithoutValidation("X-Goog-Upload-Header-Content-Length", bytes.Length.ToString());
            start.Headers.TryAddWithoutValidation("X-Goog-Upload-Header-Content-Type", mime);
            var meta = JsonSerializer.Serialize(new { file = new { display_name = displayName } });
            start.Content = new StringContent(meta, Encoding.UTF8, "application/json");
            using var sresp = await _http.SendAsync(start, ct);
            if (!sresp.IsSuccessStatusCode)
                throw new GeminiApiException((int)sresp.StatusCode,
                    MistralClient.ExtractServerMessage(await sresp.Content.ReadAsStringAsync(ct)), 1);
            uploadUrl = sresp.Headers.TryGetValues("X-Goog-Upload-URL", out var v) ? v.First()
                : throw new GeminiApiException(0, "Files API: pas d'URL d'upload renvoyée", 1);
        }

        // 2) upload the bytes and finalize.
        JsonElement fileObj;
        using (var up = new HttpRequestMessage(HttpMethod.Post, uploadUrl))
        {
            up.Headers.TryAddWithoutValidation("X-Goog-Upload-Command", "upload, finalize");
            up.Headers.TryAddWithoutValidation("X-Goog-Upload-Offset", "0");
            up.Content = new ByteArrayContent(bytes);
            up.Content.Headers.ContentType = new MediaTypeHeaderValue(mime);
            using var uresp = await _http.SendAsync(up, ct);
            var ubody = await uresp.Content.ReadAsStringAsync(ct);
            if (!uresp.IsSuccessStatusCode)
                throw new GeminiApiException((int)uresp.StatusCode, MistralClient.ExtractServerMessage(ubody), 1);
            fileObj = JsonDocument.Parse(ubody).RootElement.GetProperty("file").Clone();
        }

        var name = fileObj.GetProperty("name").GetString() ?? "";
        var uri = fileObj.GetProperty("uri").GetString() ?? "";
        var state = fileObj.TryGetProperty("state", out var st) ? st.GetString() : "ACTIVE";

        // 3) wait until the file is ACTIVE (audio is usually near-instant; cap the wait).
        for (int i = 0; state == "PROCESSING" && i < 30; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            using var g = await _http.GetAsync($"{_baseUrl}/v1beta/{name}?key={Uri.EscapeDataString(apiKey)}", ct);
            var gbody = await g.Content.ReadAsStringAsync(ct);
            if (!g.IsSuccessStatusCode) break;
            var f = JsonDocument.Parse(gbody).RootElement;
            state = f.TryGetProperty("state", out var s2) ? s2.GetString() : "ACTIVE";
            uri = f.TryGetProperty("uri", out var u2) ? u2.GetString() ?? uri : uri;
        }
        if (state == "FAILED") throw new GeminiApiException(0, "Files API: traitement du fichier échoué", 1);
        return (uri, name);
    }

    private async Task TryDeleteFileAsync(string apiKey, string name, CancellationToken ct)
    {
        try
        {
            using var del = new HttpRequestMessage(HttpMethod.Delete,
                $"{_baseUrl}/v1beta/{name}?key={Uri.EscapeDataString(apiKey)}");
            using var _ = await _http.SendAsync(del, ct);
        }
        catch { /* best effort cleanup */ }
    }

    // ── model catalogue + probe (IModelCatalog) ─────────────────────────────────

    /// <summary>GET /v1beta/models?key=… following <c>nextPageToken</c>. Single attempt per
    /// page; throws a <see cref="GeminiApiException"/> on HTTP error / network failure.</summary>
    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(string apiKey, CancellationToken ct)
    {
        var all = new Dictionary<string, ModelInfo>(StringComparer.OrdinalIgnoreCase);
        string? pageToken = null;
        for (int page = 0; page < 20; page++)   // hard cap: a runaway token loop must not spin forever
        {
            var url = $"{_baseUrl}/v1beta/models?key={Uri.EscapeDataString(apiKey)}&pageSize=200"
                      + (pageToken is null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken));
            string body;
            HttpResponseMessage resp;
            try { resp = await _http.GetAsync(url, ct); }
            catch (HttpRequestException net) { throw new GeminiApiException(0, net.Message, 1); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new GeminiApiException(0, "délai dépassé (timeout)", 1); }
            using (resp)
            {
                body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                    throw new GeminiApiException((int)resp.StatusCode, MistralClient.ExtractServerMessage(body), 1);
            }
            foreach (var m in ParseModelPage(body, out pageToken))
                all.TryAdd(m.Id, m);
            if (string.IsNullOrEmpty(pageToken)) break;
        }
        return all.Values.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Parse one page of the models list: <c>models[].name</c> ("models/xxx" → "xxx"),
    /// Chat = <c>supportedGenerationMethods</c> contains generateContent (and not a
    /// dedicated *-transcribe model), Transcribe = generateContent and id starts with
    /// "gemini" (flash/pro accept audio). Embedding / imagen / veo / tts / image / live
    /// families are excluded. <paramref name="nextPageToken"/> is null on the last page.
    /// </summary>
    public static IReadOnlyList<ModelInfo> ParseModelPage(string json, out string? nextPageToken)
    {
        nextPageToken = null;
        var list = new List<ModelInfo>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("nextPageToken", out var tok) && tok.ValueKind == JsonValueKind.String)
            nextPageToken = string.IsNullOrWhiteSpace(tok.GetString()) ? null : tok.GetString();
        if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return list;
        foreach (var m in models.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object) continue;
            var name = m.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
            var id = name.StartsWith("models/", StringComparison.OrdinalIgnoreCase) ? name["models/".Length..] : name;
            id = id.Trim();
            if (id.Length == 0 || IsExcludedFamily(id)) continue;
            bool generate = false;
            if (m.TryGetProperty("supportedGenerationMethods", out var methods) && methods.ValueKind == JsonValueKind.Array)
                foreach (var meth in methods.EnumerateArray())
                    if (meth.ValueKind == JsonValueKind.String
                        && string.Equals(meth.GetString(), "generateContent", StringComparison.OrdinalIgnoreCase))
                    { generate = true; break; }
            if (!generate) continue;   // embeddings, aqa, etc. carry other methods only
            bool isGemini = id.StartsWith("gemini", StringComparison.OrdinalIgnoreCase);
            bool dedicatedTranscribe = id.Contains("transcribe", StringComparison.OrdinalIgnoreCase);
            list.Add(new ModelInfo(id, Chat: !dedicatedTranscribe, Transcribe: isGemini));
        }
        return list;
    }

    private static bool IsExcludedFamily(string id)
    {
        foreach (var bad in new[] { "embedding", "imagen", "veo", "-tts", "-image", "-live", "native-audio", "aqa" })
            if (id.Contains(bad, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>One minimal generateContent (maxOutputTokens 1), ONE attempt, no retry;
    /// returns the status instead of throwing.</summary>
    public async Task<ModelProbeResult> ProbeChatModelAsync(string apiKey, string model, CancellationToken ct)
    {
        var payload = new
        {
            contents = new object[] { new { role = "user", parts = new object[] { new { text = "ping" } } } },
            generationConfig = new { maxOutputTokens = 1 },
        };
        try
        {
            using var req = JsonPost($"{_baseUrl}/v1beta/models/{model}:generateContent?key={Uri.EscapeDataString(apiKey)}", payload);
            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            return new ModelProbeResult(resp.IsSuccessStatusCode, (int)resp.StatusCode,
                resp.IsSuccessStatusCode ? null : MistralClient.ExtractServerMessage(body));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new ModelProbeResult(false, 0, ex is TaskCanceledException ? "délai dépassé (timeout)" : ex.Message);
        }
    }

    // ── transient-failure retry layer (mirrors MistralClient) ────────────────────

    private static HttpRequestMessage JsonPost(string url, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        return new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private async Task<string> SendWithRetryAsync(
        Func<HttpRequestMessage> makeRequest, string opLabel, CancellationToken ct)
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
                if (resp.IsSuccessStatusCode) return body;

                int status = (int)resp.StatusCode;
                var serverMsg = MistralClient.ExtractServerMessage(body);
                if (!LlmApiException.IsRetryableStatus(status) || attempt >= _retryDelays.Count)
                    throw new GeminiApiException(status, serverMsg, attempt + 1);
                delay = RetryDelay(resp, _retryDelays[attempt]);
                why = $"HTTP {status}" + (status == 429 ? " (limite de débit)" : "");
            }
            catch (HttpRequestException net)
            {
                if (attempt >= _retryDelays.Count) throw new GeminiApiException(0, net.Message, attempt + 1);
                delay = _retryDelays[attempt];
                why = "erreur réseau";
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                if (attempt >= _retryDelays.Count) throw new GeminiApiException(0, "délai dépassé (timeout)", attempt + 1);
                delay = _retryDelays[attempt];
                why = "délai dépassé";
            }

            _retryLog?.Invoke($"[Gemini] {opLabel} : {why} — nouvelle tentative dans {delay.TotalSeconds:0} s "
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
