using System.Text.Json;
using System.Text.Json.Serialization;

namespace Echo.Core.Models;

/// <summary>
/// Full settings schema, faithful to Python settings.py DEFAULTS
/// (see docs/contract/platform.md §1). Property defaults == the Python DEFAULTS,
/// so a missing key deserialises to the same value. Unknown keys are preserved
/// via <see cref="Extra"/> (mirrors the Python "unknown keys kept" behaviour).
/// Persisted to %APPDATA%\Echo\settings.json (UTF-8, indented, non-ASCII kept).
/// </summary>
public sealed class AppSettings
{
    [JsonPropertyName("auto_detect")] public bool AutoDetect { get; set; } = true;
    [JsonPropertyName("auto_transcribe")] public bool AutoTranscribe { get; set; } = true;
    [JsonPropertyName("auto_minutes")] public bool AutoMinutes { get; set; } = true;
    [JsonPropertyName("auto_stop_grace_s")] public int AutoStopGraceS { get; set; } = 45;
    [JsonPropertyName("recordings_dir")] public string? RecordingsDir { get; set; }
    [JsonPropertyName("capture_mode")] public string CaptureMode { get; set; } = "endpoint";
    [JsonPropertyName("app_lang")] public string AppLang { get; set; } = "fr";
    [JsonPropertyName("key_store")] public string KeyStore { get; set; } = "dpapi";
    [JsonPropertyName("update_url")] public string UpdateUrl { get; set; } = "";
    [JsonPropertyName("update_auto")] public bool UpdateAuto { get; set; } = true;
    [JsonPropertyName("setup_done")] public bool SetupDone { get; set; }
    [JsonPropertyName("autostart")] public bool Autostart { get; set; }
    [JsonPropertyName("cr_lang")] public string CrLang { get; set; } = "auto";
    /// <summary>Spoken language of the meeting, forwarded to the transcription engine:
    /// "auto" (engine detects, default), "fr" or "en". Distinct from the CR language: a
    /// French CR of an English meeting must not force the ASR to decode it as French.</summary>
    [JsonPropertyName("transcription_lang")] public string TranscriptionLang { get; set; } = "auto";
    [JsonPropertyName("cr_type")] public string CrType { get; set; } = "detailed";
    [JsonPropertyName("cr_formats")] public List<string> CrFormats { get; set; } = new() { "eml" };
    [JsonPropertyName("cr_out_lang")] public string CrOutLang { get; set; } = "auto";
    [JsonPropertyName("postprocess_llm")] public bool PostprocessLlm { get; set; } = true;
    [JsonPropertyName("postprocess_model")] public string PostprocessModel { get; set; } = "ministral-8b-latest";

    // Per-task Mistral models (v5.1). A free-tier key gets "403 not available in your
    // subscription tier" on mistral-large-latest, so the CR/summary model must be a
    // user setting rather than a hard-coded constant. Defaults == historical behaviour.
    /// <summary>Mistral chat model for the CR AND the summary (postprocess_model stays the LLM-correction model).</summary>
    [JsonPropertyName("mistral_cr_model")] public string MistralCrModel { get; set; } = "ministral-8b-latest";
    /// <summary>Version of the one-shot model migration already applied (2 = free defaults of 5.6.2).</summary>
    [JsonPropertyName("models_migration")] public int ModelsMigration { get; set; }
    /// <summary>Voxtral model used when transcription_provider = mistral.</summary>
    [JsonPropertyName("mistral_transcribe_model")] public string MistralTranscribeModel { get; set; } = "voxtral-mini-2602";
    /// <summary>Let the orchestrator switch to the other provider when the first one is
    /// saturated (429) or refuses the model (403), provided its key is stored.</summary>
    [JsonPropertyName("auto_fallback_provider")] public bool AutoFallbackProvider { get; set; } = true;

    // AI provider selection (v5.1): switch transcription and/or CR&summary between
    // Mistral (Voxtral + mistral chat) and Google Gemini. Defaults keep the historical
    // Mistral behaviour, so an existing install is unchanged until the user opts in.
    /// <summary>"mistral" (Voxtral) or "gemini" (gemini flash) — engine for transcription.</summary>
    [JsonPropertyName("transcription_provider")] public string TranscriptionProvider { get; set; } = "mistral";
    /// <summary>"mistral", "gemini", "cloudflare" or "openrouter": engine for the CR, the summary and the LLM postprocess.</summary>
    [JsonPropertyName("cr_provider")] public string CrProvider { get; set; } = "mistral";
    /// <summary>Gemini model used when transcription_provider = gemini.</summary>
    [JsonPropertyName("gemini_transcribe_model")] public string GeminiTranscribeModel { get; set; } = "gemini-3.6-flash";
    /// <summary>Gemini model used when cr_provider = gemini (CR + summary + postprocess).</summary>
    [JsonPropertyName("gemini_cr_model")] public string GeminiCrModel { get; set; } = "gemini-3.6-flash";

