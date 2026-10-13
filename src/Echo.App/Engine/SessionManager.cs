using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Echo.Core;
using Echo.Core.Abstractions;
using Echo.Core.Engine;
using Echo.Core.Models;
using Echo.Core.Storage;

namespace Echo.App.Engine;

public enum RecorderState { Idle, Arming, Recording, Stopping, Transcribing, GeneratingCr, Done, Error }

/// <summary>
/// Orchestrates one recording session's lifecycle (see docs/contract/platform.md §6):
/// start → capture (+ Teams watch) → stop → (auto) transcribe → (auto) minutes.
/// Owns no UI; raises <see cref="StateChanged"/> for the tray/windows to react.
/// All long operations run on background tasks so bridge calls return immediately.
///
/// TWO independent states (2026-09-16 fix): <see cref="State"/> is the CAPTURE state
/// (Idle/Arming/Recording/Stopping) and <see cref="Processing"/> is the post-meeting
/// pipeline state (Transcribing/GeneratingCr/Done/Error), which concerns a DIFFERENT
/// session most of the time. They used to share one field: the pipeline of meeting N
/// ending in "Error" flipped IsRecording to false while meeting N+1 was being captured,
/// the UI showed "not recording", the user's Stop was silently ignored and a new Start
/// replaced the live capture, which then ran orphaned for hours (3 h on 2026-09-16,
/// 31 h on 2026-09-14).
/// </summary>
public sealed class SessionManager
{
    private readonly Func<IAudioCaptureService> _audioFactory;
    private readonly ITeamsMonitor _teams;
    private readonly ITranscriber _transcriber;
    private readonly IMinutesGenerator _minutes;
    private readonly ISummarizer _summarizer;
    private readonly IVocabulary _vocab;
    private readonly ISettingsStore _settings;
    private readonly ISecretStore _secret;
    private readonly StoragePaths _paths;
    private readonly INotifier _notifier;
    private readonly I18n _i18n;
    private readonly ProcessingQueue _queue;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IAudioCaptureService? _audio;
    private CancellationTokenSource? _sessionCts;
    private Task? _teamsWatch;
    private Task? _captureLoop;
    private CaptureClock _clock;
    private long _startPerf;

    // Sessions whose pipeline is running right now (a second "Générer" / queue tick on the
    // same folder must not race the first one).
    private readonly HashSet<string> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    // Teams auto-detect decisions (rising edge, "call handled", auto-stop guard); shared by
    // the poll loop and the manual start/stop entry points, hence the lock.
    private readonly AutoDetectPolicy _autoDetect = new();

    public SessionManager(
        Func<IAudioCaptureService> audioFactory, ITeamsMonitor teams,
        ITranscriber transcriber, IMinutesGenerator minutes, ISummarizer summarizer,
        IVocabulary vocab, ISettingsStore settings, ISecretStore secret,
        StoragePaths paths, INotifier notifier, I18n i18n)
    {
        _audioFactory = audioFactory; _teams = teams; _transcriber = transcriber;
        _minutes = minutes; _summarizer = summarizer; _vocab = vocab;
        _settings = settings; _secret = secret; _paths = paths; _notifier = notifier;
        _i18n = i18n;
        _queue = new ProcessingQueue(Path.Combine(paths.DataDir, "pending.json"), Log.Info);
    }

    // ── state ────────────────────────────────────────────────────────────────

    /// <summary>Capture state only: Idle, Arming, Recording or Stopping.</summary>
    public RecorderState State { get; private set; } = RecorderState.Idle;
    /// <summary>Post-meeting pipeline state (Idle, Transcribing, GeneratingCr, Done, Error) of
    /// the LAST pipeline step reported. Several pipelines can run at once (a queued retry next
    /// to a fresh meeting): "is something being processed" is <see cref="PipelinesInFlight"/>.</summary>
    public RecorderState Processing { get; private set; } = RecorderState.Idle;
    /// <summary>Session folder the pipeline is (or was last) working on.</summary>
    public string? ProcessingDir { get; private set; }
    /// <summary>Number of session pipelines running right now.</summary>
    public int PipelinesInFlight { get { lock (_inFlight) return _inFlight.Count; } }

    public bool IsRecording => State is RecorderState.Arming or RecorderState.Recording;
    /// <summary>A capture object exists: the truth for "is audio being written right now",
    /// independent of any state flag. Stop always acts on it.</summary>
    public bool CaptureAlive => _audio is not null;
    /// <summary>Live capture health code for the UI banner ("" when healthy or idle);
    /// see <see cref="IAudioCaptureService.HealthCode"/>.</summary>
    public string CaptureHealth { get { try { return _audio?.HealthCode ?? ""; } catch { return ""; } } }
    /// <summary>Recording, stopping, or at least one pipeline is transcribing / generating.
    /// Counted from the in-flight set: with a single Processing field, the first of two
    /// concurrent pipelines to finish flipped IsBusy to false while the other still ran.</summary>
    public bool IsBusy => IsRecording || State == RecorderState.Stopping || CaptureAlive
                          || PipelinesInFlight > 0;
    /// <summary>A capture is being stopped/finalised: a Start now must be ignored (a double
    /// press of the hotkey used to start a new recording right after the stop).</summary>
    public bool IsStopping => State == RecorderState.Stopping;
    public bool Physical { get; private set; }
    public string? SessionDir { get; private set; }
    public string MeetingName { get; private set; } = "";
    public ProcessingQueue Queue => _queue;

    public event EventHandler? StateChanged;

    public double ElapsedSeconds =>
        IsRecording ? _clock.ElapsedSeconds(System.Diagnostics.Stopwatch.GetTimestamp()) : 0;

    /// <summary>UTC wall-clock anchor of the current recording (for capture "elapsed" stamps).</summary>
    public DateTime StartedWallUtc => _clock.WallUtc;

    // ── start ────────────────────────────────────────────────────────────────

    /// <summary>Smart start: Teams call if one is active, otherwise a physical (mic-only) recording.</summary>
    public Task StartSmartAsync()
    {
        if (IsStopping) return IgnoredWhileStopping();
        return StartAsync(physical: !_teams.InMeeting());
    }

