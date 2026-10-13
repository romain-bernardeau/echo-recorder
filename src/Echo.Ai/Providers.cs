namespace Echo.Ai;

/// <summary>Small helpers for the transcription_provider / cr_provider strings.</summary>
public static class LlmProvider
{
    public const string Mistral = "mistral";
    public const string Gemini = "gemini";
    public const string Cloudflare = "cloudflare";

    /// <summary>Every known provider id, in fallback preference order.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Mistral, Cloudflare };

    public static bool IsGemini(string? provider)
        => string.Equals((provider ?? "").Trim(), Gemini, StringComparison.OrdinalIgnoreCase);

    public static bool IsCloudflare(string? provider)
        => string.Equals((provider ?? "").Trim(), Cloudflare, StringComparison.OrdinalIgnoreCase);

    /// <summary>Canonical lower-case id; anything unknown or blank maps to Mistral (historical default).</summary>
    public static string Normalize(string? provider)
    {
        var p = (provider ?? "").Trim().ToLowerInvariant();
        return p == Cloudflare ? p : Mistral;   // Gemini is retired (free plans only): maps to Mistral
    }

    /// <summary>Human name for toasts / logs.</summary>
    public static string DisplayName(string? provider) => Normalize(provider) switch
    {
        Gemini => "Gemini",
        Cloudflare => "Cloudflare",
        _ => "Mistral",
    };

    /// <summary>Return <paramref name="value"/> if non-blank, else <paramref name="fallback"/>.</summary>
    public static string NonEmpty(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value!.Trim();
}

/// <summary>
/// Chat/completions backend (CR, summary, LLM postprocess). Implemented by both
/// <see cref="MistralClient"/> and <see cref="GeminiClient"/> so the AI components
/// can be pointed at either provider without knowing which one they hold.
/// </summary>
public interface IChatClient
{
    Task<string> ChatCompleteAsync(
        string apiKey, string model, IReadOnlyList<ChatMessage> messages,
        double temperature, bool jsonObject, CancellationToken ct);
}

/// <summary>
/// Transcription backend. Returns a <see cref="VoxtralResult"/> whatever the provider,
/// so the whole diarisation/attribution/output pipeline downstream is provider-agnostic.
/// </summary>
public interface ITranscribeClient
{
    Task<VoxtralResult> TranscribeAsync(
        string apiKey, string model, byte[] audioBytes, string fileName,
        IReadOnlyList<string>? contextBias, IProgress<string>? log, CancellationToken ct);
}

/// <summary>
/// Optional capability of a transcription backend: the caller should split long tracks
/// into pieces of at most <see cref="ChunkSeconds"/> and re-offset the returned
/// timestamps. Whisper on Workers AI has a request-size ceiling and loses timestamp
/// accuracy on long files; Voxtral/Gemini take a whole track and do not implement this.
/// </summary>
public interface IChunkedTranscribe
{
    int ChunkSeconds { get; }
}

/// <summary>One model exposed by a provider's catalogue endpoint. <see cref="Chat"/> =
/// usable for CR / summary / postprocess; <see cref="Transcribe"/> = usable for audio.</summary>
public sealed record ModelInfo(string Id, bool Chat, bool Transcribe);

/// <summary>Result of a one-shot model probe (no retry). <c>Status</c> is 0 when the
/// request never reached the API (network / timeout).</summary>
public sealed record ModelProbeResult(bool Ok, int Status, string? ServerMessage);

/// <summary>
/// Model catalogue + one-shot model probe, implemented by both clients so the Settings
/// UI can list what a given key can actually use (a free-tier Mistral key gets 403 on
/// mistral-large-latest) without knowing the provider.
/// </summary>
public interface IModelCatalog
{
    /// <summary>List the models this key can see. Throws an <see cref="LlmApiException"/> on an
    /// HTTP error; single attempt (interactive call, no backoff).</summary>
    Task<IReadOnlyList<ModelInfo>> ListModelsAsync(string apiKey, CancellationToken ct);

    /// <summary>Send the smallest possible chat completion to <paramref name="model"/> and report
    /// the HTTP status. Never throws: a 403 (tier), 429 (saturated) or 401 (bad key) comes back as data.</summary>
    Task<ModelProbeResult> ProbeChatModelAsync(string apiKey, string model, CancellationToken ct);
}

/// <summary>
/// Base for provider API failures, so orchestration (SessionManager) and the
/// transcription mp3→wav fallback can react to the HTTP status without caring which
/// provider raised it. <see cref="MistralApiException"/> and <see cref="GeminiApiException"/>
/// derive from this; each supplies its own actionable French <see cref="UserMessage"/>.
/// </summary>
public abstract class LlmApiException : Exception
{
    protected LlmApiException(string message) : base(message) { }

