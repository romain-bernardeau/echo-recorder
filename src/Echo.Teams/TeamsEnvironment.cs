namespace Echo.Teams;

/// <summary>
/// The "new Teams" (WebView2 / Chromium) only exposes its full accessibility tree when
/// forced on via a user environment variable. Nothing in this project works
/// (speaker names and roster come back empty) unless
/// <c>WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--force-renderer-accessibility</c> is set
/// AND Teams has been fully quit (systray → Quit) and relaunched.
/// </summary>
public static class TeamsEnvironment
{
    public const string FlagVar = "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS";
    public const string FlagVal = "--force-renderer-accessibility";

    /// <summary>True when the current process environment carries the accessibility flag.</summary>
    public static bool IsAccessibilityFlagSet()
    {
        var v = Environment.GetEnvironmentVariable(FlagVar, EnvironmentVariableTarget.Process) ?? "";
        return v.Contains(FlagVal, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Persists the accessibility flag as a USER env var (no admin required) and mirrors it
    /// into the current process. The effect only applies after Teams is fully quit and
    /// relaunched — the caller MUST prompt the user to restart Teams. Existing content of the
    /// variable is preserved (the flag is appended if not already present).
    /// </summary>
    public static void SetAccessibilityFlag()
    {
        var existing = Environment.GetEnvironmentVariable(FlagVar, EnvironmentVariableTarget.User) ?? "";
        string value = existing.Contains(FlagVal, StringComparison.OrdinalIgnoreCase)
            ? existing
            : (existing.Length > 0 ? existing + " " + FlagVal : FlagVal);

        Environment.SetEnvironmentVariable(FlagVar, value, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable(FlagVar, value, EnvironmentVariableTarget.Process);
    }
}

/// <summary>Result of a Teams UIA self-test (analogous to <c>teams_speaker.py --dump</c>).</summary>
/// <param name="MeetingWindowFound">A non-hub meeting window was located.</param>
/// <param name="NodeCount">Descendant nodes visited in the meeting window.</param>
/// <param name="HangupButtonFound">≥1 hangup AutomationId present (a call is in progress).</param>
/// <param name="SpeakerRingCount">Count of active-speaker ring tiles found.</param>
/// <param name="NamePillCount">Count of participant name-pill labels found.</param>
/// <param name="TreeLooksEmpty">The tree is suspiciously empty — likely the accessibility flag is missing.</param>
public sealed record TeamsHealth(
    bool MeetingWindowFound,
    int NodeCount,
    bool HangupButtonFound,
    int SpeakerRingCount,
    int NamePillCount,
    bool TreeLooksEmpty);