    /// <summary>Manual start (button, hotkey, tray). A start during a Teams call marks that
    /// call as handled: auto-detect will not offer it again.</summary>
    public Task StartAsync(bool physical)
    {
        lock (_autoDetect) _autoDetect.MarkManual();
        return StartCoreAsync(physical);
    }

    private Task IgnoredWhileStopping()
    {
        Log.Info("[session] start ignored: the previous recording is still being stopped");
        return Task.CompletedTask;
    }

    private async Task StartCoreAsync(bool physical)
    {
        // A Start while the previous capture is being finalised would queue on the gate and
        // start a NEW recording as soon as the stop completes (double hotkey press).
        if (IsStopping) { await IgnoredWhileStopping(); return; }
        await _gate.WaitAsync();
        try
        {
            // Never replace a live capture: that is exactly how the orphaned recordings
            // happened. Tell the user instead of silently returning.
            if (IsRecording || _audio is not null)
            {
                _notifier.Notify(_i18n.T("nf_already_recording"));
                return;
            }
            Set(RecorderState.Arming);
            Physical = physical;

            // Fresh settings from disk before capture: the audio mode (endpoint/process) and
            // other options must reflect what the file holds, not a possibly-stale in-memory
            // copy (2026-07-17: process-loopback ran although capture_mode was "endpoint").
            try { _settings.Load(); } catch (Exception ex) { Log.Error("settings reload (start)", ex); }

            MeetingInfo? info = null;
            if (!physical)
            {
                try { info = _teams.ReadMeetingInfo(TimeSpan.FromSeconds(6)); } catch { /* best effort */ }
            }
            MeetingName = info?.Name ?? "";

            var now = DateTime.Now;
            // Folder names are minute-precise: a second meeting started in the same minute
            // (quick stop/start) must not reuse — and overwrite the WAVs of — the first one,
            // which may still be transcribing.
            var dir = SessionFiles.UniqueDir(Path.Combine(_paths.RecordingsDir, _paths.SessionFolderName(now, MeetingName, physical)));
            Directory.CreateDirectory(dir);
            SessionDir = dir;
            Log.Info($"[session] start {Path.GetFileName(dir)} (physical={physical})");

            _clock = CaptureClock.StartNow();
            _startPerf = System.Diagnostics.Stopwatch.GetTimestamp();
            WriteMeetingJson(dir, MeetingName, info?.Roster ?? Array.Empty<string>(), physical);

            _sessionCts = new CancellationTokenSource();
            _audio = _audioFactory();
            lock (_trackNotifyLast) _trackNotifyLast.Clear();
            _audio.TrackFailed += (_, label) => NotifyTrackRestart(label);
            await _audio.StartAsync(dir, physical, _clock, _sessionCts.Token);

            if (!physical)
            {
                _teamsWatch = Task.Run(() => _teams.WatchAsync(dir, _clock, _sessionCts!.Token));
                // F1: auto screenshot of the shared screen (slides) -> <session>\Captures.
                // Reads auto_screenshot live and only captures while Teams is foreground.
                var ct = _sessionCts!.Token;
                _captureLoop = Task.Run(() => ScreenCapture.RunAutoCaptureAsync(
                    getFolder: () => SessionDir is null ? null : Path.Combine(SessionDir, "Captures"),
                    isEnabled: () => _settings.Current.AutoScreenshot && !Physical,
                    teamsPids: () => _teams.GetTeamsPids(),
                    ct: ct,
                    windowOnly: () => _settings.Current.CaptureArea != "screen"));
            }

            Set(RecorderState.Recording);
        }
        catch (Exception ex)
        {
            Set(RecorderState.Idle);
            _notifier.Notify($"Échec du démarrage : {ex.Message}");
            await SafeCleanupAsync();
        }
        finally { _gate.Release(); }
    }

    // ── stop + process ─────────────────────────────────────────────────────────

    /// <summary>Manual stop (button, hotkey, widget): stop, then the automatic post-meeting
    /// flow. A stop during a Teams call marks that call as handled (no "Démarrer ?" again).</summary>
    public Task StopAndProcessAsync()
    {
        lock (_autoDetect) _autoDetect.MarkManual();
        return StopCoreAsync(process: true);
    }

    /// <summary>
    /// Stop for an app exit (quit, update, Windows logoff/shutdown): finalise the WAVs and the
    /// session sidecars, but do NOT start the transcription, which the exit would kill midway.
    /// When auto_transcribe is on, the session is queued instead and processed by the next
    /// launch. Returns the stopped session folder (null when nothing was recording).
    /// </summary>
    public async Task<string?> StopForExitAsync(string reason)
    {
        var dir = await StopCoreAsync(process: false);
        if (dir is null) return null;
        Log.Info($"[session] capture stopped for exit ({reason}): {Path.GetFileName(dir)}");
        try
        {
            var s = _settings.Current;
            if (s.AutoTranscribe)
            {
                _queue.EnqueueNow(dir, minutes: s.AutoMinutes, summarize: true, $"arrêt d'Echo ({reason})");
                WriteStatus(dir, "waiting");
            }
        }
        catch (Exception ex) { Log.Error("queue on exit", ex); }
        return dir;
    }

