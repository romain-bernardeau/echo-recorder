using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Echo.Ai;

/// <summary>
/// Raised when the OpenRouter API rejects a request. Same shape as the Mistral / Gemini /
/// Cloudflare exceptions (all derive from <see cref="LlmApiException"/>) so the orchestrator
/// handles every provider uniformly.
/// </summary>
public sealed class OpenRouterApiException : LlmApiException
{
    public OpenRouterApiException(int statusCode, string? serverMessage, int attempts, string? model = null)
        : base($"OpenRouter API HTTP {statusCode}{(string.IsNullOrWhiteSpace(serverMessage) ? "" : ": " + serverMessage)} (after {attempts} attempt(s))")
    {
        StatusCode = statusCode;
        ServerMessage = serverMessage;
        Attempts = attempts;
        Model = model;
    }

    public override int StatusCode { get; }
    public override string? ServerMessage { get; }
    public override int Attempts { get; }

    /// <summary>Model the refused request named (null when unknown).</summary>
    public string? Model { get; }

    public override bool IsContextOverflow => ContextOverflowFromStatus();

    public override string UserMessage => StatusCode switch
    {
        _ when IsContextOverflow => $"transcription trop longue pour le modèle OpenRouter {Model ?? ""} (fenêtre de contexte dépassée). "
               + "Choisissez un modèle à plus grande fenêtre dans Paramètres > Fournisseurs",
        429 => $"limite de l'API OpenRouter atteinte (429) malgré {Attempts} tentatives. Les modèles gratuits ont un quota "
               + "par minute et par jour : attendez puis relancez, ou choisissez un autre modèle dans Paramètres > Fournisseurs",
        401 or 403 => $"clé API OpenRouter refusée (HTTP {StatusCode}). Vérifiez la clé dans Paramètres (openrouter.ai/keys)",
        402 => "crédit OpenRouter insuffisant (HTTP 402). Choisissez un modèle marqué Free ou ajoutez du crédit sur openrouter.ai",
        404 => $"modèle OpenRouter {Model ?? ""} introuvable ou indisponible (HTTP 404). Choisissez un autre modèle dans Paramètres > Fournisseurs",
        0 => $"API OpenRouter injoignable ({ServerMessage ?? "erreur réseau"}) après {Attempts} tentatives. Vérifiez la connexion Internet",
        >= 500 => $"service OpenRouter indisponible (HTTP {StatusCode}) malgré {Attempts} tentatives. Réessayez un peu plus tard",
        _ => $"requête refusée par OpenRouter (HTTP {StatusCode}){(string.IsNullOrWhiteSpace(ServerMessage) ? "" : " : " + ServerMessage)}",
    };
}

/// <summary>
/// Thin REST wrapper over OpenRouter (<c>https://openrouter.ai/api/v1</c>), an OpenAI-compatible
/// router to many hosted models. Chat only: OpenRouter has no audio transcription endpoint, so
/// this client is never used for transcription.
///  - chat: <c>POST /chat/completions</c> (Bearer);
///  - catalogue: <c>GET /models</c>, public (no key needed), with prices and context windows;
///  - key check: <c>GET /key</c>.
/// Same retry-on-transient ladder as the other clients.
/// </summary>
public sealed class OpenRouterClient : IChatClient, IModelCatalog
{
    public const string DefaultBaseUrl = "https://openrouter.ai/api/v1";
    /// <summary>Attribution headers OpenRouter recommends (app ranking); no user data.</summary>
    public const string Referer = "https://github.com/romainio/echo-recorder";
    public const string Title = "Echo";

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

    public OpenRouterClient(HttpClient? http = null, string? baseUrl = null,
        Action<string>? retryLog = null, IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
        _retryLog = retryLog;
        _retryDelays = retryDelays ?? DefaultRetryDelays;
    }

    // ── chat ─────────────────────────────────────────────────────────────────────

