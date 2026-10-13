using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Echo.Ai;

/// <summary>One chat message for the Mistral chat completions endpoint.</summary>
public sealed record ChatMessage(string Role, string Content);

/// <summary>One extracted Voxtral segment (timestamps in track-local seconds).</summary>
public sealed record VoxSegment(double? Start, double? End, string Text, string? VoxSpeaker);

/// <summary>Parsed Voxtral transcription response (tolerant to field aliasing).</summary>
public sealed record VoxtralResult(string? Text, string? Language, IReadOnlyList<VoxSegment> Segments, string RawJson);

/// <summary>
/// Tolerant parser for the Voxtral transcription response. Field aliasing is
/// deliberate and load-bearing (see ai.md §1.7 / §6.3):
///  - start:  <c>start</c>  -> <c>start_time</c>
///  - end:    <c>end</c>    -> <c>end_time</c>
///  - speaker: <c>speaker_id</c> FIRST, then <c>speaker</c>, then <c>speaker_index</c>
///    (speaker_id may be an int OR a string).
/// Segments with empty text are dropped.
/// </summary>
public static class VoxtralResponseParser
{
    public static VoxtralResult Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? text = GetString(root, "text");
        string? language = GetString(root, "language");

        var segments = new List<VoxSegment>();
        if (root.TryGetProperty("segments", out var segs) && segs.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in segs.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object) continue;
                var segText = (GetString(s, "text") ?? "").Trim();
                if (segText.Length == 0) continue;                    // drop empty segments
                var start = GetDouble(s, "start") ?? GetDouble(s, "start_time");
                var end = GetDouble(s, "end") ?? GetDouble(s, "end_time");
                var speaker = GetSpeaker(s);
                segments.Add(new VoxSegment(start, end, segText, speaker));
            }
        }
        return new VoxtralResult(text, language, segments, json);
    }

    private static string? GetString(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? GetDouble(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDouble(out var d) ? d : null,
            JsonValueKind.String => double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null,
            _ => null,
        };
    }

    /// <summary>speaker_id (int or string) -> speaker -> speaker_index, as a string; null if none.</summary>
    private static string? GetSpeaker(JsonElement s)
    {
        foreach (var name in new[] { "speaker_id", "speaker", "speaker_index" })
        {
            if (!s.TryGetProperty(name, out var v)) continue;
            switch (v.ValueKind)
            {
                case JsonValueKind.String:
                    var str = v.GetString();
                    if (!string.IsNullOrEmpty(str)) return str;
                    break;
                case JsonValueKind.Number:
                    return v.TryGetInt64(out var i)
                        ? i.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : v.GetRawText();
            }
        }
        return null;
    }
}

/// <summary>
/// Builds the exact <see cref="HttpRequestMessage"/> objects for the two Mistral
/// endpoints, kept separate from I/O so the request shape is unit-testable
/// (correct-by-construction; no live call needed).
/// </summary>
public static class MistralRequestBuilder
{
    public const string DefaultBaseUrl = "https://api.mistral.ai";

    /// <summary>Form-field name for repeated array fields. Mistral/httpx repeats the
    /// bare key (no <c>[]</c> suffix); confirm against a live 200 if biasing looks inert.</summary>
    public const string ContextBiasField = "context_bias";
    public const string TimestampGranularitiesField = "timestamp_granularities";

    /// <summary>
    /// POST /v1/audio/transcriptions (multipart/form-data). Base fields on every
    /// call: model, file, temperature=0, timestamp_granularities=segment.
    /// language is deliberately NOT sent (incompatible with segment granularity).
    /// </summary>
    public static HttpRequestMessage BuildTranscription(
        string baseUrl, string apiKey, string model,
        byte[] wavBytes, string fileName,
        IReadOnlyList<string>? contextBias, bool diarize)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(model), "model" },
            { new StringContent("0"), "temperature" },
            { new StringContent("segment"), TimestampGranularitiesField },
        };

        var file = new ByteArrayContent(wavBytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);

        if (diarize) form.Add(new StringContent("true"), "diarize");

        if (contextBias is { Count: > 0 })
            foreach (var term in contextBias)
                form.Add(new StringContent(term), ContextBiasField);   // repeated field, one per term

        var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/audio/transcriptions")
        {
            Content = form,
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return req;
    }

    /// <summary>POST /v1/chat/completions (JSON). response_format json_object optional.</summary>
    public static HttpRequestMessage BuildChat(
        string baseUrl, string apiKey, string model,
        IReadOnlyList<ChatMessage> messages, double temperature, bool jsonObject)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["temperature"] = temperature,
            ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
        };
        if (jsonObject) payload["response_format"] = new { type = "json_object" };

        var json = JsonSerializer.Serialize(payload);
        var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return req;
    }
}