    private async Task<string?> StopCoreAsync(bool process)
    {
        await _gate.WaitAsync();
        string? dir;
        try
        {
            // Stop acts on the LIVE CAPTURE, whatever the flags say. A capture object with no
            // "Recording" state is precisely the orphan case we must be able to end.
            if (!IsRecording && _audio is null) return null;
            Set(RecorderState.Stopping);
            dir = SessionDir;
            Log.Info($"[session] stop {Path.GetFileName(dir ?? "")}");

            _sessionCts?.Cancel();
            try { if (_teamsWatch is not null) await _teamsWatch.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            try { if (_captureLoop is not null) await _captureLoop.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }

            // The real subject often appears only AFTER the 6 s start poll (Teams shows a
            // transient "Meeting join …" title while joining). The watch loop keeps looking
            // for the whole call and writes meeting_title.txt; promote it into meeting.json
            // now so the display AND the CR use the real subject, not the date fallback.
            if (dir is not null)
            {
                try { PromoteDetectedTitle(dir); } catch (Exception ex) { Log.Error("promote title", ex); }
            }

            // Every step is individually guarded: a finalize hiccup on one track must
            // never lose the post-meeting processing (seen live: a broken mic finalize
            // skipped session.json AND the whole auto-transcription).
            IReadOnlyList<TrackResult> tracks = Array.Empty<TrackResult>();
            try { if (_audio is not null) tracks = await _audio.StopAsync(); }
            catch (Exception ex)
            {
                Log.Error("audio stop", ex);
                _notifier.Notify($"Arrêt audio incomplet ({ex.Message}). Le traitement continue avec les pistes disponibles.");
            }
            if (dir is not null)
            {
                try { WriteSessionJson(dir, tracks, Physical); } catch (Exception ex) { Log.Error("session.json", ex); }
                // Human-readable "Infos réunion.txt" sidecar (name / type / attendees / duration).
                try { SessionInfo.Write(dir, _i18n, force: true); } catch (Exception ex) { Log.Error("info file", ex); }
            }
            await SafeCleanupAsync();
            Set(RecorderState.Idle);
        }
        finally { _gate.Release(); }

        if (process && dir is not null) _ = Task.Run(() => AutoProcessAsync(dir!));
        return dir;
    }

    /// <summary>
    /// Auto post-meeting flow (Python <c>stop_and_process</c>): transcribe ONLY if
    /// <c>auto_transcribe</c> is on, and only then generate minutes if <c>auto_minutes</c>
    /// is on. When <c>auto_transcribe</c> is off, nothing is processed (session stays "recorded").
    /// </summary>
    public Task AutoProcessAsync(string dir)
    {
        var s = _settings.Current;
        if (!s.AutoTranscribe)                               // Python: no transcribe ⇒ no CR
        {
            Log.Info($"[pipeline] {Path.GetFileName(dir)}: auto_transcribe off, session kept as recorded");
            return Task.CompletedTask;
        }
        // Auto post-meeting: transcribe, generate the CR (if auto_minutes), and ALWAYS
        // build the short summary (resume.md) so the Résumé tab is ready without a click.
        Log.Info($"[pipeline] {Path.GetFileName(dir)}: automatic processing (minutes={s.AutoMinutes})");
        return RunAsync(dir, transcribe: true, minutes: s.AutoMinutes, summarize: true,
                        template: null, langOverride: null);
    }

    /// <summary>
    /// Transcribe (if needed) + generate the CR for a session — the <c>generate</c> /
    /// <c>retranscribe</c> entry point. Mirrors Python <c>_spawn_synthesis</c>: transcription
    /// runs only when <paramref name="forceTranscribe"/> is set (retranscribe) OR no
    /// <c>*Transcript.txt</c> exists yet; the minutes are ALWAYS (re)generated with the
    /// chosen <paramref name="template"/>. This is what stops an old, already-transcribed
    /// session from being re-transcribed (slow + costly) every time "Générer un CR" is clicked.
    /// </summary>
    public Task ProcessAsync(string dir, CrTemplate? template = null, string? langOverride = null,
                             bool forceTranscribe = false)
    {
        bool transcribe = forceTranscribe || !HasTranscript(dir);
        // Manual relaunches used to leave no trace at all when they succeeded.
        Log.Info($"[pipeline] {Path.GetFileName(dir)}: manual run (transcribe={transcribe}, forced={forceTranscribe}, "
                 + $"template={template?.Id ?? "default"}, lang={langOverride ?? "auto"})");
        return RunAsync(dir, transcribe, minutes: true, summarize: forceTranscribe, template, langOverride);
    }

    // ── deferred retry queue ───────────────────────────────────────────────────

    /// <summary>Start the background pump of the deferred-retry queue (call once at startup).
    /// Re-marks the parked sessions as "waiting" for this process so their badge is right.</summary>
    public void StartQueue(CancellationToken ct)
    {
        foreach (var it in _queue.Snapshot()) WriteStatus(it.Dir, "waiting");
        try { RecoverInterrupted(); } catch (Exception ex) { Log.Error("interrupted sessions recovery", ex); }
        _ = Task.Run(() => _queue.RunLoopAsync(RunQueuedAsync, TimeSpan.FromSeconds(60), ct), ct);
    }

    /// <summary>
    /// Startup: a session whose sentinel still says "running"/"waiting" for a process that is
    /// gone (Echo killed, quit or logged off mid-pipeline) would show "en cours" forever and
    /// never be retried. It is queued again (badge "waiting") when there is something left to
    /// produce and the means to produce it; a session with nothing to transcribe from is
    /// flagged "error"; one that already has all its products just loses the stale badge.
    /// </summary>
    internal void RecoverInterrupted(Func<int, bool>? isEchoAlive = null)
    {
        var root = _paths.RecordingsDir;
        if (!Directory.Exists(root)) return;
        isEchoAlive ??= SessionStatus.IsEchoProcessAlive;
        var s = _settings.Current;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            SessionStatus.Sentinel? st;
            try { st = SessionStatus.Read(dir); } catch { continue; }
            if (!SessionStatus.IsInterrupted(st, Environment.ProcessId, isEchoAlive)) continue;
            if (_queue.Contains(dir)) continue;          // re-marked "waiting" just above

            var name = Path.GetFileName(dir);
            bool hasTr = HasTranscript(dir);
            bool needCr = s.AutoMinutes && !SessionFiles.HasCr(dir);
            bool needSum = !File.Exists(Path.Combine(dir, "resume.md"));
            if (!hasTr && !SessionFiles.HasAudio(dir))
            {
                WriteStatus(dir, "error");
                Log.Info($"[recovery] {name}: interrupted pipeline, no audio left: marked error");
            }
            else if (hasTr && !needCr && !needSum)
            {
                ClearStatus(dir);
                Log.Info($"[recovery] {name}: interrupted pipeline, nothing left to produce: badge cleared");
            }
            else
            {
                _queue.EnqueueNow(dir, needCr, needSum, "traitement interrompu par l'arrêt d'Echo");
                WriteStatus(dir, "waiting");
                Log.Info($"[recovery] {name}: interrupted pipeline queued again (transcribe={!hasTr}, minutes={needCr}, summary={needSum})");
            }
        }
    }