    /// <summary>HTTP status; 0 when the request never got an HTTP response (network/timeout).</summary>
    public abstract int StatusCode { get; }
    public abstract string? ServerMessage { get; }
    public abstract int Attempts { get; }

    public virtual bool IsRetryable => IsRetryableStatus(StatusCode);

    /// <summary>True when the provider refused the request because the prompt exceeds the
    /// model's context window. The orchestrator may then switch provider even on a 400.</summary>
    public virtual bool IsContextOverflow => false;

    /// <summary>429 (rate limit) and 5xx (server) are transient; 0 = network, also transient.</summary>
    public static bool IsRetryableStatus(int status) => status == 0 || status == 429 || status >= 500;

    /// <summary>French, user-facing, actionable — shown in toasts/balloons.</summary>
    public abstract string UserMessage { get; }

    /// <summary>Recognises the "prompt too long" wording of the three providers:
    /// Mistral "Prompt contains N tokens ... too large for model with M maximum context length",
    /// Gemini "The input token count (N) exceeds the maximum number of tokens allowed (M)",
    /// Workers AI "... exceeded this model context window limit" / "Input is too long".</summary>
    public static bool LooksLikeContextOverflow(string? serverMessage)
    {
        var m = (serverMessage ?? "").ToLowerInvariant();
        if (m.Length == 0) return false;
        return m.Contains("context length") || m.Contains("context window") || m.Contains("context_length")
            || m.Contains("too large for model") || m.Contains("input token count")
            || m.Contains("exceeds the maximum number of tokens") || m.Contains("maximum context")
            || m.Contains("too many tokens") || m.Contains("input is too long") || m.Contains("prompt is too long");
    }

    /// <summary>Shared rule for the providers' <see cref="IsContextOverflow"/>: a request-level
    /// refusal (400/413/422) whose server message speaks about the context window.</summary>
    protected bool ContextOverflowFromStatus()
        => StatusCode is 400 or 413 or 422 && LooksLikeContextOverflow(ServerMessage);
}

/// <summary>
/// The provider answered 2xx but the answer is unusable: empty content (Gemini safety block,
/// unknown Workers AI response shape) or cut at the output-token ceiling. Raised instead of
/// writing an empty resume.md / CR. Status 502 for an empty answer (another provider, or a
/// later retry, may well succeed), 413 for a truncated one (the answer did not fit).
/// </summary>
public sealed class LlmEmptyResponseException : LlmApiException
{
    public LlmEmptyResponseException(string provider, string? model, bool truncated, string? reason = null)
        : base($"{LlmProvider.DisplayName(provider)} {(truncated ? "truncated" : "empty")} response"
               + $"{(string.IsNullOrWhiteSpace(model) ? "" : " (" + model + ")")}"
               + $"{(string.IsNullOrWhiteSpace(reason) ? "" : ": " + reason)}")
    {
        Provider = LlmProvider.Normalize(provider);
        Model = model;
        Truncated = truncated;
        ServerMessage = reason;
    }

    public string Provider { get; }
    public string? Model { get; }
    /// <summary>True when the answer was cut at the output ceiling (finish_reason "length" /
    /// finishReason MAX_TOKENS); false when it came back empty.</summary>
    public bool Truncated { get; }

    public override int StatusCode => Truncated ? 413 : 502;
    public override string? ServerMessage { get; }
    public override int Attempts => 1;

    public override string UserMessage => Truncated
        ? $"réponse de {LlmProvider.DisplayName(Provider)} tronquée (limite de longueur du modèle {Model ?? ""} atteinte). "
          + "Relancez, ou choisissez un modèle plus grand dans Paramètres > Fournisseurs"
        : $"réponse vide de {LlmProvider.DisplayName(Provider)}"
          + (string.IsNullOrWhiteSpace(ServerMessage) ? "" : $" ({ServerMessage})")
          + ". Relancez, ou changez de fournisseur dans Paramètres > Fournisseurs";
}

/// <summary>A chat or transcription call was served by another model than the one configured
/// (e.g. mistral-large-latest outside the key's tier, retried on mistral-small-latest).</summary>
public sealed record ModelFallback(string Provider, string RequestedModel, string UsedModel, string Reason);

/// <summary>Implemented by clients that may silently switch model; lets the orchestrator tell the
/// user which model actually produced the CR / summary / transcript.</summary>
public interface IModelFallbackSource
{
    /// <summary>Raised once per substitution, on the calling thread, before the retried call.</summary>
    event Action<ModelFallback>? ModelFallback;

    /// <summary>The most recent substitution made by this client (null when none yet).</summary>
    ModelFallback? LastFallback { get; }
}
