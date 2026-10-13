namespace Echo.Core.Engine;

public enum AutoDetectAction { None, OfferStart, AutoStop }

/// <summary>One poll of the Teams auto-detect loop.</summary>
/// <param name="InMeeting">Teams reports an active call right now.</param>
/// <param name="AutoDetect">Setting auto_detect ("offer to record when a call starts").</param>
/// <param name="Recording">A capture is running (arming or recording).</param>
/// <param name="Physical">The running capture is an in-person (mic-only) meeting.</param>
/// <param name="SessionDir">Folder of the running capture (identifies THIS recording).</param>
/// <param name="GraceTicks">Polls without a call before the call counts as ended (≥ 1).</param>
public readonly record struct AutoDetectTick(
    bool InMeeting, bool AutoDetect, bool Recording, bool Physical, string? SessionDir, int GraceTicks);

/// <summary>
/// Pure decision core of the Teams auto-detect / auto-stop loop (port of the Python
/// <c>meeting_monitor</c>, with its <c>was_in</c> rising edge and its
/// <c>rec.recording and not rec.physical</c> guard, both lost in the first .NET port):
/// <list type="bullet">
/// <item>the start is OFFERED only on the rising edge of a call (not in a call → in a call),
///   once per call, and only when auto_detect is on;</item>
/// <item>a manual start or stop during a call marks that call as handled: no offer comes
///   back for it (a Stop in the middle of a call used to re-trigger "Démarrer ?" 5 s later,
///   and auto_record restarted the capture);</item>
/// <item>the AUTO-STOP fires only after the grace period, only for a non-physical session,
///   and only if a Teams call was actually seen in progress during THIS recording: an
///   in-person meeting, or a manual recording with no Teams call, used to be cut after 45 s;</item>
/// <item>a detection that blinks off for less than the grace period changes nothing.</item>
/// </list>
/// Not thread-safe by itself: the owner serialises <see cref="Step"/> and the Mark* calls.
/// </summary>
public sealed class AutoDetectPolicy
{
    /// <summary>Last debounced observation: a call is in progress.</summary>
    public bool WasInMeeting { get; private set; }
    /// <summary>Consecutive polls without a call while <see cref="WasInMeeting"/>.</summary>
    public int Missed { get; private set; }
    /// <summary>The current call was already offered, or started/stopped by hand.</summary>
    public bool CallHandled { get; private set; }
    /// <summary>Session folder of the recording during which a Teams call was seen.</summary>
    public string? TeamsSeenFor { get; private set; }

    public AutoDetectAction Step(AutoDetectTick k)
    {
        if (k.InMeeting)
        {
            Missed = 0;
            if (k.Recording && !k.Physical) TeamsSeenFor = k.SessionDir;
            bool rising = !WasInMeeting;
            WasInMeeting = true;
            if (!rising) return AutoDetectAction.None;
            if (k.Recording) { CallHandled = true; return AutoDetectAction.None; }   // already on it
            if (CallHandled || !k.AutoDetect) return AutoDetectAction.None;
            CallHandled = true;
            return AutoDetectAction.OfferStart;
        }

        if (!WasInMeeting) { Missed = 0; return AutoDetectAction.None; }
        Missed++;
        if (Missed < Math.Max(1, k.GraceTicks)) return AutoDetectAction.None;   // blink, not an end

        // The call really ended.
        WasInMeeting = false;
        Missed = 0;
        CallHandled = false;
        bool seenThisRecording = k.Recording && TeamsSeenFor is not null
                                 && string.Equals(TeamsSeenFor, k.SessionDir, StringComparison.OrdinalIgnoreCase);
        TeamsSeenFor = null;
        return seenThisRecording && !k.Physical ? AutoDetectAction.AutoStop : AutoDetectAction.None;
    }

    /// <summary>The user started or stopped a recording by hand: if a call is in progress, it
    /// is handled (no offer for it any more). Outside a call this is a no-op, so the NEXT call
    /// still gets its offer.</summary>
    public void MarkManual()
    {
        if (WasInMeeting) CallHandled = true;
    }

    /// <summary>Grace period in polls: ceil(grace / poll), at least 1.</summary>
    public static int GraceTicks(int graceSeconds, int pollSeconds)
        => Math.Max(1, (int)Math.Ceiling(Math.Max(0, graceSeconds) / (double)Math.Max(1, pollSeconds)));
}