    internal async Task RunQueuedAsync(ProcessingQueue.Item it)
    {
        if (_audio is not null && SessionDir is not null && SamePath(it.Dir, SessionDir)) return; // still recording it
        // A pipeline is already working on it (a manual run, or the previous tick still going):
        // it will clear or re-park the item itself. Skip SILENTLY: this tick comes back every
        // minute and used to toast "déjà en cours de traitement" each time.
        if (IsInFlight(it.Dir)) return;
        Log.Info($"[queue] retrying {Path.GetFileName(it.Dir)} (attempt {it.Attempts}/{ProcessingQueue.DelaysMinutes.Length})");
        _notifier.Notify(_i18n.T("nf_retry_now").Replace("{name}", DisplayName(it.Dir)));
        await RunAsync(it.Dir, transcribe: !HasTranscript(it.Dir), minutes: it.Minutes, summarize: it.Summarize,
                       template: null, langOverride: null);

        // Safety net: every terminal path clears the item and every transient one re-parks it
        // with the next delay. One left due with the SAME attempt count would be re-run on the
        // next tick, every minute, forever (audio re-sent to the API + two toasts a minute).
        var after = _queue.Get(it.Dir);
        if (after is not null && after.Attempts == it.Attempts
            && after.NextAtEpoch <= DateTimeOffset.Now.ToUnixTimeSeconds() && !IsInFlight(it.Dir))
        {
            DropFromQueue(it.Dir);
            Log.Info($"[queue] {Path.GetFileName(it.Dir)}: run ended without a verdict, removed from the queue");
        }
    }

    private bool IsInFlight(string dir) { lock (_inFlight) return _inFlight.Contains(dir); }

    /// <summary>Terminal outcome for <paramref name="dir"/>: out of the retry queue, and a
    /// "waiting" badge must not outlive its queue item (a session with its transcript loses
    /// the badge, one without is flagged "error").</summary>
    private void DropFromQueue(string dir)
    {
        _queue.Clear(dir);
        try
        {
            if (SessionStatus.Read(dir) is { State: "waiting" })
            {
                if (HasTranscript(dir)) ClearStatus(dir); else WriteStatus(dir, "error");
            }
        }
        catch { /* badge only */ }
    }

    // ── pipeline ───────────────────────────────────────────────────────────────

    private async Task RunAsync(string dir, bool transcribe, bool minutes, bool summarize,
                                CrTemplate? template, string? langOverride)
    {
        // A session being captured right now cannot be processed (its speakers.jsonl is
        // still open for writing and its WAVs are growing). Seen 2026-09-16 13:25.
        if (_audio is not null && SessionDir is not null && SamePath(dir, SessionDir))
        {
            _notifier.Notify(_i18n.T("nf_stop_first"));
            return;
        }
        lock (_inFlight)
        {
            if (!_inFlight.Add(dir)) { _notifier.Notify(_i18n.T("nf_processing_busy")); return; }
        }
        try { await RunCoreAsync(dir, transcribe, minutes, summarize, template, langOverride); }
        finally { lock (_inFlight) _inFlight.Remove(dir); }
    }