/// <summary>
/// Raised when the Mistral API rejects a request (after retries, for retryable
/// statuses). Carries the HTTP status (0 = network/timeout, never reached the API),
/// the server-reported message, and a ready-to-show French <see cref="UserMessage"/>
/// so the UI can say "limite API Mistral" instead of a raw HttpRequestException.
/// </summary>
public sealed class MistralApiException : LlmApiException
{
    public MistralApiException(int statusCode, string? serverMessage, int attempts, string? model = null,
                               bool zeroQuota = false)
        : base($"Mistral API HTTP {statusCode}{(string.IsNullOrWhiteSpace(serverMessage) ? "" : ": " + serverMessage)} (after {attempts} attempt(s))")
    {
        StatusCode = statusCode;
        ServerMessage = serverMessage;
        Attempts = attempts;
        Model = model;
        IsZeroQuota = zeroQuota;
    }

    /// <summary>429 with <c>x-ratelimit-limit-req-minute: 0</c>: the key is allowed ZERO requests
    /// per minute on this model (seen 2026-10-13 on mistral-small/medium-latest). Mistral's way of
    /// saying "not in your plan"; retrying can never succeed.</summary>
    public bool IsZeroQuota { get; }

    public override bool IsRetryable => !IsZeroQuota && base.IsRetryable;

    /// <summary>HTTP status; 0 when the request never got an HTTP response (network/timeout).</summary>
    public override int StatusCode { get; }
    public override string? ServerMessage { get; }
    public override int Attempts { get; }

    /// <summary>Model the refused request named (null when unknown, e.g. catalogue calls).</summary>
    public string? Model { get; }

    /// <summary>403 "This model is not available in your subscription tier": the KEY is fine,
    /// the model is simply not part of its plan (seen 2026-09-25 on mistral-large-latest).</summary>
    public bool IsTierRestricted
        => IsZeroQuota
           || StatusCode == 403 && ((ServerMessage ?? "").Contains("subscription tier", StringComparison.OrdinalIgnoreCase)
                                    || (ServerMessage ?? "").Contains("not available in your", StringComparison.OrdinalIgnoreCase));

    /// <summary>400 "Invalid model": the model id does not exist (seen on voxtral-small-latest).</summary>
    public bool IsInvalidModel
        => StatusCode is 400 or 404 && ((ServerMessage ?? "").Contains("invalid model", StringComparison.OrdinalIgnoreCase)
                                        || (ServerMessage ?? "").Contains("invalid_model", StringComparison.OrdinalIgnoreCase));

    public override bool IsContextOverflow => ContextOverflowFromStatus();

    /// <summary>French, user-facing, actionable — shown in toasts/balloons.</summary>
    public override string UserMessage => this switch
    {
        { IsTierRestricted: true } =>
            $"Le modèle {Model ?? "demandé"} n'est pas inclus dans l'offre de votre clé Mistral. "
            + "Choisissez un autre modèle dans Paramètres > Fournisseurs.",
        { IsInvalidModel: true } =>
            $"Le modèle {Model ?? "demandé"} est inconnu de l'API Mistral. Choisissez un autre modèle dans Paramètres > Fournisseurs.",
        { IsContextOverflow: true } =>
            $"transcription trop longue pour le modèle Mistral {Model ?? ""} (fenêtre de contexte dépassée). "
            + "Choisissez un modèle à plus grande fenêtre dans Paramètres > Fournisseurs",
        _ => StatusMessage,
    };

    private string StatusMessage => StatusCode switch
    {
        429 => $"limite de l'API Mistral atteinte (429, trop de requêtes) malgré {Attempts} tentatives. "
               + "Attendez 1 à 2 minutes puis relancez ; si cela persiste, vérifiez le quota de la clé sur console.mistral.ai",
        401 or 403 => $"clé API Mistral refusée (HTTP {StatusCode}). Vérifiez la clé dans Paramètres",
        0 => $"API Mistral injoignable ({ServerMessage ?? "erreur réseau"}) après {Attempts} tentatives. Vérifiez la connexion Internet",
        >= 500 => $"service Mistral indisponible (HTTP {StatusCode}) malgré {Attempts} tentatives. Réessayez un peu plus tard",
        _ => $"requête refusée par l'API Mistral (HTTP {StatusCode}){(string.IsNullOrWhiteSpace(ServerMessage) ? "" : " : " + ServerMessage)}",
    };
}

