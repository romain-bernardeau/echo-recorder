namespace Echo.Core;

/// <summary>
/// App-wide constants. Values marked "MUST NOT CHANGE" are load-bearing for
/// backward compatibility with data written by the Python version (Echo 4.2).
/// </summary>
public static class EchoConstants
{
    public const string AppName = "Echo";

    /// <summary>.NET rewrite line. Kept &gt; the Python 4.2.0 so update logic treats it as newer.</summary>
    public const string AppVersion = "5.8.1";

    /// <summary>Where every install looks for updates: the version.json asset of the latest
    /// GitHub release (public, no sign-in). Its relative zip url resolves next to it.</summary>
    public const string DefaultUpdateUrl = "https://github.com/romainio/echo-recorder/releases/latest/download/version.json";

    /// <summary>Online help: Markdown pages in the public repository (README.md, README.fr.md,
    /// README.es.md), opened in the system browser by the Help buttons.</summary>
    public const string HelpBaseUrl = "https://github.com/romainio/echo-recorder/blob/main/help/";

    public const string EnvApiKey = "MISTRAL_API_KEY";
    /// <summary>Env var for the Google AI Studio (Gemini) key, when the Gemini provider is used.</summary>
    public const string EnvGoogleApiKey = "GOOGLE_API_KEY";
    /// <summary>Env var for the Cloudflare Workers AI API token, when the Cloudflare provider is used.</summary>
    public const string EnvCloudflareApiToken = "CLOUDFLARE_API_TOKEN";
    /// <summary>Env var for the OpenRouter API key, when the OpenRouter provider is used.</summary>
    public const string EnvOpenRouterApiKey = "OPENROUTER_API_KEY";
    public const string SingleInstanceMutex = "EchoWebviewSingletonMutex";

    // --- DPAPI (secret_store.py) — MUST stay byte-identical or existing secret.bin fails to decrypt ---
    /// <summary>Optional entropy passed to CryptProtectData/CryptUnprotectData. Literally ASCII "Voxra" (5 bytes).</summary>
    public static readonly byte[] DpapiEntropy = "Voxra"u8.ToArray();
    /// <summary>Informational description string (not used by .NET ProtectedData, kept for reference).</summary>
    public const string DpapiDescription = "Voxra Mistral key";
    public const string KeyringService = "Voxra";
    public const string KeyringUser = "mistral_api_key";

    // --- Timing / behaviour constants (see docs/contract/platform.md appendix) ---
    public const int MeetingMonitorPollSeconds = 5;
    public const int DefaultAutoStopGraceSeconds = 45;
    public const double CaptureChangeThreshold = 12.0;
    public const double CaptureMinIntervalSeconds = 4.0;
    public const double CapturePollSeconds = 2.0;
    public const string DefaultFlagColor = "#0D2F4B"; // navy

    // --- Models ---
    public const string DefaultPostprocessModel = "ministral-8b-latest";

    // --- Gemini (Google AI Studio) defaults ---
    /// <summary>Flash model used for Gemini transcription (returns JSON segments parsed
    /// by VoxtralResponseParser). gemini-3.6-flash is the proven-reachable default;
    /// overridable via settings (gemini_transcribe_model).</summary>
    public const string DefaultGeminiTranscribeModel = "gemini-3.6-flash";
    /// <summary>Flash model used for the Gemini CR / summary / postprocess text calls.</summary>
    public const string DefaultGeminiCrModel = "gemini-3.6-flash";
    /// <summary>Provider identifiers for the transcription_provider / cr_provider settings.</summary>
    public const string ProviderMistral = "mistral";
    public const string ProviderGemini = "gemini";
    public const string ProviderCloudflare = "cloudflare";
    public const string ProviderOpenRouter = "openrouter";

    // --- Cloudflare Workers AI defaults ---
    /// <summary>Whisper large v3 turbo on Workers AI: segment timestamps, no diarisation,
    /// about 0.0005 USD per audio minute. The audio is sent in ~10 min chunks (see Transcriber).</summary>
    public const string DefaultCloudflareTranscribeModel = "@cf/openai/whisper-large-v3-turbo";
    /// <summary>Chat model for CR / summary / postprocess when cr_provider = cloudflare
    /// (OpenAI-compatible /ai/v1/chat/completions endpoint).</summary>
    public const string DefaultCloudflareCrModel = "@cf/meta/llama-3.3-70b-instruct-fp8-fast";

    // --- OpenRouter defaults (chat only: CR, summary, postprocess) ---
    /// <summary>Free model (pricing 0/0 in the public catalogue on 2026-10-13): Gemma 4 31B
    /// instruct is a dense multilingual instruct model with a 262 k context window, accepts
    /// response_format (JSON minutes) and does not reason by default, so the answer is not
    /// eaten by hidden thinking tokens. No free Llama 3.3 70B, Mistral or DeepSeek variant was
    /// listed that day.</summary>
    public const string DefaultOpenRouterCrModel = "google/gemma-4-31b-it:free";

    // --- Legacy folder name (one-time Voxra -> Echo migration) ---
    public const string LegacyAppName = "Voxra";
}