    private async Task RunCoreAsync(string dir, bool transcribe, bool minutes, bool summarize,
                                    CrTemplate? template, string? langOverride)
    {
        // Re-read settings from disk before deciding what to produce. The on-disk file is the
        // source of truth; the in-memory copy was observed to drift (2026-07-17: auto_minutes
        // read as false at runtime although settings.json held true, so CRs silently stopped).
        // Reloading here is cheap and makes the CR decision match what the user actually set.
        try { _settings.Load(); } catch (Exception ex) { Log.Error("settings reload (process)", ex); }
        var s = _settings.Current;

        // Provider-aware key resolution: transcription and CR can each run on Mistral,
        // Gemini or Cloudflare, so pick the matching key for each. Only the transcription
        // key is a hard blocker (nothing can be produced without a transcript); a missing
        // CR key surfaces as a per-step error later.
        var trProv = Echo.Ai.LlmProvider.Normalize(s.TranscriptionProvider);
        var crProv = Echo.Ai.LlmProvider.Normalize(s.CrProvider);
        var transcribeKey = _secret.ResolveKeyFor(trProv);
        var crKey = _secret.ResolveKeyFor(crProv);
        // Say out loud which engine is about to be used, and on which model. Without this line
        // a run that silently went to another provider than the one selected in Settings could
        // only be guessed at from the error message (2026-09-18: two meetings transcribed on
        // voxtral while the Settings page showed Cloudflare).
        Log.Info($"[providers] transcription = {ProviderName(trProv)} / {TranscribeModel(s, trProv)}"
                 + $" · compte rendu = {ProviderName(crProv)} / {CrModel(s, crProv)}"
                 + $" · clés présentes : tr={!string.IsNullOrWhiteSpace(transcribeKey)} cr={!string.IsNullOrWhiteSpace(crKey)}");
        // The session's badge before this run: a failed RE-transcription puts it back as it
        // was, so a session that already has its transcript is never degraded to "error".
        bool hadTranscript = HasTranscript(dir);
        string? prevStatus = ReadRawStatus(dir);

        if (transcribe && string.IsNullOrWhiteSpace(transcribeKey))
        {
            // No key for the chosen engine: use another one right away if it has a key.
            var alt = s.AutoFallbackProvider ? AltProvider(trProv) : null;
            if (alt is not null)
            {
                _notifier.Notify(_i18n.T("nf_fallback").Replace("{from}", ProviderName(trProv))
                    .Replace("{to}", ProviderName(alt)).Replace("{why}", "clé absente"));
                s = WithProvider(s, transcription: true, alt);
                trProv = alt; transcribeKey = _secret.ResolveKeyFor(alt);
            }
            else
            {
                // Terminal: retrying without a key cannot succeed (it used to re-run every minute).
                DropFromQueue(dir);
                _notifier.Notify($"Clé API {ProviderName(trProv)} absente : transcription impossible. Renseignez-la dans Paramètres.");
                return;
            }
        }

        WriteStatus(dir, "running");
        try
        {
            // ── 1. transcription: the only step whose failure blocks everything after.
            if (transcribe)
            {
                var bias = _vocab.BuildContextBias(s);
                SetProcessing(RecorderState.Transcribing, dir);
                var (ok, err, s1) = await RunWithFallbackAsync(
                    (st, key) => _transcriber.TranscribeAsync(
                        new TranscriptionRequest(dir, key, st, bias, ChatApiKey: crKey), null, CancellationToken.None),
                    s, transcription: true, transcribeKey!, "transcription");
                if (!ok)
                {
                    Log.Error("transcription", err!);
                    if (IsNoAudio(err))
                    {
                        // Nothing to send (audio purged by the retention, or empty tracks): not an
                        // API or model problem, and retrying cannot help. Say so plainly and leave
                        // the session exactly as it was (it may well have its transcript).
                        RestoreStatus(dir, prevStatus);
                        DropFromQueue(dir);
                        _notifier.Notify(err is Echo.Ai.AudioPurgedException purged
                            ? Tr("nf_audio_purged", purged.UserMessage).Replace("{name}", DisplayName(dir))
                            : Tr("nf_no_audio",
                                "Aucune piste audio exploitable pour « {name} » (audio vide ou absent) : transcription impossible.")
                                .Replace("{name}", DisplayName(dir)));
                    }
                    else if (hadTranscript)
                    {
                        // A RE-transcription failed: the existing transcript stays the truth. No
                        // "error" badge, no deferred retry (the queue would only redo the CR).
                        if (err is Echo.Ai.LlmApiException rme) ApiHealth.Report(rme.UserMessage);
                        RestoreStatus(dir, prevStatus);
                        _notifier.Notify(Tr("nf_retranscribe_fail",
                            "Échec de la nouvelle transcription sur {provider} ({model}) : {why}. La transcription existante est conservée.")
                            .Replace("{provider}", ProviderName(trProv)).Replace("{model}", TranscribeModel(s, trProv))
                            .Replace("{why}", UserMessage(err!)));
                    }
                    else
                    {
                        if (err is Echo.Ai.LlmApiException tme) ApiHealth.Report(tme.UserMessage);
                        if (IsTransient(err))
                        {
                            Defer(dir, minutes, summarize, UserMessage(err!), hasTranscript: false);
                        }
                        else
                        {
                            WriteStatus(dir, "error");
                            _queue.Clear(dir);
                            // Name the engine that was actually called and the model it was given:
                            // a "Mistral refused voxtral" toast while Settings shows Cloudflare is
                            // unreadable otherwise, and the fix (change the model, or the provider)
                            // is in the same sentence.
                            _notifier.Notify($"Échec de la transcription sur {ProviderName(trProv)} "
                                             + $"({TranscribeModel(s, trProv)}) : {UserMessage(err!)}. "
                                             + "L'audio est conservé : corrigez le modèle dans Paramètres, "
                                             + "Fournisseurs & modèles, puis « Relancer la transcription ».");
                        }
                    }
                    SetProcessing(RecorderState.Error, dir);
                    return;
                }
                s = s1;              // the provider that actually transcribed (postprocess key follows crKey)
                ApiHealth.Ok();
            }

            // ── 2/3. CR + summary: independent products. One failing must not hide the
            // other, nor flag a session that HAS its transcript as broken (seen live:
            // a Mistral 429 on the CR left the whole session in error although the
            // transcript was on disk — perceived as "il n'a pas voulu transcrire").
            var stepErrors = new List<string>();
            bool transientCr = false, transientSum = false;
            int crFiles = 0;
            if (minutes)
            {
                SetProcessing(RecorderState.GeneratingCr, dir);
                var tpl = template ?? s.CrTemplates.FirstOrDefault(t => t.Id == s.CrTemplateId) ?? s.CrTemplates[0];
                MinutesResult? mres = null;
                var (ok, err, _) = await RunWithFallbackAsync(
                    async (st, key) => { mres = await _minutes.GenerateAsync(new MinutesRequest(dir, key, st, tpl, langOverride), null, CancellationToken.None); },
                    s, transcription: false, crKey ?? "", "compte rendu");
                if (ok) { crFiles = mres?.OutputFiles?.Count ?? 0; ApiHealth.Ok(); }
                else
                {
                    Log.Error("minutes", err!);
                    if (err is Echo.Ai.LlmApiException mme) ApiHealth.Report(mme.UserMessage);
                    stepErrors.Add($"compte rendu : {UserMessage(err!)}");
                    transientCr = IsTransient(err);
                }
            }
            // Short auto-summary (resume.md) so the Résumé tab is populated without a click.
            if (summarize)
            {
                SetProcessing(RecorderState.GeneratingCr, dir);
                var (ok, err, _) = await RunWithFallbackAsync(
                    (st, key) => _summarizer.SummarizeAsync(dir, key, st, CancellationToken.None),
                    s, transcription: false, crKey ?? "", "résumé");
                if (!ok)
                {
                    Log.Error("auto summary", err!);
                    stepErrors.Add($"résumé : {UserMessage(err!)}");
                    transientSum = IsTransient(err);
                }
            }

            // The transcript exists at this point: the session is usable — never leave
            // the red "error" badge for a CR/summary failure alone.
            ClearStatus(dir);
            try { SessionInfo.Ensure(dir, _i18n); } catch { }
            // Pipeline outcome trace — makes a silent skip diagnosable (the CR stopped being
            // produced from 2026-07-15 while the notification still said "compte-rendu prêts"):
            // if auto_minutes is on but crFiles=0 with no error, this line pins where it went.
            Log.Info($"[pipeline] auto_transcribe={s.AutoTranscribe} auto_minutes={s.AutoMinutes} " +
                     $"minutesRequested={minutes} crFiles={crFiles} stepErrors={stepErrors.Count} " +
                     $"tr_provider={s.TranscriptionProvider} cr_provider={s.CrProvider}");
            // NOTE: intentionally NO folder reveal here. The Python app did os.startfile(dir)
            // at the end, but the user does not want the folder to pop open after a recording
            // (and shell-executing the directory could raise a Windows "location unavailable"
            // dialog). The session is reachable from the app; "Ouvrir le dossier" stays manual.
            if (stepErrors.Count == 0)
            {
                _queue.Clear(dir);
                // Be honest: only claim a compte-rendu when one was actually written. A CR that
                // was requested but produced NO file (no exception) is surfaced, not hidden behind
                // "compte-rendu prêts"; a run without the CR step reports the summary only.
                if (minutes && crFiles == 0)
                {
                    SetProcessing(RecorderState.Error, dir);
                    _notifier.Notify(_i18n.T("nf_cr_missing"));
                }
                else
                {
                    SetProcessing(RecorderState.Done, dir);
                    string doneKey = !transcribe ? "nf_cr_done" : (minutes ? "nf_done" : "nf_done_nocr");
                    _notifier.Notify(_i18n.T(doneKey));
                }
            }
            else if (transientCr || transientSum)
            {
                // The transcript is there; park the CR/summary for a later, silent retry.
                SetProcessing(RecorderState.Error, dir);
                Defer(dir, minutes: minutes && transientCr, summarize: summarize && transientSum,
                      string.Join(" ; ", stepErrors), hasTranscript: true);
            }
            else
            {
                _queue.Clear(dir);
                SetProcessing(RecorderState.Error, dir);
                var prefix = transcribe ? "Transcription terminée, mais échec du " : "Échec du ";
                _notifier.Notify(prefix + string.Join(" ; échec du ", stepErrors)
                                 + ". Relancez via « Générer un CR ».");
            }
        }
        catch (Exception ex)
        {
            // Safety net for anything outside the per-step guards.
            Log.Error("pipeline", ex);
            // Never degrade a session that has its transcript (or gets one in this run).
            if (HasTranscript(dir)) RestoreStatus(dir, hadTranscript ? prevStatus : null);
            else WriteStatus(dir, "error");
            _queue.Clear(dir);
            SetProcessing(RecorderState.Error, dir);
            _notifier.Notify($"Échec du traitement : {UserMessage(ex)}");
        }
    }