/// <summary>
/// Thin REST wrapper over the two Mistral endpoints. There is no official .NET
/// Mistral SDK; this uses <see cref="HttpClient"/> + <see cref="System.Text.Json"/>.
/// Reproduces the parameter-shedding ladder (transcription) and the
/// response_format fallback (chat), and retries transient failures (429/5xx/network)
/// with backoff — a post-meeting pipeline must survive a one-minute rate-limit window.
/// </summary>
public sealed class MistralClient : ITranscribeClient, IChatClient, IModelCatalog, IModelFallbackSource
{
    /// <summary>First chat model tried when the configured one is outside the key's plan.</summary>
    public const string FallbackChatModel = "ministral-8b-latest";

    /// <summary>Fallback chain, tried in order (skipping the refused model). Free-plan models
    /// first: on 2026-10-13 a free key got 0 req/min on mistral-small/medium while ministral-8b
    /// and open-mistral-nemo answered at 188 req/min. mistral-small last, for paid keys.</summary>
    public static readonly IReadOnlyList<string> FallbackChatModels =
        new[] { FallbackChatModel, "open-mistral-nemo", "mistral-small-latest" };

    /// <summary>Transcription model retried once when the configured Voxtral id is unknown.</summary>
    public const string FallbackTranscribeModel = "voxtral-mini-latest";

    public event Action<ModelFallback>? ModelFallback;
    public ModelFallback? LastFallback { get; private set; }

    private void ReportFallback(string requested, string used, string reason, string logLine)
    {
        var fb = new ModelFallback(LlmProvider.Mistral, requested, used, reason);
        LastFallback = fb;
        _retryLog?.Invoke(logLine);
        try { ModelFallback?.Invoke(fb); } catch { /* a listener must never break the call */ }
    }

