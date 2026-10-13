using Echo.Core.Models;

namespace Echo.Core.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
//  Audio (implemented in Echo.Audio) — see docs/contract/audio.md
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Captures the meeting into mic.wav (+ system.wav for non-physical) in a session
/// folder, using a shared <see cref="CaptureClock"/> for alignment. The system track
/// uses WASAPI process-/endpoint-loopback; the mic track uses a WASAPI capture client.
/// </summary>
public interface IAudioCaptureService : IAsyncDisposable
{
    /// <summary>Start recording. physical => microphone only (no system loopback).</summary>
    Task StartAsync(string sessionDir, bool physical, CaptureClock clock, CancellationToken ct);

    /// <summary>Stop, flush WAVs, return per-track results (with any error).</summary>
    Task<IReadOnlyList<TrackResult>> StopAsync();

    /// <summary>Instantaneous RMS levels (0..1) for the live meters.</summary>
    (double Mic, double System) GetLevels();

    /// <summary>Raised with the track label ("mic"/"system") when a track dies and is restarted.</summary>
    event EventHandler<string>? TrackFailed;

    /// <summary>Live capture health for the UI banner: "" (healthy / unknown),
    /// "loopback_dead" (system track delivers nothing), "no_mic" (mic track dead or absent),
    /// "silent_teams" (system track alive but inaudible for a long time). Codes match the
    /// warn_* i18n keys. Cheap to read (polled by get_state).</summary>
    string HealthCode => "";
}

// ─────────────────────────────────────────────────────────────────────────────
//  Teams (implemented in Echo.Teams) — see docs/contract/teams.md
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>UI-Automation driven Teams awareness: detection, speaker watch, roster, PIDs.</summary>
public interface ITeamsMonitor
{
    /// <summary>True when a call is active (hangup-button present in the Teams UIA tree).</summary>
    bool InMeeting();

    /// <summary>Read the current meeting subject + roster (blocking up to <paramref name="timeout"/>).</summary>
    MeetingInfo ReadMeetingInfo(TimeSpan timeout);

    /// <summary>
    /// Continuously write speakers.jsonl and roster.jsonl into <paramref name="sessionDir"/>
    /// until cancelled. Timestamps are wall-clock epoch (aligned via the clock).
    /// </summary>
    Task WatchAsync(string sessionDir, CaptureClock clock, CancellationToken ct);

    /// <summary>PIDs of Teams processes (for the screenshot foreground privacy gate).</summary>
    IReadOnlyList<int> GetTeamsPids();
}

/// <summary>Parses a native Teams transcript export (.docx / .vtt) into normalized turns.</summary>
public interface ITeamsTranscriptImporter
{
    bool CanImport(string path);
    IReadOnlyList<TranscriptTurn> Import(string path);
}

// ─────────────────────────────────────────────────────────────────────────────
//  AI pipeline (implemented in Echo.Ai) — see docs/contract/ai.md
// ─────────────────────────────────────────────────────────────────────────────

public sealed record TranscriptionRequest(
    string SessionDir,
    string ApiKey,
    AppSettings Settings,
    IReadOnlyList<string> ContextBias,
    /// <summary>Key for the LLM postprocess pass (CR provider), when it differs from the
    /// transcription key (e.g. transcription = Gemini but CR = Mistral). Defaults to ApiKey.</summary>
    string? ChatApiKey = null);

public sealed record TranscriptionResult(
    string RawTranscriptPath,
    string ProcessedTranscriptPath,
    IReadOnlyList<TranscriptTurn> Turns);

public sealed record MinutesRequest(
    string SessionDir,
    string ApiKey,
    AppSettings Settings,
    CrTemplate Template,
    string? OutLangOverride = null);

public sealed record MinutesResult(IReadOnlyList<string> OutputFiles);

/// <summary>Transcribes the session's tracks with Voxtral and merges diarisation.</summary>
public interface ITranscriber
{
    Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, IProgress<string>? log, CancellationToken ct);
}

/// <summary>Generates the meeting minutes (CR) in the configured formats via Mistral.</summary>
public interface IMinutesGenerator
{
    Task<MinutesResult> GenerateAsync(MinutesRequest request, IProgress<string>? log, CancellationToken ct);
}

/// <summary>Ultra-short summary of a session's transcript → resume.md.</summary>
public interface ISummarizer
{
    Task<string> SummarizeAsync(string sessionDir, string apiKey, AppSettings settings, CancellationToken ct);
}

/// <summary>Domain vocabulary: context-bias tokens for Voxtral + deterministic canonicalisation.</summary>
public interface IVocabulary
{
    /// <summary>Bias tokens (matching ^[^,\s]+$, ≤100, mission-first) for the Voxtral request.</summary>
    IReadOnlyList<string> BuildContextBias(AppSettings settings, string? missionHint = null);

    /// <summary>Deterministic lexicon canonicalisation of one text block.</summary>
    string CanonicalizeDeterministic(string text);
}

// ─────────────────────────────────────────────────────────────────────────────
//  Platform storage (implemented in Echo.Core)
// ─────────────────────────────────────────────────────────────────────────────

public interface ISettingsStore
{
    AppSettings Current { get; }
    AppSettings Load();
    void Save(AppSettings settings);
    /// <summary>Set one key by its JSON name (Api.set_setting) and persist.</summary>
    void Set(string jsonKey, object? value);
    /// <summary>Mutate a CLONE of the current settings under the store lock, then persist it.
    /// Use this instead of mutating <see cref="Current"/> in place (concurrent bridge calls).</summary>
    void Update(Action<AppSettings> mutate);
}

public interface ISecretStore
{
    /// <summary>Resolve the Mistral key from env or the configured backend; null if none.</summary>
    string? ResolveKey();
    bool KeyPresent();
    /// <summary>Store via DPAPI, clear other backends, set key_store=dpapi, export to env.</summary>
    void Save(string key);
    void Clear();

    /// <summary>Resolve the Google AI Studio (Gemini) key from env or DPAPI; null if none.</summary>
    string? ResolveGoogleKey();
    bool GoogleKeyPresent();
    /// <summary>Store the Google key via DPAPI (separate file) and export it to the process env.</summary>
    void SaveGoogle(string key);
    void ClearGoogle();

    /// <summary>Resolve the Cloudflare Workers AI token from env or DPAPI; null if none.</summary>
    string? ResolveCloudflareKey();
    bool CloudflareKeyPresent();
    void SaveCloudflare(string key);
    void ClearCloudflare();

    /// <summary>Resolve the OpenRouter API key from env or DPAPI; null if none.</summary>
    string? ResolveOpenRouterKey();
    bool OpenRouterKeyPresent();
    void SaveOpenRouter(string key);
    void ClearOpenRouter();

    /// <summary>Resolve the key of any provider id ("mistral" | "gemini" | "cloudflare" | "openrouter"); null if none / unknown.</summary>
    string? ResolveKeyFor(string? provider);
    bool KeyPresentFor(string? provider);
}

/// <summary>Global learned address book: Teams display name → e-mail (contacts.json).</summary>
public interface IContactsStore
{
    IReadOnlyDictionary<string, string> All();
    string? Get(string displayName);
    void Set(string displayName, string? email);
}

/// <summary>Delivers user notifications (in-app toast when visible, else silent tray balloon).</summary>
public interface INotifier
{
    void Notify(string message);
}