    /// <summary>One chat completion. response_format json_object only when
    /// <paramref name="jsonObject"/> (the CR); retried plain when the model refuses the
    /// parameter (400/422 that is not a context overflow), same as Mistral.</summary>
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
        catch (OpenRouterApiException e) when (e.StatusCode is 400 or 422 && !e.IsContextOverflow)
        {
            return await SendChat(apiKey, model, messages, temperature, jsonObject: false, ct);
        }
    }

    private async Task<string> SendChat(string apiKey, string model, IReadOnlyList<ChatMessage> messages,
        double temperature, bool jsonObject, CancellationToken ct)
    {
        var body = await SendWithRetryAsync(
            () => JsonPost($"{_baseUrl}/chat/completions", apiKey, BuildChatPayload(model, messages, temperature, jsonObject)),
            $"chat {model}", model, ct);
        return ExtractChatContent(body, model);
    }

    public static Dictionary<string, object?> BuildChatPayload(string model, IReadOnlyList<ChatMessage> messages, double temperature, bool jsonObject)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["temperature"] = temperature,
            ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
        };
        if (jsonObject) payload["response_format"] = new { type = "json_object" };
        return payload;
    }

    /// <summary>choices[0].message.content. A cut answer (finish_reason "length") or an empty
    /// one (free models sometimes answer 200 with no content) is an
    /// <see cref="LlmEmptyResponseException"/>, never an empty CR.</summary>
    internal static string ExtractChatContent(string body, string model)
    {
        string content = "";
        string? reason = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("choices", out var ch) && ch.ValueKind == JsonValueKind.Array && ch.GetArrayLength() > 0)
            {
                var choice = ch[0];
                if (choice.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object
                    && msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    content = c.GetString() ?? "";
                if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                    reason = fr.GetString();
            }
        }
        catch (JsonException) { /* unreadable: empty below */ }

        if (string.Equals(reason, "length", StringComparison.OrdinalIgnoreCase))
            throw new LlmEmptyResponseException(LlmProvider.OpenRouter, model, truncated: true, reason);
        if (content.Trim().Length == 0)
            throw new LlmEmptyResponseException(LlmProvider.OpenRouter, model, truncated: false,
                string.Equals(reason, "content_filter", StringComparison.OrdinalIgnoreCase) ? "filtre de contenu" : null);
        return content;
    }

    // ── catalogue + probe (IModelCatalog) ───────────────────────────────────────

    /// <summary>GET /models, single attempt. The catalogue is public: <paramref name="apiKey"/>
    /// may be empty. Each model's context_length is remembered for <see cref="ContextBudget"/>.</summary>
    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(string apiKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/models");
        AddHeaders(req, apiKey);
        string body;
        try
        {
            using var resp = await _http.SendAsync(req, ct);
            body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new OpenRouterApiException((int)resp.StatusCode, ExtractServerMessage(body), 1);
        }
        catch (HttpRequestException net) { throw new OpenRouterApiException(0, net.Message, 1); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new OpenRouterApiException(0, "délai dépassé (timeout)", 1); }
        return ParseModelList(body);
    }

    /// <summary>data[]: id, context_length, pricing.prompt / pricing.completion (strings, "0" =
    /// free), architecture.output_modalities. Chat = text-only output (music / image generators
    /// are skipped as chat). Free = both prices are zero, or the id ends with ":free".</summary>
    public static IReadOnlyList<ModelInfo> ParseModelList(string json)
    {
        var list = new Dictionary<string, ModelInfo>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return Array.Empty<ModelInfo>();
        foreach (var m in data.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object) continue;
            var id = m.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? (i.GetString() ?? "").Trim() : "";
            if (id.Length == 0) continue;

            bool chat = true;
            if (m.TryGetProperty("architecture", out var arch) && arch.ValueKind == JsonValueKind.Object
                && arch.TryGetProperty("output_modalities", out var outs) && outs.ValueKind == JsonValueKind.Array)
                chat = outs.EnumerateArray().All(o => o.ValueKind == JsonValueKind.String && o.GetString() == "text");

            bool free = id.EndsWith(":free", StringComparison.OrdinalIgnoreCase);
            if (!free && m.TryGetProperty("pricing", out var pr) && pr.ValueKind == JsonValueKind.Object)
                free = IsZeroPrice(pr, "prompt") && IsZeroPrice(pr, "completion");

            if (m.TryGetProperty("context_length", out var cl) && cl.ValueKind == JsonValueKind.Number && cl.TryGetInt32(out var ctx))
                ContextBudget.RememberOpenRouterWindow(id, ctx);

            list.TryAdd(id, new ModelInfo(id, Chat: chat, Transcribe: false, Free: free));
        }
        return list.Values.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsZeroPrice(JsonElement pricing, string name)
    {
        if (!pricing.TryGetProperty(name, out var v)) return false;
        var s = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
        return decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d == 0m;
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
            using var req = JsonPost($"{_baseUrl}/chat/completions", apiKey, payload);
            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            int status = StatusOf(resp, body);
            bool ok = status is >= 200 and < 300;
            return new ModelProbeResult(ok, status, ok ? null : ExtractServerMessage(body));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new ModelProbeResult(false, 0, ex is TaskCanceledException ? "délai dépassé (timeout)" : ex.Message);
        }
    }

    /// <summary>GET /key: validates the key without spending anything. Never throws.</summary>
    public async Task<ModelProbeResult> ProbeKeyAsync(string apiKey, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/key");
            AddHeaders(req, apiKey);
            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            bool ok = resp.IsSuccessStatusCode;
            return new ModelProbeResult(ok, (int)resp.StatusCode, ok ? null : ExtractServerMessage(body));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new ModelProbeResult(false, 0, ex is TaskCanceledException ? "délai dépassé (timeout)" : ex.Message);
        }
    }

    // ── plumbing ─────────────────────────────────────────────────────────────────

    private static void AddHeaders(HttpRequestMessage req, string? apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Headers.TryAddWithoutValidation("HTTP-Referer", Referer);
        req.Headers.TryAddWithoutValidation("X-Title", Title);
    }

    private static HttpRequestMessage JsonPost(string url, string apiKey, object payload)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        AddHeaders(req, apiKey);
        return req;
    }

    /// <summary>HTTP status, except that a 2xx body carrying only <c>{"error":{"code":N}}</c> (an
    /// upstream failure relayed by OpenRouter) is read as status N (502 when absent).</summary>
    internal static int StatusOf(HttpResponseMessage resp, string body)
    {
        int status = (int)resp.StatusCode;
        if (!resp.IsSuccessStatusCode) return status;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && !root.TryGetProperty("choices", out _) && !root.TryGetProperty("data", out _)
                && root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object)
                return e.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) && n >= 400 ? n : 502;
        }
        catch (JsonException) { /* not JSON: leave it to the content parser */ }
        return status;
    }

    /// <summary>error.message (+ error.metadata.raw when the upstream provider explained itself)
    /// → message → raw, truncated.</summary>
    internal static string? ExtractServerMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Truncate(body);
            if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object
                && e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
            {
                var msg = m.GetString();
                if (e.TryGetProperty("metadata", out var md) && md.ValueKind == JsonValueKind.Object
                    && md.TryGetProperty("raw", out var raw) && raw.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(raw.GetString()))
                    msg += " (" + raw.GetString() + ")";
                return Truncate(msg);
            }
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

    private async Task<string> SendWithRetryAsync(Func<HttpRequestMessage> makeRequest, string opLabel, string model, CancellationToken ct)
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
                int status = StatusOf(resp, body);
                if (status is >= 200 and < 300) return body;

                var serverMsg = ExtractServerMessage(body);
                if (!LlmApiException.IsRetryableStatus(status) || attempt >= _retryDelays.Count)
                    throw new OpenRouterApiException(status, serverMsg, attempt + 1, model);
                delay = RetryDelay(resp, _retryDelays[attempt]);
                why = $"HTTP {status}" + (status == 429 ? " (limite de débit)" : "");
            }
            catch (HttpRequestException net)
            {
                if (attempt >= _retryDelays.Count) throw new OpenRouterApiException(0, net.Message, attempt + 1, model);
                delay = _retryDelays[attempt];
                why = "erreur réseau";
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                if (attempt >= _retryDelays.Count) throw new OpenRouterApiException(0, "délai dépassé (timeout)", attempt + 1, model);
                delay = _retryDelays[attempt];
                why = "délai dépassé";
            }

            _retryLog?.Invoke($"[OpenRouter] {opLabel} : {why}, nouvelle tentative dans {delay.TotalSeconds:0} s "
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
