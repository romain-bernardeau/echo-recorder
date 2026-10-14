using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Echo.Core.Abstractions;
using Echo.Core.Models;
using Echo.Core.Storage;
using Echo.Teams.Internal;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace Echo.Teams;

/// <summary>
/// UI-Automation driven Teams awareness (bot-free): meeting detection, meeting subject +
/// roster, active-speaker watch, and Teams PIDs. Faithful C# port of
/// <c>teams_speaker.py</c> using FlaUI (UIA3) with per-thread MTA COM init.
///
/// Prerequisite: <c>WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--force-renderer-accessibility</c>
/// must be set and Teams restarted, otherwise the accessibility tree is nearly empty
/// (see <see cref="TeamsEnvironment"/> and docs/teams-impl-notes.md).
/// </summary>
public sealed class TeamsMonitor : ITeamsMonitor, IDisposable
{
    private readonly TeamsSelectors _sel;
    private readonly Action<string>? _log;
    private readonly UiaWorker _worker;

    private readonly HashSet<string> _genericFolded;
    private readonly List<string> _hubHintsFolded;
    private readonly List<string> _compactFolded;

    /// <summary>
    /// User-facing alert (French, like the Echo.Audio alerts): fired at most once per watch
    /// session when the meeting stays in COMPACT view with no readable participants, so the
    /// user can enlarge the window (there is no Teams setting to disable compact view).
    /// Wired to the tray notifier by the composition root.
    /// </summary>
    public event Action<string>? Alert;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        // ensure_ascii=False equivalent: keep accented names literal.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public TeamsMonitor(TeamsSelectors? selectors = null, Action<string>? log = null)
    {
        _sel = selectors ?? TeamsSelectors.Load(log: log);
        _log = log;
        _worker = new UiaWorker(log);

        _genericFolded = _sel.GenericTitles.Select(Fold).ToHashSet(StringComparer.Ordinal);
        _hubHintsFolded = _sel.NonMeetingTitleHints.Select(Fold).ToList();
        _compactFolded = _sel.CompactViewTitleHints.Select(Fold).ToList();

        if (!TeamsEnvironment.IsAccessibilityFlagSet())
        {
            _log?.Invoke(
                "[Echo.Teams] WARNING: WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--force-renderer-accessibility " +
                "is not set in this process. Teams speaker/roster reads may be empty until it is set and Teams restarted.");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  ITeamsMonitor
    // ─────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public bool InMeeting()
    {
        try
        {
            return _worker.Invoke(a =>
            {
                var pids = TeamsPidSet();
                foreach (var w in FindTeamsWindows(a, pids))
                {
                    foreach (var id in _sel.HangupAutomationIds)
                    {
                        if (Uia.FindFirstByAutomationId(w, id) != null)
                            return true;
                    }
                }
                return false;
            });
        }
        catch (Exception ex)
        {
            // A UIA read failure is "undecided" — the orchestrator must not count this as an
            // absence. We return false but the debounced stop logic upstream tolerates flicker.
            _log?.Invoke($"[Echo.Teams] InMeeting read failed (undecided): {ex.Message}");
            return false;
        }
    }

    /// <inheritdoc/>
    public MeetingInfo ReadMeetingInfo(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        string title = "";

        // The subject appears in the window title ~1s AFTER the call starts, so poll the
        // (cheap) window Name until a non-generic subject appears or the timeout elapses.
        while (true)
        {
            try
            {
                var found = _worker.Invoke(a =>
                {
                    var pids = TeamsPidSet();
                    return SubjectFromWindows(FindTeamsWindows(a, pids));
                });
                if (!string.IsNullOrEmpty(found))
                {
                    title = found!;
                    break;
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Echo.Teams] ReadMeetingInfo title poll failed: {ex.Message}");
            }

            if (DateTime.UtcNow >= deadline) break;
            Thread.Sleep(250);
        }

        // One roster + call-state snapshot at the end.
        (IReadOnlyList<string> roster, bool inCall) = SnapshotRosterAndCall();
        return new MeetingInfo(title, roster, inCall);
    }

    private (IReadOnlyList<string> Roster, bool InCall) SnapshotRosterAndCall()
    {
        try
        {
            return _worker.Invoke(a =>
            {
                var pids = TeamsPidSet();
                var wins = FindTeamsWindows(a, pids);
                var window = PickMeetingWindow(wins);

                bool inCall = false;
                foreach (var w in wins)
                {
                    foreach (var id in _sel.HangupAutomationIds)
                    {
                        if (Uia.FindFirstByAutomationId(w, id) != null) { inCall = true; break; }
                    }
                    if (inCall) break;
                }

                IReadOnlyList<string> roster = window is null
                    ? Array.Empty<string>()
                    : RosterFromWindow(window, WalkerFor(a));

                return (roster, inCall);
            });
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[Echo.Teams] Roster/call snapshot failed: {ex.Message}");
            return (Array.Empty<string>(), false);
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<int> GetTeamsPids() => TeamsPidSet().ToList();

    /// <inheritdoc/>
    public Task WatchAsync(string sessionDir, CaptureClock clock, CancellationToken ct)
    {
        // Dedicated MTA thread with its own UIA3Automation (one automation per thread).
        var tcs = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            UIA3Automation? automation = null;
            try
            {
                automation = new UIA3Automation();
                WatchLoop(automation, sessionDir, ct);
                tcs.TrySetResult();
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Echo.Teams] Watch loop failed: {ex.Message}");
                tcs.TrySetResult(); // never fault the caller — audio recording is independent
            }
            finally
            {
                try { automation?.Dispose(); } catch { /* ignore */ }
            }
        })
        {
            IsBackground = true,
            Name = "Echo.Teams.Watch",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return tcs.Task;
    }

    private void WatchLoop(UIA3Automation automation, string sessionDir, CancellationToken ct)
    {
        Directory.CreateDirectory(sessionDir);
        // Walker for this thread's automation, reused for every tick (raw view = Python parity).
        var walker = WalkerFor(automation);
        string speakersPath = SessionLayout.ForWrite(sessionDir, SessionLayout.Speakers);
        string rosterPath = SessionLayout.ForWrite(sessionDir, SessionLayout.Roster);
        string titlePath = SessionLayout.ForWrite(sessionDir, SessionLayout.MeetingTitle);

        // interval 0.5s ⇒ roster snapshot every 20 ticks (~10 s), matching the Python cadence.
        const double interval = 0.5;
        int rosterEvery = Math.Max(1, (int)Math.Round(20 * 0.5 / Math.Max(0.1, interval)));

        var rosterSeen = new SortedSet<string>(StringComparer.Ordinal);
        int pollI = 0;
        bool titleDone = false;
        double lastWarn = 0;
        // Compact-view handling: while NO name has been read yet, re-pick the window every
        // ~10 s (the user may enlarge the compact tile mid-meeting: the real meeting window
        // then reappears and must take over). If the pick stays compact ~15 s with nothing
        // readable, alert the user ONCE to enlarge it (no Teams setting disables compact view).
        bool namesSeen = false, compactAlerted = false;
        int sinceRepick = 0, compactTicks = 0;
        string lastAcquiredName = "";

        using var speakersW = new StreamWriter(
            new FileStream(speakersPath, FileMode.Create, FileAccess.Write, FileShare.Read), Utf8NoBom) { AutoFlush = true };
        using var rosterW = new StreamWriter(
            new FileStream(rosterPath, FileMode.Create, FileAccess.Write, FileShare.Read), Utf8NoBom) { AutoFlush = true };

        AutomationElement? window = null;

        while (!ct.IsCancellationRequested)
        {
            long loopStart = Stopwatch.GetTimestamp();

            if (window is null)
            {
                var pids = TeamsPidSet();
                window = PickMeetingWindow(FindTeamsWindows(automation, pids));
                if (window != null)
                {
                    // Log only when the pick CHANGES: the nameless re-pick below re-acquires
                    // every ~10 s and would otherwise spam the log with identical lines.
                    var name = Uia.Name(window);
                    if (name != lastAcquiredName)
                    {
                        lastAcquiredName = name;
                        _log?.Invoke($"[Echo.Teams] Meeting window acquired: {name}");
                    }
                }
            }

            double t = EpochNow();
            List<string> speakers = new();
            List<object> detail = new();

            if (window != null)
            {
                try
                {
                    (speakers, detail) = DetectActiveSpeakers(window, walker);
                    if (speakers.Count > 0) namesSeen = true;

                    if (IsCompactWindow(window))
                    {
                        compactTicks++;
                        if (!compactAlerted && !namesSeen && compactTicks >= 30)   // ~15 s
                        {
                            compactAlerted = true;
                            Alert?.Invoke("Réunion Teams en affichage compact : Echo ne voit ni les participants "
                                          + "ni qui parle. Cliquez sur la vignette de réunion pour agrandir la fenêtre.");
                        }
                    }
                    else compactTicks = 0;

                    // No name read since the start: re-pick regularly so a window enlarged
                    // mid-meeting (or opened late) takes over from the compact/wrong one.
                    if (!namesSeen && rosterSeen.Count == 0 && ++sinceRepick >= rosterEvery)
                    {
                        sinceRepick = 0;
                        window = null;
                    }
                }
                catch (Exception ex)
                {
                    // Window closed/recreated -> re-acquire next tick (exactly like Python watch()).
                    _log?.Invoke($"[Echo.Teams] Read failed, re-acquiring window: {ex.Message}");
                    window = null;
                }
            }
            else
            {
                if (t - lastWarn > 5)
                {
                    _log?.Invoke(
                        "[Echo.Teams] Teams meeting window not found (in a call?). If it never appears, verify " +
                        "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--force-renderer-accessibility and restart Teams. Audio keeps recording.");
                    lastWarn = t;
                }
            }

            double pollMs = Math.Round(ElapsedMs(loopStart), 1);
            var line = JsonSerializer.Serialize(new
            {
                t,
                speakers,
                detail,
                poll_ms = pollMs,
            }, JsonOpts);
            speakersW.Write(line);
            speakersW.Write('\n');

            // First non-generic subject → meeting_title.txt (Teams exposes it ~1s late).
            // Scan ALL Teams windows, not just the tracked meeting window: in compact
            // meeting view the tracked window is named "Affichage compact de reunion"
            // (now folded as generic), while the MAIN Teams window title bar still reads
            // "<Subject> | Microsoft Teams". Reading every window's title recovers the subject.
            if (!titleDone)
            {
                try
                {
                    var pids = TeamsPidSet();
                    var wins = FindTeamsWindows(automation, pids);
                    var cand = SubjectFromWindows(wins);
                    if (!string.IsNullOrEmpty(cand))
                    {
                        File.WriteAllText(titlePath, cand, Utf8NoBom);
                        titleDone = true;
                        _log?.Invoke($"[Echo.Teams] Meeting subject detected: {cand}");
                    }
                }
                catch { /* ignore */ }
            }

            // Cumulative roster snapshot every ~10 s; append only when new names appear.
            if (window != null)
            {
                pollI++;
                if (pollI % rosterEvery == 0)
                {
                    List<string> all;
                    try { all = RosterFromWindow(window, walker); }
                    catch { all = new List<string>(); }

                    var fresh = all.Where(n => !rosterSeen.Contains(n)).ToList();
                    if (fresh.Count > 0)
                    {
                        foreach (var n in fresh) rosterSeen.Add(n);
                        var rline = JsonSerializer.Serialize(new
                        {
                            t,
                            roster = rosterSeen.ToArray(),
                        }, JsonOpts);
                        rosterW.Write(rline);
                        rosterW.Write('\n');
                    }
                }
            }

            double sleepLeft = interval - (ElapsedMs(loopStart) / 1000.0);
            if (sleepLeft > 0)
            {
                // Interruptible sleep: returns immediately when cancelled.
                if (ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(sleepLeft))) break;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Health-check (self-test analogous to `teams_speaker.py --dump`)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The tree walker used for EVERY Teams walk. Defaults to the RAW view because the
    /// Python `uiautomation` lib walks `IUIAutomation.RawViewWalker` (its ControlViewWalker
    /// line is commented out in the lib source) — with the ControlView, Chromium exposes a
    /// pruned tree where participant tiles are empty and speakers/roster come back EMPTY
    /// (observed live 2026-07-13). Override via "TreeView" in teams_selectors.json.
    /// </summary>
    private FlaUI.Core.ITreeWalker WalkerFor(FlaUI.Core.AutomationBase a) =>
        string.Equals(_sel.TreeView, "control", StringComparison.OrdinalIgnoreCase)
            ? a.TreeWalkerFactory.GetControlViewWalker()
            : a.TreeWalkerFactory.GetRawViewWalker();

    /// <summary>
    /// Walks the meeting window and reports whether the key selectors still resolve
    /// (hangup button, speaker ring, name pills). Surfaces a clear "Teams layout changed /
    /// accessibility flag missing" warning rather than silently returning empty speakers.
    /// </summary>
    public TeamsHealth HealthCheck()
    {
        try
        {
            return _worker.Invoke(a =>
            {
                var pids = TeamsPidSet();
                var window = PickMeetingWindow(FindTeamsWindows(a, pids));
                if (window is null)
                    return new TeamsHealth(false, 0, false, 0, 0, TreeLooksEmpty: true);

                bool hangup = false;
                foreach (var id in _sel.HangupAutomationIds)
                {
                    if (Uia.FindFirstByAutomationId(window, id) != null) { hangup = true; break; }
                }

                int nodes = 0, rings = 0, pills = 0;
                string ring = _sel.ActiveSpeakerRingClass.ToLowerInvariant();
                var walker = WalkerFor(a);
                foreach (var (el, _, parent) in Uia.Descendants(window, walker, _sel.MaxDepth, _sel.NodeCap))
                {
                    nodes++;
                    var cls = Uia.ClassNameLower(el);
                    if (cls.Contains(ring)) rings++;
                    else if (Uia.IsText(el))
                    {
                        if (Uia.AncestorClassMatches(parent, walker, _sel.NameContainerHints, _sel.NameContainerAncestorLevels))
                            pills++;
                    }
                }

                bool empty = nodes < 5;
                if (empty)
                {
                    _log?.Invoke(
                        "[Echo.Teams] Teams tree looks empty — set WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=" +
                        "--force-renderer-accessibility and restart Teams.");
                }
                else if (rings == 0 && pills == 0)
                {
                    _log?.Invoke(
                        "[Echo.Teams] Tree is populated but no speaker rings or name pills matched — Teams may " +
                        "have shipped a UI update. Update selectors in teams_selectors.json (see runbook).");
                }

                return new TeamsHealth(true, nodes, hangup, rings, pills, empty);
            });
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[Echo.Teams] HealthCheck failed: {ex.Message}");
            return new TeamsHealth(false, 0, false, 0, 0, TreeLooksEmpty: true);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  UIA helpers (run inside the worker / watch thread only)
    // ─────────────────────────────────────────────────────────────────────────

    private HashSet<int> TeamsPidSet()
    {
        var wanted = _sel.TeamsProcessNames.Select(n => n.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var pids = new HashSet<int>();
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch { return pids; }

        foreach (var p in all)
        {
            try
            {
                var n = p.ProcessName.ToLowerInvariant();
                if (wanted.Contains(n) || wanted.Contains(n + ".exe"))
                    pids.Add(p.Id);
            }
            catch { /* process exited / access denied */ }
            finally { try { p.Dispose(); } catch { /* ignore */ } }
        }
        return pids;
    }

    private List<AutomationElement> FindTeamsWindows(UIA3Automation automation, HashSet<int> pids)
    {
        var result = new List<AutomationElement>();
        if (pids.Count == 0) return result;
        try
        {
            // Raw-view walk (mirrors Python root.GetChildren(), whose lib walks RawViewWalker).
            var walker = WalkerFor(automation);
            var desktop = automation.GetDesktop();
            foreach (var w in Uia.Children(desktop, walker))
            {
                if (pids.Contains(Uia.ProcessId(w)))
                    result.Add(w);
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[Echo.Teams] Window enumeration failed: {ex.Message}");
        }
        return result;
    }

    private AutomationElement? PickMeetingWindow(List<AutomationElement> wins)
    {
        if (wins.Count == 0) return null;
        var meeting = wins.Where(w => !IsHubWindow(w)).ToList();
        var pool = meeting.Count > 0 ? meeting : wins;
        // Prefer the REAL meeting window: hangup button present (the authoritative in-call
        // marker) beats everything; non-compact beats the compact floating tile, whose UIA
        // tree has no participant tiles at all (seen live 2026-07-16: roster/speakers empty
        // for a whole meeting because the compact window had been picked). Area only breaks ties.
        return pool.OrderByDescending(MeetingWindowScore).ThenByDescending(Uia.Area).First();
    }

    /// <summary>2 pts for a hangup button, 1 pt for not being the compact tile. Never throws.</summary>
    private int MeetingWindowScore(AutomationElement w)
    {
        int score = 0;
        try
        {
            foreach (var id in _sel.HangupAutomationIds)
                if (Uia.FindFirstByAutomationId(w, id) != null) { score += 2; break; }
            if (!IsCompactWindow(w)) score += 1;
        }
        catch { /* unreadable window scores 0 */ }
        return score;
    }

    private bool IsCompactWindow(AutomationElement w)
    {
        var folded = Fold(Uia.Name(w));
        return _compactFolded.Any(h => folded.Contains(h));
    }

    /// <summary>
    /// Resolve the meeting subject by reading the title of ANY Teams window, not only the
    /// tracked meeting window. In compact meeting view the tracked window's name is generic
    /// ("Affichage compact de reunion"), but the main Teams window title bar still carries
    /// "&lt;Subject&gt; | Microsoft Teams". Non-hub windows, largest first, are preferred so a
    /// real meeting/main window wins over a Calendar/Chat tab; returns the first non-generic
    /// <see cref="CleanTitle"/>, or null if none of the windows expose a real subject.
    /// </summary>
    private string? SubjectFromWindows(List<AutomationElement> wins)
    {
        foreach (var w in wins.OrderByDescending(w => !IsHubWindow(w)).ThenByDescending(Uia.Area))
        {
            string? subj = null;
            try { subj = CleanTitle(Uia.Name(w)); } catch { /* ignore this window */ }
            if (!string.IsNullOrEmpty(subj)) return subj;
        }
        return null;
    }

    private bool IsHubWindow(AutomationElement w)
    {
        var folded = Fold(Uia.Name(w));
        return _hubHintsFolded.Any(h => folded.Contains(h));
    }

    private List<string> RosterFromWindow(AutomationElement window, ITreeWalker walker)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (el, _, parent) in Uia.Descendants(window, walker, _sel.MaxDepth, _sel.NodeCap))
        {
            if (!Uia.IsText(el)) continue;
            var nm = Uia.Name(el).Trim();
            if (nm.Length == 0) continue;
            if (Uia.AncestorClassMatches(parent, walker, _sel.NameContainerHints, _sel.NameContainerAncestorLevels))
                names.Add(nm);
        }
        return names.ToList();
    }

    /// <summary>
    /// Geometric active-speaker detection (no IsSpeaking UIA property). Single DFS collects
    /// (1) speaking-ring tiles by ClassName substring and (2) name-pill TextControls, then
    /// joins by rectangle-center containment. Returns (speaker names, detail hit objects).
    /// </summary>
    private (List<string> Speakers, List<object> Detail) DetectActiveSpeakers(AutomationElement window, ITreeWalker walker)
    {
        string ring = _sel.ActiveSpeakerRingClass.ToLowerInvariant();
        var frames = new List<RectD>();
        var names = new List<(RectD Rect, string Name)>();

        foreach (var (el, _, parent) in Uia.Descendants(window, walker, _sel.MaxDepth, _sel.NodeCap))
        {
            var cls = Uia.ClassNameLower(el);
            // 'vdi-occlusion' (name pill) != 'vdi-frame-occlusion' (speaker ring) — match the full ring class.
            if (cls.Contains(ring))
            {
                var r = Uia.Rect(el);
                if (r != null) frames.Add(r.Value);
                continue;
            }

            if (Uia.IsText(el))
            {
                var nm = Uia.Name(el).Trim();
                if (nm.Length == 0) continue;
                if (Uia.AncestorClassMatches(parent, walker, _sel.NameContainerHints, _sel.NameContainerAncestorLevels))
                {
                    var r = Uia.Rect(el);
                    if (r != null) names.Add((r.Value, nm));
                }
            }
        }

        var speakers = new List<string>();
        var detail = new List<object>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var fr in frames)
        {
            foreach (var (nr, nm) in names)
            {
                if (nr.CenterIn(fr) && seen.Add(nm))
                {
                    speakers.Add(nm);
                    detail.Add(new
                    {
                        raw_name = nm,
                        cue = _sel.ActiveSpeakerRingClass,
                        ctype = "GroupControl",   // hard-coded literal (not read from the tile), matching the Python shape
                        automation_id = "",
                        @class = _sel.ActiveSpeakerRingClass,
                    });
                    break;
                }
            }
        }

        return (speakers, detail);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Title / folding helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Extracts the meeting subject from a "&lt;subject&gt; | Microsoft Teams" title, or null if all segments are generic.</summary>
    private string? CleanTitle(string? title) => PickSubject(title, _genericFolded, _sel.SubjectStripPrefixes);

    /// <summary>
    /// Pure title-resolution: extract the meeting subject from a Teams window title, or null.
    /// Two shapes are handled (both seen live):
    ///  - Plain "&lt;Subject&gt; | Microsoft Teams" -> first non-generic, non-email segment.
    ///  - Account/JOIN-decorated "&lt;X&gt; | Reunion Microsoft Teams | &lt;tenant&gt; | &lt;email&gt; |
    ///    Microsoft Teams" (detected by an email-bearing segment): ONLY the leading segment can
    ///    be a real subject; every later segment is org/account chrome. On the join screen the
    ///    lead is itself generic ("Meeting join"), so nothing is returned and polling continues
    ///    until the real "&lt;Subject&gt; | Microsoft Teams" title appears (2026-07-16 regression).
    /// Extracted as a pure function so it is unit-testable without a live UIA tree.
    /// <paramref name="foldedGenerics"/> must already be folded via <see cref="Fold"/>.
    /// </summary>
    internal static string? PickSubject(string? title, IReadOnlyCollection<string> foldedGenerics,
        IReadOnlyList<string>? stripPrefixes = null)
    {
        var parts = (title ?? "").Split('|')
            .Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (parts.Count == 0) return null;

        static bool Emailish(string s) => s.Contains('@');
        // A usable subject: not an email/account segment, and once Outlook/Teams chrome
        // prefixes ("Meeting request - ", "TR:", …) are stripped, not a generic label.
        string? Clean(string s)
        {
            if (Emailish(s)) return null;
            var core = StripSubjectPrefixes(s, stripPrefixes);
            return core.Length > 0 && !foldedGenerics.Contains(Fold(core)) ? core : null;
        }

        if (parts.Any(Emailish))            // account/join-decorated title
            return Clean(parts[0]);

        foreach (var part in parts)         // plain title
            if (Clean(part) is { } subject) return subject;
        return null;
    }

    /// <summary>Repeatedly strip any listed prefix from the front (OrdinalIgnoreCase).</summary>
    internal static string StripSubjectPrefixes(string s, IReadOnlyList<string>? prefixes)
    {
        s = s.Trim();
        if (prefixes is null || prefixes.Count == 0) return s;
        bool again = true;
        while (again && s.Length > 0)
        {
            again = false;
            foreach (var p in prefixes)
            {
                if (p.Length > 0 && s.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                {
                    s = s[p.Length..].TrimStart();
                    again = true;
                }
            }
        }
        return s.Trim();
    }

    /// <summary>NFKD normalize, drop combining marks, lower-case (so "Activité" folds to "activite").</summary>
    internal static string Fold(string? s)
    {
        var norm = (s ?? "").Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(norm.Length);
        foreach (var c in norm)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static double EpochNow() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    private static double ElapsedMs(long startTicks) =>
        (Stopwatch.GetTimestamp() - startTicks) / (double)Stopwatch.Frequency * 1000.0;

    public void Dispose() => _worker.Dispose();
}