    /// <summary>Waits between attempts (5 tries total). A Mistral 429 window usually
    /// resets within a minute; the schedule covers ~110 s before giving up.</summary>
    private static readonly TimeSpan[] DefaultRetryDelays =
    {
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60),
    };

    /// <summary>Honour Retry-After up to this cap (a server asking for hours is a quota issue, not a burst).</summary>
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(120);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly Action<string>? _retryLog;
    private readonly IReadOnlyList<TimeSpan> _retryDelays;

    public MistralClient(HttpClient? http = null, string? baseUrl = null,
        Action<string>? retryLog = null, IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _baseUrl = (baseUrl ?? MistralRequestBuilder.DefaultBaseUrl).TrimEnd('/');
        _retryLog = retryLog;
        _retryDelays = retryDelays ?? DefaultRetryDelays;
    }

    /// <summary>
    /// Transcribe one WAV. Ladder: full (diarize + bias) -> drop bias (keep diarize)
    /// -> drop diarize. The byte buffer is reused (equivalent to f.seek(0)).
    /// The ladder ONLY degrades on 400/422 (server rejecting a parameter) — a 429 or
    /// 5xx is retried with backoff by <see cref="SendWithRetryAsync"/> and, if it
    /// still fails, must surface as-is: re-uploading without diarize would not fix a
    /// rate limit, it would just burn more of it and silently lose diarisation.
    /// </summary>
    public async Task<VoxtralResult> TranscribeAsync(
        string apiKey, string model, byte[] wavBytes, string fileName,
        IReadOnlyList<string>? contextBias, IProgress<string>? log, CancellationToken ct)
    {
        try
        {
            return await TranscribeLadder(apiKey, model, wavBytes, fileName, contextBias, log, ct);
        }
        catch (MistralApiException e) when (e.IsInvalidModel
                                             && !string.Equals(model, FallbackTranscribeModel, StringComparison.OrdinalIgnoreCase))
        {
            // 400 "Invalid model" (voxtral-small-latest, 2026-09-25): the model id is wrong, not
            // the audio. One retry on the always-available mini model rather than losing the meeting.
            var line = $"[Mistral] modèle {model} inconnu, repli sur {FallbackTranscribeModel}";
            ReportFallback(model, FallbackTranscribeModel, "invalid_model", line);
            log?.Report("  " + line);
            return await TranscribeLadder(apiKey, FallbackTranscribeModel, wavBytes, fileName, contextBias, log, ct);
        }
    }

    private async Task<VoxtralResult> TranscribeLadder(
        string apiKey, string model, byte[] wavBytes, string fileName,
        IReadOnlyList<string>? contextBias, IProgress<string>? log, CancellationToken ct)
    {
        var hasBias = contextBias is { Count: > 0 };
        try
        {
            return await SendTranscription(apiKey, model, wavBytes, fileName,
                hasBias ? contextBias : null, diarize: true, ct);
        }
        catch (MistralApiException e1) when (e1.StatusCode is 400 or 422 && !e1.IsInvalidModel)
        {
            log?.Report($"  [voxtral] full call rejected (HTTP {e1.StatusCode}); retrying degraded.");
            if (hasBias)
            {
                try
                {
                    return await SendTranscription(apiKey, model, wavBytes, fileName, null, diarize: true, ct);
                }
                catch (MistralApiException e2) when (e2.StatusCode is 400 or 422 && !e2.IsInvalidModel)
                {
                    log?.Report($"  [voxtral] retry without diarize (HTTP {e2.StatusCode}).");
                    return await SendTranscription(apiKey, model, wavBytes, fileName, null, diarize: false, ct);
                }
            }
            return await SendTranscription(apiKey, model, wavBytes, fileName, null, diarize: false, ct);
        }
    }

    private async Task<VoxtralResult> SendTranscription(
        string apiKey, string model, byte[] wavBytes, string fileName,
        IReadOnlyList<string>? contextBias, bool diarize, CancellationToken ct)
    {
        var body = await SendWithRetryAsync(
            () => MistralRequestBuilder.BuildTranscription(_baseUrl, apiKey, model, wavBytes, fileName, contextBias, diarize),
            $"transcription {fileName}", ct, model);
        return VoxtralResponseParser.Parse(body);
    }

    // ── transient-failure retry layer ───────────────────────────────────────────

    /// <summary>
    /// Send with retries on 429/5xx/network/timeout. The request is REBUILT for each
    /// attempt (HttpRequestMessage is single-use); non-retryable statuses throw a
    /// <see cref="MistralApiException"/> immediately, retryable ones after the
    /// schedule is exhausted. Honours Retry-After when the server provides one.
    /// </summary>
    private async Task<string> SendWithRetryAsync(
        Func<HttpRequestMessage> makeRequest, string opLabel, CancellationToken ct, string? model = null)
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
                var serverMsg = ExtractServerMessage(body);
                if (status == 429 && IsZeroQuota(resp))
                    throw new MistralApiException(status, serverMsg, attempt + 1, model, zeroQuota: true);
                if (!MistralApiException.IsRetryableStatus(status) || attempt >= _retryDelays.Count)
                    throw new MistralApiException(status, serverMsg, attempt + 1, model);
                delay = RetryDelay(resp, _retryDelays[attempt]);
                why = $"HTTP {status}" + (status == 429 ? " (limite de débit)" : "");
            }
            catch (HttpRequestException net)
            {
                if (attempt >= _retryDelays.Count) throw new MistralApiException(0, net.Message, attempt + 1, model);
                delay = _retryDelays[attempt];
                why = "erreur réseau";
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                // HttpClient timeout (not a user cancellation).
                if (attempt >= _retryDelays.Count) throw new MistralApiException(0, "délai dépassé (timeout)", attempt + 1, model);
                delay = _retryDelays[attempt];
                why = "délai dépassé";
            }

            _retryLog?.Invoke($"[Mistral] {opLabel} : {why} — nouvelle tentative dans {delay.TotalSeconds:0} s "
                              + $"({attempt + 1}/{_retryDelays.Count}).");
            await Task.Delay(delay, ct);
        }
    }

    /// <summary>A 429 whose per-minute request allowance is 0: the model is outside the key's plan.</summary>
    internal static bool IsZeroQuota(HttpResponseMessage resp)
        => resp.Headers.TryGetValues("x-ratelimit-limit-req-minute", out var v)
           && v.FirstOrDefault()?.Trim() == "0";

    /// <summary>Server Retry-After (delta or date), clamped to [scheduled, 120 s].</summary>
    private static TimeSpan RetryDelay(HttpResponseMessage resp, TimeSpan scheduled)
    {
        TimeSpan? ra = null;
        var h = resp.Headers.RetryAfter;
        if (h?.Delta is { } d) ra = d;
        else if (h?.Date is { } when) ra = when - DateTimeOffset.UtcNow;
        if (ra is not { } r || r <= scheduled) return scheduled;
        return r > MaxRetryAfter ? MaxRetryAfter : r;
    }

    /// <summary>Best-effort extraction of the error message from a Mistral error body
    /// ({"message": …} or {"error": {"message": …}} or {"detail": …}); truncated.</summary>
    internal static string? ExtractServerMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Truncate(body);
            if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                return Truncate(m.GetString());
            if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object
                && e.TryGetProperty("message", out var em) && em.ValueKind == JsonValueKind.String)
                return Truncate(em.GetString());
            if (root.TryGetProperty("detail", out var det))
                return Truncate(det.ValueKind == JsonValueKind.String ? det.GetString() : det.GetRawText());
            return Truncate(body);
        }
        catch
        {
            return Truncate(body);
        }
    }

    private static string? Truncate(string? s)
    {
        s = s?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        return s.Length <= 300 ? s : s[..300] + "…";
    }

    /// <summary>
    /// One chat completion. Returns the assistant message content.
    /// With <paramref name="jsonObject"/> (minutes/CR path) it requests
    /// response_format=json_object and falls back without it ONLY when the server
    /// rejects the parameter itself (400/422) — a 429/5xx is already retried with
    /// backoff, and re-sending without JSON mode would not fix it (it would burn
    /// more rate limit and lose the structured output).
    /// WITHOUT it (summary, free-text paths) the request is plain — forcing JSON
    /// mode on a prose prompt makes the model wrap its answer in {"content": …},
    /// which then shows up verbatim in the UI.
    /// </summary>
    public async Task<string> ChatCompleteAsync(
        string apiKey, string model, IReadOnlyList<ChatMessage> messages,
        double temperature, bool jsonObject, CancellationToken ct)
    {
        try
        {
            return await ChatWithJsonFallback(apiKey, model, messages, temperature, jsonObject, ct);
        }
        catch (MistralApiException e) when (e.IsTierRestricted)
        {
            // The key works but the model is outside its plan: 403 "not available in your
            // subscription tier" (mistral-large-latest, 2026-09-25) or a 429 with a 0 req/min
            // allowance (mistral-small-latest, 2026-10-13). Walk the fallback chain so the
            // CR / summary / postprocess survive instead of failing the session.
            var last = e;
            foreach (var fallback in FallbackChatModels)
            {
                if (string.Equals(fallback, model, StringComparison.OrdinalIgnoreCase)) continue;
                ReportFallback(model, fallback, "subscription_tier",
                    $"[Mistral] modèle {model} hors offre, repli sur {fallback}");
                try { return await ChatWithJsonFallback(apiKey, fallback, messages, temperature, jsonObject, ct); }
                catch (MistralApiException e2) when (e2.IsTierRestricted) { last = e2; }
            }
            throw last;
        }
    }

    private async Task<string> ChatWithJsonFallback(
        string apiKey, string model, IReadOnlyList<ChatMessage> messages,
        double temperature, bool jsonObject, CancellationToken ct)
    {
        if (!jsonObject)
            return await SendChat(apiKey, model, messages, temperature, jsonObject: false, ct);
        try
        {
            return await SendChat(apiKey, model, messages, temperature, jsonObject: true, ct);
        }
        catch (MistralApiException e) when (e.StatusCode is 400 or 422 && !e.IsContextOverflow && !e.IsInvalidModel)
        {
            return await SendChat(apiKey, model, messages, temperature, jsonObject: false, ct);
        }
    }

    private async Task<string> SendChat(
        string apiKey, string model, IReadOnlyList<ChatMessage> messages,
        double temperature, bool jsonObject, CancellationToken ct)
    {
        var body = await SendWithRetryAsync(
            () => MistralRequestBuilder.BuildChat(_baseUrl, apiKey, model, messages, temperature, jsonObject),
            $"chat {model}", ct, model);
        return ExtractChatContent(body, LlmProvider.Mistral, model);
    }

    /// <summary>
    /// choices[0].message.content of an OpenAI-shaped chat answer (Mistral, Workers AI).
    /// A finish_reason "length" means the answer was cut at the output ceiling: raised as a
    /// truncated <see cref="LlmEmptyResponseException"/> rather than returned half-written.
    /// Empty content is returned as-is; the callers decide (the summary refuses it).
    /// </summary>
    internal static string ExtractChatContent(string body, string provider, string model)
    {
        using var doc = JsonDocument.Parse(body);
        var choice = doc.RootElement.GetProperty("choices")[0];
        var content = choice.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c)
                      && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
        if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
        {
            var reason = fr.GetString();
            if (string.Equals(reason, "length", StringComparison.OrdinalIgnoreCase)
                || string.Equals(reason, "model_length", StringComparison.OrdinalIgnoreCase))
                throw new LlmEmptyResponseException(provider, model, truncated: true, reason);
            if (content.Trim().Length == 0 && string.Equals(reason, "content_filter", StringComparison.OrdinalIgnoreCase))
                throw new LlmEmptyResponseException(provider, model, truncated: false, "filtre de contenu");
        }
        return content;
    }

    // ── model catalogue + probe (IModelCatalog) ─────────────────────────────────

    /// <summary>GET /v1/models, single attempt (interactive Settings call). Throws a
    /// <see cref="MistralApiException"/> on HTTP error / network failure.</summary>
    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(string apiKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/v1/models");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        string body;
        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, ct); }
        catch (HttpRequestException net) { throw new MistralApiException(0, net.Message, 1); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new MistralApiException(0, "délai dépassé (timeout)", 1); }
        using (resp)
        {
            body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new MistralApiException((int)resp.StatusCode, ExtractServerMessage(body), 1);
        }
        return ParseModelList(body);
    }

    /// <summary>
    /// Parse a /v1/models response: <c>data[].id</c> plus, when present,
    /// <c>capabilities.completion_chat</c>. Voxtral ids are the transcription models.
    /// Without a capabilities block, chat is inferred by excluding the obvious non-chat
    /// families (embed, moderation, ocr, voxtral). Duplicates removed, sorted by id.
    /// </summary>
    public static IReadOnlyList<ModelInfo> ParseModelList(string json)
    {
        var seen = new Dictionary<string, ModelInfo>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return Array.Empty<ModelInfo>();
        foreach (var m in data.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object) continue;
            var id = m.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString()?.Trim() : null;
            if (string.IsNullOrEmpty(id) || seen.ContainsKey(id)) continue;
            bool transcribe = id.Contains("voxtral", StringComparison.OrdinalIgnoreCase);
            bool chat;
            if (m.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Object
                && caps.TryGetProperty("completion_chat", out var cc)
                && cc.ValueKind is JsonValueKind.True or JsonValueKind.False)
                chat = cc.GetBoolean() && !transcribe;
            else
                chat = !transcribe && !IsNonChatFamily(id);
            seen[id] = new ModelInfo(id, chat, transcribe);
        }
        return seen.Values.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsNonChatFamily(string id)
        => id.Contains("embed", StringComparison.OrdinalIgnoreCase)
        || id.Contains("moderation", StringComparison.OrdinalIgnoreCase)
        || id.Contains("ocr", StringComparison.OrdinalIgnoreCase);

    /// <summary>One minimal chat completion (max_tokens 1), ONE attempt, no retry: the
    /// point is to read the status (403 = not in this key's tier, 429 = saturated, 401 = bad key).</summary>
    public async Task<ModelProbeResult> ProbeChatModelAsync(string apiKey, string model, CancellationToken ct)
    {
        var payload = new
        {
            model,
            messages = new[] { new { role = "user", content = "ping" } },
            max_tokens = 1,
        };
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            // A zero-allowance 429 is a plan restriction, not saturation: report it like a 403 tier.
            if ((int)resp.StatusCode == 429 && IsZeroQuota(resp))
                return new ModelProbeResult(false, 403, "not available in your subscription tier (0 requests/minute)");
            return new ModelProbeResult(resp.IsSuccessStatusCode, (int)resp.StatusCode,
                resp.IsSuccessStatusCode ? null : ExtractServerMessage(body));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new ModelProbeResult(false, 0, ex is TaskCanceledException ? "délai dépassé (timeout)" : ex.Message);
        }
    }
}
