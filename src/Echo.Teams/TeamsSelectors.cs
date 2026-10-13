using System.Text.Json;

namespace Echo.Teams;

/// <summary>
/// All the brittle Teams UI literals (CSS classes, AutomationIds, title heuristics)
/// live here as configuration, NOT inline in the walk code. The CSS-class selectors
/// (<c>vdi-frame-occlusion</c>, <c>cw-container</c>, <c>lpccommonweb-hovertarget</c>, …)
/// break roughly weekly on Teams updates — this record ships sensible defaults and can
/// be fully overridden from a <c>teams_selectors.json</c> file placed next to the app,
/// so a UI change is a data edit rather than a recompile. See docs/teams-impl-notes.md
/// for the "selectors broke → edit teams_selectors.json" runbook.
/// </summary>
public sealed class TeamsSelectors
{
    /// <summary>Process image names (case-insensitive) that host a Teams window.</summary>
    public string[] TeamsProcessNames { get; set; } = { "ms-teams.exe", "teams.exe" };

    /// <summary>
    /// AutomationId candidates for the "Leave / Quitter" hangup button. The presence of
    /// any of these in the tree is the authoritative "a call is in progress" signal.
    /// UIA uses <c>hangup-button</c>; the DOM variants are kept as fallbacks.
    /// </summary>
    public string[] HangupAutomationIds { get; set; } = { "hangup-button", "hangup-leave-button", "hangup-main-btn" };

    /// <summary>Folded window-title substrings that mark a "hub" tab (not a meeting window).</summary>
    public string[] NonMeetingTitleHints { get; set; } =
    {
        "calendar", "calendrier", "chat", "activity", "activite",
        "conversation", "fichiers", "files", "teams et chaines", "communautes",
    };

    /// <summary>Folded title segments too generic to be a real meeting subject.
    /// "affichage compact de reunion" / "compact meeting view" is the accessible NAME of
    /// Teams' compact meeting window (the small floating view). It is NOT a subject: taken
    /// literally it was saved as the meeting name (seen live 2026-07-16). Reject it so the
    /// title falls back to the real subject read from another Teams window, or the date name.</summary>
    public string[] GenericTitles { get; set; } =
    {
        "microsoft teams", "teams", "meeting", "reunion", "meet now",
        "reunion maintenant", "nouvelle reunion", "new meeting", "appel", "call",
        "affichage compact de reunion", "affichage compact", "compact meeting view",
        "compact view", "reunion en cours", "meeting in progress",
        // Join / pre-meeting window states (seen live 2026-07-16: the whole title was
        // "Meeting join | Reunion Microsoft Teams | <tenant> | <email> | Microsoft Teams").
        "meeting join", "join meeting", "rejoindre la reunion", "rejoindre une reunion",
        "reunion microsoft teams", "microsoft teams reunion", "pre-join", "prejoin", "lobby",
    };

    /// <summary>
    /// Folded title substrings identifying the COMPACT meeting window (the small floating
    /// tile Teams shows when the user navigates away; no opt-out setting exists). Its UIA
    /// tree carries neither participant tiles nor name pills, so it must never be preferred
    /// over a real meeting window, and its presence triggers the "enlarge the window" alert.
    /// </summary>
    public string[] CompactViewTitleHints { get; set; } =
    {
        "affichage compact", "compact meeting view", "compact view",
    };

    /// <summary>
    /// Outlook/Teams decorations stripped from the FRONT of a detected subject (repeatedly,
    /// case-insensitive). Example: "Meeting request - Atlas roadmap
    /// review" — the real subject starts after the "Meeting request - " chrome.
    /// Accented and unaccented variants are listed explicitly (comparison is ordinal).
    /// </summary>
    public string[] SubjectStripPrefixes { get; set; } =
    {
        "meeting request -", "meeting request :", "demande de réunion -", "demande de reunion -",
        "demande de réunion :", "demande de reunion :", "invitation -", "invitation :",
        "réunion :", "reunion :", "tr:", "tr :", "re:", "re :", "fw:", "fw :", "fwd:", "fwd :",
    };

    /// <summary>ClassName substring (lower-cased) marking an active-speaker ring tile.</summary>
    public string ActiveSpeakerRingClass { get; set; } = "vdi-frame-occlusion";

    /// <summary>All active-speaker markers (used by the health-check / dump diagnostic).</summary>
    public string[] ActiveSpeakerMarkers { get; set; } = { "vdi-frame-occlusion", "voice-level-stream-outline" };

    /// <summary>Reserved aria-label fallback hints for speaking state (not used by the live detector).</summary>
    public string[] SpeakingHints { get; set; } = { "en train de parler", "is speaking", "active speaker" };

    /// <summary>Parent-ClassName substrings (lower) marking the name-pill container of a participant tile.</summary>
    public string[] NameContainerHints { get; set; } = { "cw-container", "lpccommonweb-hovertarget" };

    /// <summary>
    /// How many control-view ancestor levels (starting at the immediate parent) to search for a
    /// <see cref="NameContainerHints"/> ClassName match when joining a name TextControl to its tile.
    /// Python checks only the immediate parent (=1); the default 3 tolerates extra wrapper nodes
    /// in the Teams/Chromium control view without matching unrelated text (audit F1).
    /// </summary>
    public int NameContainerAncestorLevels { get; set; } = 3;

    /// <summary>
    /// UIA tree view to walk: "raw" (default) or "control".
    /// MUST stay "raw" to match the Python `uiautomation` lib, whose ViewWalker is
    /// literally `IUIAutomation.RawViewWalker` (the ControlViewWalker line is commented
    /// out in the lib's source). Verified live 2026-07-13: on the same running meeting,
    /// the ControlView exposed a pruned Teams tree (~100 nodes, tiles empty ⇒
    /// speakers/roster EMPTY) while the raw view exposed the ring tiles and name labels.
    /// </summary>
    public string TreeView { get; set; } = "raw";

    /// <summary>Lower-cased filename substrings hinting a native Teams transcript .docx.</summary>
    public string[] TranscriptFilenameHints { get; set; } = { "transcription", "transcript", "teams" };

    /// <summary>Max DFS depth when walking the Teams tree (matches the Python detector).</summary>
    public int MaxDepth { get; set; } = 45;

    /// <summary>Max node count before the DFS stops descending (matches the Python detector).</summary>
    public int NodeCap { get; set; } = 25000;

    public const string FileName = "teams_selectors.json";

    /// <summary>
    /// Loads selector overrides from <c>teams_selectors.json</c> in <paramref name="baseDir"/>
    /// (defaults to the app base directory). Any key present overrides the default; any key
    /// missing keeps its built-in default. On any error the defaults are used and a warning
    /// is logged, so a malformed override can never take Teams awareness offline.
    /// </summary>
    public static TeamsSelectors Load(string? baseDir = null, Action<string>? log = null)
    {
        baseDir ??= AppContext.BaseDirectory;
        var path = Path.Combine(baseDir, FileName);
        if (!File.Exists(path))
            return new TeamsSelectors();

        try
        {
            var json = File.ReadAllText(path);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
            var loaded = JsonSerializer.Deserialize<TeamsSelectors>(json, options);
            if (loaded != null)
            {
                log?.Invoke($"[Echo.Teams] Loaded selector overrides from {path}");
                return loaded;
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"[Echo.Teams] Failed to read {path} ({ex.Message}); using built-in defaults.");
        }

        return new TeamsSelectors();
    }
}