    /// <summary>
    /// Run <paramref name="step"/> with the configured provider; on an API failure that the
    /// OTHER provider could avoid (rate limit, tier/model refusal, outage, bad key, prompt too
    /// long for this model), and when <c>auto_fallback_provider</c> is on and the other key
    /// exists, run it once more on the other provider. Returns the settings actually used (so
    /// later steps follow). Every success is logged with provider, model and duration: a
    /// transcript that went fine used to leave no line at all.
    /// 400/422 (request rejected by the server) never fall back — the other engine would get
    /// the same malformed input — EXCEPT a context overflow, which a model with a larger
    /// window can absorb.
    /// </summary>
    private async Task<(bool Ok, Exception? Error, AppSettings Used)> RunWithFallbackAsync(
        Func<AppSettings, string, Task> step, AppSettings s, bool transcription, string key, string label)
    {
        var prov = Echo.Ai.LlmProvider.Normalize(transcription ? s.TranscriptionProvider : s.CrProvider);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await step(s, key);
            LogStepOk(label, s, transcription, prov, sw.Elapsed, fallback: false);
            return (true, null, s);
        }
        catch (Echo.Ai.LlmApiException ex) when (CanFallback(ex, s))
        {
            var alt = AltProvider(prov);
            if (alt is null) return (false, ex, s);
            var altKey = _secret.ResolveKeyFor(alt)!;
            Log.Error($"{label} ({ProviderName(prov)}), trying {ProviderName(alt)}", ex);
            _notifier.Notify(_i18n.T("nf_fallback").Replace("{from}", ProviderName(prov))
                .Replace("{to}", ProviderName(alt)).Replace("{why}", ShortWhy(ex)));
            var s2 = WithProvider(s, transcription, alt);
            sw.Restart();
            try
            {
                await step(s2, altKey);
                LogStepOk(label, s2, transcription, alt, sw.Elapsed, fallback: true);
                return (true, null, s2);
            }
            catch (Exception ex2)
            {
                // Always log the fallback's own failure: on 2026-09-17 the Mistral retry died
                // within 30 s and only the Gemini 503 was reported, hiding the real cause.
                Log.Error($"{label} ({ProviderName(alt)}) also failed", ex2);
                // Report the more actionable of the two: a transient one keeps the retry path open.
                return (false, IsTransient(ex2) || !IsTransient(ex) ? ex2 : ex, s);
            }
        }
        catch (Exception ex) { return (false, ex, s); }
    }

    private static void LogStepOk(string label, AppSettings s, bool transcription, string provider, TimeSpan took, bool fallback)
    {
        var model = transcription ? TranscribeModel(s, provider) : CrModel(s, provider);
        Log.Info($"[pipeline] {label} OK : {ProviderName(provider)} / {model} en {took.TotalSeconds:0.0} s"
                 + (fallback ? " (fournisseur de secours)" : ""));
    }

    internal static bool CanFallback(Echo.Ai.LlmApiException ex, AppSettings s)
        => s.AutoFallbackProvider && (ex.StatusCode is not (400 or 422) || ex.IsContextOverflow);

    /// <summary>429 / 5xx / network: worth a later automatic retry.</summary>
    private static bool IsTransient(Exception? ex) => ex is Echo.Ai.LlmApiException e && e.IsRetryable;

    /// <summary>
    /// The transcriber found nothing to send: audio purged by the retention policy
    /// (<see cref="Echo.Ai.AudioPurgedException"/>), or no usable track at all ("No usable
    /// audio track."). Neither is an API or model problem.
    /// </summary>
    internal static bool IsNoAudio(Exception? ex)
        => ex is not null
           && (ex is Echo.Ai.AudioPurgedException
               || ex.Message.Contains("No usable audio track", StringComparison.OrdinalIgnoreCase)
               || (ex.InnerException is { } inner && IsNoAudio(inner)));

    private void Defer(string dir, bool minutes, bool summarize, string why, bool hasTranscript)
    {
        var delay = _queue.Enqueue(dir, minutes, summarize, why);
        if (delay is { } m)
        {
            WriteStatus(dir, "waiting");
            _notifier.Notify(_i18n.T("nf_retry_later").Replace("{why}", why).Replace("{m}", m.ToString()));
        }
        else
        {
            // Budget exhausted: the item is already out of the queue. A session that has its
            // transcript is usable and never flagged "error" for a CR/summary failure.
            if (hasTranscript) ClearStatus(dir); else WriteStatus(dir, "error");
            _notifier.Notify(_i18n.T("nf_retry_giveup")
                .Replace("{n}", ProcessingQueue.DelaysMinutes.Length.ToString()).Replace("{why}", why));
        }
    }

    /// <summary>Model that will actually be used for the transcription, per provider — same
    /// resolution as Transcriber.ResolveTranscribe, for the diagnostic line.</summary>
    private static string TranscribeModel(AppSettings s, string provider) => provider switch
    {
        Echo.Ai.LlmProvider.Gemini => Echo.Ai.LlmProvider.NonEmpty(s.GeminiTranscribeModel, EchoConstants.DefaultGeminiTranscribeModel),
        Echo.Ai.LlmProvider.Cloudflare => Echo.Ai.LlmProvider.NonEmpty(s.CloudflareTranscribeModel, EchoConstants.DefaultCloudflareTranscribeModel),
        _ => Echo.Ai.LlmProvider.NonEmpty(s.MistralTranscribeModel, "voxtral-mini-2602"),
    };

    private static string CrModel(AppSettings s, string provider) => provider switch
    {
        Echo.Ai.LlmProvider.Gemini => Echo.Ai.LlmProvider.NonEmpty(s.GeminiCrModel, EchoConstants.DefaultGeminiCrModel),
        Echo.Ai.LlmProvider.Cloudflare => Echo.Ai.LlmProvider.NonEmpty(s.CloudflareCrModel, EchoConstants.DefaultCloudflareCrModel),
        _ => Echo.Ai.LlmProvider.NonEmpty(s.MistralCrModel, "ministral-8b-latest"),
    };

    private static AppSettings WithProvider(AppSettings s, bool transcription, string provider)
    {
        // Deep copy through JSON: AppSettings round-trips (snake_case names + extension data).
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s)) ?? s;
        var p = Echo.Ai.LlmProvider.Normalize(provider);
        if (transcription) copy.TranscriptionProvider = p; else copy.CrProvider = p;
        return copy;
    }

    /// <summary>First OTHER provider (Mistral, Gemini, Cloudflare order) whose key is stored;
    /// null when none. Cloudflare needs its account id too.</summary>
    private string? AltProvider(string current)
    {
        foreach (var p in Echo.Ai.LlmProvider.All)
        {
            if (p == current) continue;
            if (string.IsNullOrWhiteSpace(_secret.ResolveKeyFor(p))) continue;
            if (p == Echo.Ai.LlmProvider.Cloudflare && string.IsNullOrWhiteSpace(_settings.Current.CloudflareAccountId)) continue;
            return p;
        }
        return null;
    }

    private static string ProviderName(string provider) => Echo.Ai.LlmProvider.DisplayName(provider);

    private static string ShortWhy(Echo.Ai.LlmApiException ex) => ex.StatusCode switch
    {
        _ when ex.IsContextOverflow => "trop long pour le modèle",
        413 => "réponse tronquée",                 // LlmEmptyResponseException: 413 = truncated, 502 = empty
        429 => "saturé, 429",
        401 => "clé refusée, 401",
        403 => "modèle hors palier, 403",
        404 => "modèle inconnu, 404",
        0 => "injoignable",
        >= 500 => $"indisponible, {ex.StatusCode}",
        _ => $"HTTP {ex.StatusCode}",
    };

    /// <summary>User-facing error text: API failures get their actionable French message
    /// (rate limit, bad key, network…), the rest keeps ex.Message.</summary>
    private static string UserMessage(Exception ex)
        => ex is Echo.Ai.LlmApiException m ? m.UserMessage : ex.Message;

    /// <summary>True iff the session already holds a transcript (Python <c>_has_transcript</c>:
    /// any file whose name ends in "Transcript.txt", incl. the "Processed Transcript.txt").</summary>
    private static bool HasTranscript(string dir) => SessionFiles.HasTranscript(dir);

    private static string DisplayName(string dir)
    {
        try
        {
            var mj = Path.Combine(dir, "meeting.json");
            if (File.Exists(mj))
            {
                var n = JsonNode.Parse(File.ReadAllText(mj))?["name"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(n)) return n!;
            }
        }
        catch { }
        return Path.GetFileName(dir);
    }

    private static bool SamePath(string a, string b) => SessionFiles.SamePath(a, b);

    // ── auto-detect loop (meeting_monitor) ──────────────────────────────────────

    /// <summary>Polls Teams every 5 s; offers/auto-starts recording when a call starts and
    /// auto-stops it after the grace period when it ends. The decisions themselves live in
    /// <see cref="AutoDetectPolicy"/> (pure, unit-tested).</summary>
    public async Task RunAutoDetectAsync(Func<string, bool> askYesNo, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var s = _settings.Current;
                bool inMeeting = _teams.InMeeting();
                var tick = new AutoDetectTick(inMeeting, s.AutoDetect, IsRecording, Physical, SessionDir,
                    AutoDetectPolicy.GraceTicks(s.AutoStopGraceS, EchoConstants.MeetingMonitorPollSeconds));
                AutoDetectAction action;
                lock (_autoDetect) action = _autoDetect.Step(tick);

                if (action == AutoDetectAction.OfferStart)
                {
                    Log.Info($"[auto-detect] Teams call started: {(s.AutoRecord ? "auto_record" : "asking")}");
                    bool go = s.AutoRecord || askYesNo(Tr("ask_teams_start", "Réunion Teams détectée. Démarrer l'enregistrement ?"));
                    // The question may have stayed on screen for a while: re-check before starting.
                    if (go && !IsRecording && _audio is null && !IsStopping) await StartCoreAsync(physical: false);
                }
                else if (action == AutoDetectAction.AutoStop)
                {
                    Log.Info($"[auto-detect] Teams call ended ({s.AutoStopGraceS} s without a call): stopping {Path.GetFileName(SessionDir ?? "")}");
                    await StopCoreAsync(process: true);
                }
            }
            catch { /* keep the loop alive across UIA hiccups */ }

            try { await Task.Delay(TimeSpan.FromSeconds(EchoConstants.MeetingMonitorPollSeconds), ct); }
            catch (TaskCanceledException) { break; }
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private void Set(RecorderState st) { State = st; StateChanged?.Invoke(this, EventArgs.Empty); }

    private void SetProcessing(RecorderState st, string? dir)
    {
        Processing = st; ProcessingDir = dir;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // A permanently broken track can trip its watchdog every few seconds; keep the
    // "relancée" toast informative, not a stream (one per track per 2 minutes).
    private readonly Dictionary<string, DateTime> _trackNotifyLast = new();

    private void NotifyTrackRestart(string label)
    {
        lock (_trackNotifyLast)
        {
            if (_trackNotifyLast.TryGetValue(label, out var last) && (DateTime.UtcNow - last).TotalSeconds < 120)
                return;
            _trackNotifyLast[label] = DateTime.UtcNow;
        }
        _notifier.Notify($"Piste « {label} » relancée.");
    }

    private async Task SafeCleanupAsync()
    {
        try { if (_audio is not null) await _audio.DisposeAsync(); } catch { }
        _audio = null;
        _sessionCts?.Dispose(); _sessionCts = null;
        _teamsWatch = null;
        _captureLoop = null;
    }

    private static void WriteMeetingJson(string dir, string name, IReadOnlyList<string> roster, bool physical)
    {
        var o = new JsonObject
        {
            ["name"] = name,
            ["date_str"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            ["roster"] = new JsonArray(roster.Select(r => JsonValue.Create(r)).ToArray()),
            ["physical"] = physical,
            ["started"] = DateTimeOffset.Now.ToUnixTimeSeconds(),
        };
        File.WriteAllText(Path.Combine(dir, "meeting.json"), o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Fill a MISSING meeting.json "name" from the subject the Teams watch loop detected during
    /// the call (meeting_title.txt). Only fills a blank name: a subject caught at start or a user
    /// rename is never clobbered. Lets a subject that Teams exposed only after the join screen
    /// still reach the display and the CR.
    /// </summary>
    private void PromoteDetectedTitle(string dir)
    {
        var titlePath = Path.Combine(dir, "meeting_title.txt");
        if (!File.Exists(titlePath)) return;
        var detected = File.ReadAllText(titlePath).Trim();
        if (detected.Length == 0) return;

        var mjp = Path.Combine(dir, "meeting.json");
        JsonObject node;
        try { node = File.Exists(mjp) ? (JsonNode.Parse(File.ReadAllText(mjp))?.AsObject() ?? new()) : new(); }
        catch { node = new JsonObject(); }

        var current = (node["name"]?.GetValue<string>() ?? "").Trim();
        if (current.Length > 0) return;      // real subject / user rename already present

        node["name"] = detected;
        File.WriteAllText(mjp, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        MeetingName = detected;
        Log.Info($"[pipeline] meeting title promoted from watch: {detected}");
    }

    private void WriteSessionJson(string dir, IReadOnlyList<TrackResult> tracks, bool physical)
    {
        // NaN/Infinity are not representable in JSON and make ToJsonString THROW —
        // a degenerate track (0 frame, broken finalize) must not cost the sidecar.
        static double Fin(double v) => double.IsFinite(v) ? v : 0.0;
        var tracksObj = new JsonObject();
        foreach (var t in tracks)
            tracksObj[t.Label] = new JsonObject { ["wav"] = t.WavPath, ["duration_s"] = Fin(t.DurationSeconds), ["error"] = t.Error };
        var o = new JsonObject
        {
            ["out_dir"] = dir,
            ["physical"] = physical,
            ["ended_wall_epoch"] = DateTimeOffset.Now.ToUnixTimeSeconds(),
            ["duration_s"] = Fin(_clock.ElapsedSeconds(System.Diagnostics.Stopwatch.GetTimestamp())),
            ["tracks"] = tracksObj,
        };
        File.WriteAllText(Path.Combine(dir, "session.json"), o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    // .echo_status sentinel: "state\n<pid>\n<epoch>" — drives the "processing" badge.
    // States: running | waiting (parked in the retry queue) | error. See SessionStatus.
    private static void WriteStatus(string dir, string state) => SessionStatus.Write(dir, state);

    private static void ClearStatus(string dir) => SessionStatus.Clear(dir);

    /// <summary>Raw sentinel content (null when absent), to put the badge back as it was.</summary>
    private static string? ReadRawStatus(string dir)
    {
        try
        {
            var p = Path.Combine(dir, SessionStatus.FileName);
            return File.Exists(p) ? File.ReadAllText(p) : null;
        }
        catch { return null; }
    }

    private static void RestoreStatus(string dir, string? raw)
    {
        if (raw is null) { ClearStatus(dir); return; }
        try { File.WriteAllText(Path.Combine(dir, SessionStatus.FileName), raw); } catch { }
    }

    /// <summary>i18n lookup with a French fallback, for keys the string table may not carry
    /// yet (I18n.T returns the raw key when it is missing).</summary>
    private string Tr(string key, string fallbackFr)
    {
        var v = _i18n.T(key);
        return string.IsNullOrEmpty(v) || v == key ? fallbackFr : v;
    }
}