    // Cloudflare Workers AI (v5.3): Whisper large v3 turbo for transcription, an
    // OpenAI-compatible chat model for the CR. The REST URL carries the ACCOUNT id
    // (https://api.cloudflare.com/client/v4/accounts/{id}/ai/run/{model}); the API
    // token itself lives in the DPAPI secret store, never here.
    /// <summary>Cloudflare account id (32 hex chars, Workers AI dashboard).</summary>
    [JsonPropertyName("cloudflare_account_id")] public string CloudflareAccountId { get; set; } = "";
    /// <summary>Workers AI model used when transcription_provider = cloudflare.</summary>
    [JsonPropertyName("cloudflare_transcribe_model")] public string CloudflareTranscribeModel { get; set; } = "@cf/openai/whisper-large-v3-turbo";
    /// <summary>Workers AI chat model used when cr_provider = cloudflare (CR + summary + postprocess).</summary>
    [JsonPropertyName("cloudflare_cr_model")] public string CloudflareCrModel { get; set; } = "@cf/meta/llama-3.3-70b-instruct-fp8-fast";
    /// <summary>OpenRouter chat model used when cr_provider = openrouter (CR + summary + postprocess).
    /// OpenRouter has no transcription API, so there is no openrouter transcribe model.</summary>
    [JsonPropertyName("openrouter_cr_model")] public string OpenRouterCrModel { get; set; } = EchoConstants.DefaultOpenRouterCrModel;
    [JsonPropertyName("user_name")] public string UserName { get; set; } = "";
    [JsonPropertyName("user_email")] public string UserEmail { get; set; } = "";
    [JsonPropertyName("cr_templates")] public List<CrTemplate> CrTemplates { get; set; } = CrTemplate.Defaults();
    [JsonPropertyName("cr_template")] public string CrTemplateId { get; set; } = "detailed";
    [JsonPropertyName("flags")] public List<Flag> Flags { get; set; } = new();
    [JsonPropertyName("auto_screenshot")] public bool AutoScreenshot { get; set; }
    [JsonPropertyName("capture_button")] public bool CaptureButton { get; set; }
    /// <summary>Screenshot scope: "teams" (crop to the Teams meeting window, default) or
    /// "screen" (the whole desktop, legacy). Falls back to full screen if the window
    /// bounds cannot be resolved.</summary>
    [JsonPropertyName("capture_area")] public string CaptureArea { get; set; } = "teams";
    [JsonPropertyName("capture_button_pos")] public int[]? CaptureButtonPos { get; set; }

    // Disk housekeeping (v2 additions, no Python counterpart).
    /// <summary>Keep the raw per-track WAVs after transcription instead of archiving
    /// them to per-track MP3s (~1 GB/h vs ~55 MB/h). Off by default.</summary>
    [JsonPropertyName("keep_original_wav")] public bool KeepOriginalWav { get; set; }
    /// <summary>Days after which a transcribed session's per-track audio (wav/mp3) is
    /// deleted. Transcripts, CR and the mixdown "… - Audio.mp3" are kept forever.
    /// 0 = never delete.</summary>
    [JsonPropertyName("audio_retention_days")] public int AudioRetentionDays { get; set; }
    /// <summary>Days after which the per-track audio of a session that has NO transcript is
    /// deleted too. These are the failed runs, and they are the heaviest folders on disk
    /// precisely because the sweeper used to refuse to touch them (2026-09-18: 15 GB out of
    /// 20 sat in four untranscribed sessions, one of them a 4-hour orphan recording). The
    /// delay is deliberately long: the audio is the only copy, so it is kept well past the
    /// point where a retry is realistic. 0 = never delete.</summary>
    [JsonPropertyName("failed_audio_retention_days")] public int FailedAudioRetentionDays { get; set; } = 30;

    // UI-only keys: read by the front-end but absent from Python DEFAULTS. Added explicitly.
    [JsonPropertyName("ui_theme")] public string UiTheme { get; set; } = "system"; // light | dark | system
    [JsonPropertyName("auto_record")] public bool AutoRecord { get; set; }          // start without Yes/No dialog

    // Global start/stop hotkey (works even when Echo is in the tray). Combo format
    // "Ctrl+Alt+R": modifiers among Ctrl/Alt/Shift/Win plus one letter/digit/F-key.
    [JsonPropertyName("hotkey_enabled")] public bool HotkeyEnabled { get; set; } = true;
    [JsonPropertyName("hotkey")] public string Hotkey { get; set; } = "Ctrl+Alt+R";

    /// <summary>Last app version whose "what's new" notice the user has seen (UI shows the
    /// notice once per version, on the first launch after an update).</summary>
    [JsonPropertyName("last_seen_version")] public string LastSeenVersion { get; set; } = "";

    /// <summary>Unknown keys present in the JSON file are round-tripped, never dropped.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A CR "style". Two built-ins (Détaillé/Synthèse) are non-deletable.</summary>
public sealed class CrTemplate
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>Base structure: "detailed" (4 sections) | "general" (short).</summary>
    [JsonPropertyName("base")] public string Base { get; set; } = "detailed";
    [JsonPropertyName("instructions")] public string Instructions { get; set; } = "";
    [JsonPropertyName("sample")] public string Sample { get; set; } = "";
    [JsonPropertyName("builtin")] public bool Builtin { get; set; }

    /// <summary>Unknown keys inside a template object are round-tripped, never dropped (audit F10).</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public static List<CrTemplate> Defaults() => new()
    {
        new CrTemplate { Id = "detailed", Name = "Détaillé", Base = "detailed", Builtin = true },
        new CrTemplate { Id = "summary",  Name = "Synthèse", Base = "general",  Builtin = true },
    };
}

/// <summary>A mission/label. Assignment to a session lives in &lt;session&gt;\flag.txt.</summary>
public sealed class Flag
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("color")] public string Color { get; set; } = EchoConstants.DefaultFlagColor;
}
