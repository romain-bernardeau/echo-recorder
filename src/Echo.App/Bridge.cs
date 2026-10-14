using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Echo.App.Engine;
using Echo.Core;
using Echo.Core.Models;
using Echo.Core.Storage;

namespace Echo.App;

/// <summary>Window / widget / lifecycle operations the bridge routes to the WPF App.</summary>
public interface IAppShell
{
    void WinMinimize();
    void WinMaximizeToggle();
    void WinCloseToTray();
    void WinResize(double w, double h);
    bool WidgetShow();
    void WidgetHide();
    bool WidgetPin(bool on);
    void WidgetResize(double w, double? h);
    void ShowMainWindow();
    string? PickTranscriptFile();
    /// <summary>Open-file dialog for a vocabulary list (.txt/.md/*.*); null if cancelled.</summary>
    string? PickVocabFile();
    /// <summary>Folder-picker (recordings root); starts at <paramref name="initialDir"/>. Null if cancelled.</summary>
    string? PickFolder(string? initialDir);
    bool AskYesNo(string message);
    /// <summary>Gracefully quit to free locked files so a detached updater can copy + relaunch.</summary>
    void QuitForUpdate();
}

/// <summary>
/// The JS↔native contract (echo_ui.Api, 57 methods). The JS shim posts
/// {id, method, args}; this dispatches to a method table and returns a JSON result.
/// Query methods run synchronously (fast file IO); state-changing engine calls are
/// fired on background tasks and return immediately (Python's per-call thread).
/// </summary>
public sealed class Bridge
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    // get_last_capture is polled by the widget every ~4 s; cache the JPEG data-URI keyed by
    // (path, mtime) so the (expensive) decode+resize+encode only runs when the newest file changes.
    private static readonly object CapCacheGate = new();
    private static string? _capCachePath;
    private static long _capCacheTicks;
    private static string? _capCacheUri;

    private readonly AppServices _s;
    private readonly IAppShell _shell;

    public Bridge(AppServices services, IAppShell shell)
    {
        _s = services;
        _shell = shell;
    }

    /// <summary>Recordings root for the WebView2 virtual-host mapping (see WebViewHost).</summary>
    internal string RecordingsDir => _s.Paths.RecordingsDir;

    /// <summary>Set by <see cref="WebViewHost"/>: re-maps https://echo-recordings/ at once when
    /// the user picks another recordings folder (the audio player used to keep pointing at
    /// the old folder until the view was recreated).</summary>
    internal Action<string>? RecordingsDirChanged { get; set; }

    public async Task<object?> DispatchAsync(string method, JsonElement[] a)
    {
        switch (method)
        {
            // ── live state / meta ──────────────────────────────────────────
            case "get_state": return GetState();
            case "get_meta": return GetMeta();
            case "strings": return new { lang = _s.I18n.Lang, table = _s.I18n.Table };

            // ── sessions list & detail ─────────────────────────────────────
            case "recents": return _s.Query.Recents(IntOr(a, 0, 6));
            case "all_recents": return _s.Query.AllRecents();
            case "total_recents": return _s.Query.Total();
            case "search_all": return await Task.Run(() => _s.Query.SearchAll(Str(a, 0)));
            case "session_detail":
                return SessionDir(Str(a, 0)) is { } detailDir ? _s.Query.Detail(detailDir) : Err("not_found", L("introuvable", "not found"));
            case "read_cr_text": return ReadCrText(Str(a, 0));
            case "rename_session": return RenameSession(Str(a, 0), Str(a, 1));
            case "delete_session": return DeleteSession(Str(a, 0));
            case "rename_speaker": return RenameSpeaker(Str(a, 0), Str(a, 1), Str(a, 2));
            case "audio_clip": return await Task.Run(() => AudioClip(Str(a, 0), DblOr(a, 1, 0), DblOr(a, 2, 8)));
            case "set_attendee_email": return SetAttendeeEmail(Str(a, 0), Str(a, 1), Str(a, 2));
            case "generate_summary": return await GenerateSummaryAsync(Str(a, 0));

            // ── recording controls ─────────────────────────────────────────
            case "start": Fire(() => _s.Session.StartSmartAsync()); return true;
            case "start_physical": Fire(() => _s.Session.StartAsync(physical: true)); return true;
            case "stop": Fire(() => _s.Session.StopAndProcessAsync()); return true;
            case "retranscribe": return Retranscribe(Str(a, 0));
            case "generate": return Generate(Str(a, 0), StrN(a, 1), StrN(a, 2));
            case "export_claude": return ExportClaude(Str(a, 0));
            case "import_teams": return ImportTeams(StrN(a, 0), StrN(a, 1));

            // ── screenshots / files ─────────────────────────────────────────
            case "capture_now": return CaptureNow();
            case "get_last_capture": return GetLastCapture();
            case "open_path": return OpenUserPath(Str(a, 0));
            case "open_folder": ShellOpen(_s.Paths.RecordingsDir); return true;
            case "pick_recordings_dir": return PickRecordingsDir();

            // ── notes ────────────────────────────────────────────────────────
            case "add_note": return AddNote(Str(a, 0), BoolOr(a, 1, false), BoolOr(a, 2, true), StrN(a, 3));
            case "add_capture_note": return AddCaptureNote(Str(a, 0), Str(a, 1), Str(a, 2));
            case "read_notes":
                return SessionDir(Str(a, 0)) is { } notesDir ? SessionQuery.FileText(SessionLayout.NotesPath(notesDir)) : "";
            case "write_notes": return WriteNotes(Str(a, 0), Str(a, 1));

            // ── settings & key ─────────────────────────────────────────────
            case "set_setting": return SetSetting(Str(a, 0), a.Length > 1 ? a[1] : default);
            case "set_lang": _s.Settings.Set("app_lang", Str(a, 0)); return true;
            case "set_autostart": return SetAutostart(BoolOr(a, 0, false));
            case "key_present": return _s.Secret.KeyPresent();
            case "save_key": return SaveKey(Str(a, 0));
            case "test_key": return await TestKeyAsync(StrN(a, 0));
            case "google_key_present": return _s.Secret.GoogleKeyPresent();
            case "save_google_key": return SaveGoogleKey(Str(a, 0));
            case "test_google_key": return await TestGoogleKeyAsync(StrN(a, 0));
            case "cloudflare_key_present": return _s.Secret.CloudflareKeyPresent();
            case "save_cloudflare_key": return SaveCloudflareKey(Str(a, 0));
            case "test_cloudflare_key": return await TestCloudflareKeyAsync(StrN(a, 0), StrN(a, 1));
            case "openrouter_key_present": return _s.Secret.OpenRouterKeyPresent();
            case "save_openrouter_key": return SaveOpenRouterKey(Str(a, 0));
            case "test_openrouter_key": return await TestOpenRouterKeyAsync(StrN(a, 0));
            case "list_models": return await ListModelsAsync(Str(a, 0), StrN(a, 1));
            case "probe_model": return await ProbeModelAsync(Str(a, 0), Str(a, 1));
            case "probe_transcribe_model": return await ProbeTranscribeModelAsync(Str(a, 0), Str(a, 1));

            // ── disk usage ───────────────────────────────────────────────────
            case "storage_stats": return await Task.Run(() => StorageStats());
            case "sweep_storage": return await Task.Run(() => SweepStorage(BoolOr(a, 0, true)));

            // ── business vocabulary (global context_bias.txt + per-mission vocab\<id>.txt) ──
            case "read_vocab": return ReadVocab(StrN(a, 0));
            case "write_vocab": return WriteVocab(Str(a, 0), StrN(a, 1));
            case "import_vocab": return ImportVocab(StrN(a, 0));

            // ── flags / missions ────────────────────────────────────────────
            case "add_flag": return AddFlag(Str(a, 0), StrN(a, 1));
            case "delete_flag": return DeleteFlag(Str(a, 0));
            case "set_session_flag": return SetSessionFlag(Str(a, 0), StrN(a, 1));

            // ── CR templates ────────────────────────────────────────────────
            case "save_template": return SaveTemplate(a.Length > 0 ? a[0] : default);
            case "delete_template": return DeleteTemplate(Str(a, 0));
            case "set_default_template": return SetSetting("cr_template", a.Length > 0 ? a[0] : default);

            // ── help & updates ───────────────────────────────────────────────
            case "check_update": return CheckUpdate();
            // An update or a rollback quits Echo to swap its files: never while a meeting is
            // being recorded or processed (the capture would be cut and the session orphaned).
            case "apply_update":
                if (_s.Session.IsBusy) return Err("busy", L("Enregistrement ou traitement en cours.", "Recording or processing in progress."));
                ApplyUpdate(); return new { ok = true };
            case "rollback":
                if (_s.Session.IsBusy) return Err("busy", L("Enregistrement ou traitement en cours.", "Recording or processing in progress."));
                Rollback(); return new { ok = true };
            case "has_backup": return Updater.HasBackup(AppContext.BaseDirectory);
            case "open_help": return OpenHelp(StrN(a, 0));

            // ── main-window chrome ───────────────────────────────────────────
            case "win_minimize": _shell.WinMinimize(); return true;
            case "win_maximize": _shell.WinMaximizeToggle(); return true;
            case "win_close": _shell.WinCloseToTray(); return true;
            case "win_resize": _shell.WinResize(DblOr(a, 0, 800), DblOr(a, 1, 560)); return true;

            // ── floating widget ─────────────────────────────────────────────
            case "widget_close":
            case "widget_hide": _shell.WidgetHide(); return true;
            case "widget_show": return _shell.WidgetShow();
            case "poll_toasts": return _s.Notifier.Drain();
            case "set_ui_visible": _s.Notifier.SetUiVisible(Str(a, 0), BoolOr(a, 1, true)); return true;
            case "widget_pin": return _shell.WidgetPin(BoolOr(a, 0, true));
            case "widget_pinned": return WidgetPinned();
            case "widget_resize": _shell.WidgetResize(DblOr(a, 0, 360), a.Length > 1 ? DblOr(a, 1, 0) : (double?)null); return true;

            default:
                Log.Error($"bridge: unknown method '{method}'");
                return null;
        }
    }

    // ── live state / meta ──────────────────────────────────────────────────

    // The UI polls get_state every second; cache the key-presence file check (5 s TTL).
    private static bool _keyCache;
    private static DateTime _keyCacheAt = DateTime.MinValue;

    /// <summary>Live state, polled by both pages. Kept cheap on purpose: no file read here
    /// (the old speakers.jsonl read on every poll fed a field no page displayed).</summary>
    private object GetState()
    {
        var sm = _s.Session;
        int elapsed = sm.IsRecording ? (int)sm.ElapsedSeconds : 0;
        // Capture health code ("" | loopback_dead | no_mic | silent_teams) and its warn_* text
        // in the UI language. Only meaningful while recording.
        var health = sm.IsRecording ? (sm.CaptureHealth ?? "") : "";
        var healthMsg = health.Length > 0 ? _s.I18n.T("warn_" + health) : "";
        if (healthMsg == "warn_" + health) healthMsg = "";   // unknown code: no raw key on screen
        if (DateTime.UtcNow - _keyCacheAt > TimeSpan.FromSeconds(5))
        {
            // Key presence for the ACTIVE providers only: a Gemini-only setup must not show
            // "off" because the Mistral key is absent, and vice versa.
            try
            {
                var cur = _s.Settings.Current;
                _keyCache = _s.Secret.KeyPresentFor(SessionManager.TranscriptionProviderOf(cur))
                         && _s.Secret.KeyPresentFor(Echo.Ai.LlmProvider.Normalize(cur.CrProvider));
            }
            catch { _keyCache = false; }
            _keyCacheAt = DateTime.UtcNow;
        }
        return new
        {
            recording = sm.IsRecording,
            physical = sm.Physical,
            elapsed_s = elapsed,
            elapsed_str = SessionQuery.Hms(elapsed),
            // Empty when Teams gave no title: each page shows its own translated default.
            meeting_name = sm.MeetingName ?? "",
            session_dir = sm.SessionDir ?? "",
            health,
            health_msg = healthMsg,
            // Sidebar API indicator: "ok" (discreet green) | "err" (red, help button) | "off"
            // (a key required by an active provider is missing).
            api = !_keyCache ? "off" : (ApiHealth.HasError ? "err" : "ok"),
            api_msg = ApiHealth.Message ?? "",
        };
    }

    private object GetMeta()
    {
        var s = _s.Settings.Current;
        return new
        {
            version = EchoConstants.AppVersion,
            key_present = _s.Secret.KeyPresent(),
            google_key_present = _s.Secret.GoogleKeyPresent(),
            cloudflare_key_present = _s.Secret.CloudflareKeyPresent(),
            openrouter_key_present = _s.Secret.OpenRouterKeyPresent(),
            autostart = AutoStart.IsEnabled(),
            has_backup = Updater.HasBackup(AppContext.BaseDirectory),
            rec_dir = _s.Paths.RecordingsDir,
            // Project the WHOLE settings model (snake_case JSON names via [JsonPropertyName],
            // unknown keys flattened through [JsonExtensionData]). Deriving this from the model
            // instead of a hand-maintained allow-list is deliberate: the old allow-list silently
            // dropped keys added later (audio_retention_days, keep_original_wav), so those values
            // persisted to disk but read back as their default on every restart. Any key the model
            // knows now round-trips to the front-end automatically.
            settings = s,
            flags = s.Flags.Select(f => new { id = f.Id, name = f.Name, color = f.Color }).ToList(),
            templates = s.CrTemplates.Select(t => new
            {
                id = t.Id,
                name = t.Name,
                @base = t.Base,
                instructions = t.Instructions,
                sample = t.Sample,
                builtin = t.Builtin,
            }).ToList(),
            default_template = s.CrTemplateId,
        };
    }

    // ── sessions ───────────────────────────────────────────────────────────

    private object? ReadCrText(string rawPath)
    {
        var path = SessionDir(rawPath);
        if (path is null) return null;
        var crs = SessionLayout.CrFiles(path);
        var mds = crs.Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)).ToList();
        if (mds.Count > 0)
        {
            var p = mds.OrderByDescending(File.GetLastWriteTimeUtc).First();
            return new { kind = "md", text = SessionQuery.ReadText(p), name = Path.GetFileName(p) };
        }
        var emls = crs.Where(f => f.EndsWith(".eml", StringComparison.OrdinalIgnoreCase)).ToList();
        if (emls.Count == 0) return null;
        var e = emls.OrderByDescending(File.GetLastWriteTimeUtc).First();
        try
        {
            var (kind, text) = EmlReader.ReadBody(e);
            return new { kind, text, name = Path.GetFileName(e) };
        }
        catch (Exception ex) { Log.Error("read_cr_text", ex); return null; }
    }

    private bool RenameSession(string rawPath, string name)
    {
        name = (name ?? "").Trim();
        var path = SessionDir(rawPath);
        if (string.IsNullOrEmpty(name) || path is null) return false;
        var mjp = SessionLayout.Find(path, SessionLayout.Meeting);
        try
        {
            var node = File.Exists(mjp)
                ? (System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(mjp))?.AsObject() ?? new())
                : new System.Text.Json.Nodes.JsonObject();
            node["name"] = name;
            WriteAtomic(SessionLayout.ForWrite(path, SessionLayout.Meeting), node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            // The visible files follow the new name ("<Titre> - Compte rendu.docx"…), never overwriting.
            SessionLayout.RenameVisible(path, Log.Info);
            return true;
        }
        catch (Exception ex) { Log.Error("rename_session", ex); return false; }
    }

    private object DeleteSession(string rawPath)
    {
        var path = SessionDir(rawPath);
        // only a meeting folder: Echo's files inside, or its "yyyyMMdd - HHhmm - " name
        if (path is null) return Err("not_found", L("introuvable", "not found"));
        if (!SessionLayout.IsSession(path)
            && !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), @"^\d{8} - \d{2}h\d{2}"))
            return Err("not_found", L("introuvable", "not found"));
        if (_s.Session.IsRecording && SamePath(_s.Session.SessionDir, path))
            return Err("recording_active", L("Réunion en cours d'enregistrement", "Meeting currently being recorded"));
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            return new { ok = true };
        }
        catch (Exception ex) { Log.Error("delete_session", ex); return new { ok = false, error = ex.Message }; }
    }

    /// <summary>
    /// Rename a speaker in the transcript turn HEADERS only ("[12:34] Participant 1 :" on a
    /// line of its own). The old plain substring replace also renamed "Participant 12" into
    /// "Marc2" and rewrote the spoken text wherever the label happened to appear.
    /// </summary>
    private object RenameSpeaker(string rawPath, string oldName, string newName)
    {
        var path = SessionDir(rawPath);
        oldName = (oldName ?? "").Trim();
        newName = (newName ?? "").Trim();
        if (path is null || oldName.Length == 0 || newName.Length == 0 || newName.Contains('\n') || newName.Contains('\r'))
            return Err("invalid_params", L("paramètres invalides", "invalid parameters"));
        try
        {
            var header = SpeakerHeaderRegex(oldName);
            int count = 0;
            foreach (var f in _s.Query.FindFiles(path, "Transcript.txt"))
            {
                var text = File.ReadAllText(f);
                int n = 0;
                var replaced = header.Replace(text, m => { n++; return m.Groups[1].Value + newName + m.Groups[2].Value; });
                if (n == 0) continue;
                WriteAtomic(f, replaced);
                count += n;
            }
            // persist mapping (replayed on regeneration)
            var mapPath = SessionLayout.Find(path, SessionLayout.SpeakerMap);
            var map = new Dictionary<string, string>();
            try { if (File.Exists(mapPath)) map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(mapPath)) ?? new(); } catch { }
            map[oldName] = newName;
            WriteAtomic(SessionLayout.ForWrite(path, SessionLayout.SpeakerMap), JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
            return new { ok = true, count };
        }
        catch (Exception ex) { Log.Error("rename_speaker", ex); return new { ok = false, error = ex.Message }; }
    }

    /// <summary>A whole turn-header line for <paramref name="speaker"/>: "[M:SS]", "[MM:SS]",
    /// "[MMM:SS]" or "[H:MM:SS]", the exact name, then " :". Group 1 = timestamp + spacing,
    /// group 2 = colon and line end (CR kept), so the replacement only swaps the name.</summary>
    internal static System.Text.RegularExpressions.Regex SpeakerHeaderRegex(string speaker)
        => new(@"^(\[\d{1,3}:\d{2}(?::\d{2})?\][ \t]*)" + System.Text.RegularExpressions.Regex.Escape(speaker) + @"([ \t]*:[ \t]*\r?)$",
               System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private bool SetAttendeeEmail(string rawPath, string name, string email)
    {
        var path = SessionDir(rawPath);
        if (path is null || string.IsNullOrWhiteSpace(name)) return false;
        email = (email ?? "").Trim();
        try
        {
            var p = SessionLayout.ForWrite(path, SessionLayout.Emails);
            var map = _s.Query.ReadSessionEmails(path);
            if (string.IsNullOrEmpty(email)) map.Remove(name);
            else map[name] = email;
            WriteAtomic(p, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
            _s.Contacts.Set(name, string.IsNullOrEmpty(email) ? null : email);
            return true;
        }
        catch (Exception ex) { Log.Error("set_attendee_email", ex); return false; }
    }

    /// <summary>Summary with the key of the CR provider actually selected (it used to take the
    /// Mistral key whatever cr_provider said). Returns the summary TEXT, read back from its file.</summary>
    private async Task<object> GenerateSummaryAsync(string rawPath)
    {
        var path = SessionDir(rawPath);
        if (path is null) return Err("not_found", L("introuvable", "not found"));
        var settings = _s.Settings.Current;
        var provider = Echo.Ai.LlmProvider.Normalize(settings.CrProvider);
        var key = _s.Secret.ResolveKeyFor(provider);
        if (string.IsNullOrWhiteSpace(key))
        {
            var name = Echo.Ai.LlmProvider.DisplayName(provider);
            return new
            {
                ok = false, error_code = "key_missing", provider = name,
                error = L($"Clé API {name} absente : enregistrez-la dans Paramètres, Fournisseurs.",
                          $"{name} API key missing: save it in Settings, Providers."),
            };
        }
        try
        {
            var file = await _s.Summarizer.SummarizeAsync(path, key!, settings, CancellationToken.None);
            var text = SessionQuery.FileText(File.Exists(file) ? file : SessionLayout.FindSummary(path) ?? file);
            return new { ok = true, text };
        }
        catch (Exception ex) { Log.Error("generate_summary", ex); return new { ok = false, error = ex.Message }; }
    }

    // ── recording ──────────────────────────────────────────────────────────

    private bool Retranscribe(string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && SessionDir(path) is null) return false;   // outside the recordings folder
        var dir = SessionDir(path) ?? _s.Query.Latest();
        if (dir is null) { _s.Notifier.Notify(_s.I18n.T("nf_none")); return false; }
        Fire(() => _s.Session.ProcessAsync(dir, forceTranscribe: true));   // retranscribe = always re-run Voxtral + CR
        return true;
    }

    private bool Generate(string path, string? templateId, string? lang)
    {
        if (!string.IsNullOrWhiteSpace(path) && SessionDir(path) is null) return false;   // outside the recordings folder
        var dir = SessionDir(path) ?? _s.Query.Latest();
        if (dir is null) { _s.Notifier.Notify(_s.I18n.T("nf_none")); return false; }
        var s = _s.Settings.Current;
        var tpl = s.CrTemplates.FirstOrDefault(t => t.Id == templateId)
                  ?? s.CrTemplates.FirstOrDefault(t => t.Id == s.CrTemplateId)
                  ?? s.CrTemplates.FirstOrDefault();
        var langOverride = string.IsNullOrWhiteSpace(lang) || lang == "auto" ? null : lang;
        Fire(() => _s.Session.ProcessAsync(dir, tpl, langOverride));
        return true;
    }

    private object ExportClaude(string rawPath)
    {
        var path = SessionDir(rawPath);
        if (path is null) return Err("not_found", L("introuvable", "not found"));
        try
        {
            var folder = Path.Combine(path, "Claude");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "Contexte pour Claude.md");
            var sb = new StringBuilder();
            sb.AppendLine("# Contexte pour Claude").AppendLine();
            var transcript = SessionLayout.FindTranscript(path);
            var notes = SessionQuery.FileText(SessionLayout.NotesPath(path));
            if (!string.IsNullOrWhiteSpace(notes))
                sb.AppendLine("## Notes").AppendLine().AppendLine(notes).AppendLine();
            if (transcript is not null)
                sb.AppendLine("## Transcription").AppendLine().AppendLine(SessionQuery.ReadText(transcript));
            File.WriteAllText(file, sb.ToString());
            return new { ok = true, folder, file };
        }
        catch (Exception ex) { Log.Error("export_claude", ex); return new { ok = false, error = ex.Message }; }
    }

    private bool ImportTeams(string? crType, string? lang)
    {
        var f = _shell.PickTranscriptFile();
        if (f is null) return false;
        if (Echo.Ai.AudioImport.IsAudioFile(f)) Fire(() => ImportAudioCore(f, lang));
        else Fire(() => ImportTeamsCore(f, crType, lang));
        return true;
    }

    /// <summary>
    /// Import an external audio/video file as a meeting: decode it into a session folder
    /// (<see cref="Echo.Ai.AudioImport"/>), then run the normal pipeline — transcription, CR
    /// with the user's default template, summary — exactly like a recorded meeting.
    /// </summary>
    private async Task ImportAudioCore(string file, string? lang)
    {
        _s.Notifier.Notify(_s.I18n.T("nf_audio_import_running").Replace("{name}", Path.GetFileName(file)));
        string? dir = null, tmpWav = null;
        try
        {
            var name = Path.GetFileNameWithoutExtension(file).Trim();
            if (name.Length == 0) name = "Import audio";
            // Decode next to the sessions (same volume → cheap move) but as a loose FILE, so the
            // meeting list never shows a half-built session. The folder stamp is the START time,
            // which needs the duration (file time ≈ end of recording on a phone/recorder).
            Directory.CreateDirectory(_s.Paths.RecordingsDir);
            tmpWav = Path.Combine(_s.Paths.RecordingsDir, $"_import-{Guid.NewGuid():N}.wav");
            var wavPath = tmpWav;
            double dur = await Task.Run(() => Echo.Ai.AudioImport.Decode(file, wavPath));
            var start = Echo.Ai.AudioImport.EstimateStart(File.GetLastWriteTime(file), dur, DateTime.Now);
            dir = UniqueDir(Path.Combine(_s.Paths.RecordingsDir, _s.Paths.SessionFolderName(start, name, physical: false)));
            Directory.CreateDirectory(dir);
            Echo.Ai.AudioImport.WriteMetadata(dir, file, name, start, dur);
            File.Move(tmpWav, SessionLayout.ForWrite(dir, "system.wav"));
            tmpWav = null;
            Log.Info($"[import] audio {Path.GetFileName(file)} → {Path.GetFileName(dir)} ({dur:F0} s)");

            var langOverride = string.IsNullOrWhiteSpace(lang) || lang == "auto" ? null : lang;
            await _s.Session.ProcessAsync(dir, template: null, langOverride, forceTranscribe: true);
        }
        catch (Exception ex)
        {
            Log.Error("import_audio", ex);
            if (tmpWav is not null) try { File.Delete(tmpWav); } catch { }
            // A folder holding nothing usable would show up as an empty meeting: drop it.
            if (dir is not null && Echo.Ai.AudioArchive.FindTrackSource(dir, "system") is null)
                try { Directory.Delete(dir, recursive: true); } catch { }
            _s.Notifier.Notify(_s.I18n.T("nf_audio_import_fail").Replace("{name}", Path.GetFileName(file)));
        }
    }

    /// <summary>Shared rule with the recorder: "dir", else "dir (2)", "(3)"… (Echo.Core SessionFiles).</summary>
    private static string UniqueDir(string dir) => Echo.Core.Storage.SessionFiles.UniqueDir(dir);

    /// <summary>
    /// Faithful port of <c>_spawn_teams_cr</c>: parse a native Teams transcript (.docx/.vtt),
    /// build a session folder (meeting.json + a speaker-attributed transcript file in the same
    /// format <c>transcribe</c> writes), then generate the CR via <see cref="SessionManager"/>
    /// (which skips re-transcription because a transcript already exists).
    /// </summary>
    private async Task ImportTeamsCore(string file, string? crType, string? lang)
    {
        _s.Notifier.Notify(_s.I18n.T("nf_teams_running"));
        try
        {
            var importer = _s.Importer;
            if (!importer.CanImport(file)) { _s.Notifier.Notify(_s.I18n.T("nf_fail")); return; }
            var turns = importer.Import(file);
            if (turns.Count == 0) { _s.Notifier.Notify(_s.I18n.T("nf_fail")); return; }

            var meta = importer.TryReadMeta(file);   // name/date from the transcript itself (else filename)
            var name = string.IsNullOrWhiteSpace(meta.Name) ? Path.GetFileNameWithoutExtension(file) : meta.Name!;
            var atts = AttendeesInOrder(turns);

            // UniqueDir: two imports within the same minute (same name) used to land in, and
            // overwrite, the same folder.
            var dir = UniqueDir(Path.Combine(_s.Paths.RecordingsDir, _s.Paths.SessionFolderName(DateTime.Now, name, physical: false)));
            Directory.CreateDirectory(dir);

            var mj = new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = name,
                ["date_str"] = meta.DateStr,
                ["roster"] = new System.Text.Json.Nodes.JsonArray(atts.Select(x => System.Text.Json.Nodes.JsonValue.Create(x)).ToArray()),
                ["physical"] = false,
                ["started"] = DateTimeOffset.Now.ToUnixTimeSeconds(),
            };
            File.WriteAllText(SessionLayout.ForWrite(dir, SessionLayout.Meeting), mj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            File.WriteAllText(SessionLayout.TranscriptPath(dir),
                ToProcessedText(turns, name, meta.DateStr, atts), new UTF8Encoding(false));

            // cr_type: arg else settings.cr_type; lang: arg else settings (auto → no override) — like Python.
            var s = _s.Settings.Current;
            var effectiveType = string.IsNullOrWhiteSpace(crType) ? s.CrType : crType!;
            var baseType = string.Equals(effectiveType, "general", StringComparison.OrdinalIgnoreCase) ? "general" : "detailed";
            var tpl = new CrTemplate { Id = "_teams", Name = "Teams", Base = baseType, Instructions = "", Sample = "", Builtin = false };
            var langOverride = string.IsNullOrWhiteSpace(lang) || lang == "auto" ? null : lang;

            await _s.Session.ProcessAsync(dir, tpl, langOverride);   // notifies completion + opens the folder
        }
        catch (Exception ex) { Log.Error("import_teams", ex); _s.Notifier.Notify(_s.I18n.T("nf_fail")); }
    }

    /// <summary>Speaker order of first appearance, case-insensitive dedupe (Python <c>attendees</c>).</summary>
    private static List<string> AttendeesInOrder(IReadOnlyList<TranscriptTurn> turns)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var t in turns)
        {
            var sp = (t.Speaker ?? "").Trim();
            if (sp.Length > 0 && seen.Add(sp)) order.Add(sp);
        }
        return order;
    }

    /// <summary>Same layout as <c>transcribe.write_processed</c> / <c>teams_transcript.to_processed_text</c>.</summary>
    private static string ToProcessedText(IReadOnlyList<TranscriptTurn> turns, string? name, string? dateStr, IReadOnlyList<string> atts)
    {
        var header = $"{(string.IsNullOrWhiteSpace(name) ? "Reunion" : name)}\n{dateStr ?? ""}\n" +
                     $"Participants: {(atts.Count > 0 ? string.Join(", ", atts) : "n/a")}";
        var lines = new List<string> { header, "" };
        foreach (var t in turns)
        {
            int sec = (int)t.StartS;
            lines.Add($"[{sec / 60:D2}:{sec % 60:D2}] {(string.IsNullOrWhiteSpace(t.Speaker) ? "Unknown" : t.Speaker)} :");
            lines.Add(t.Text);
            lines.Add("");
        }
        return string.Join("\n", lines).Trim() + "\n";
    }

    // ── screenshots ──────────────────────────────────────────────────────────

    private bool CaptureNow()
    {
        var sm = _s.Session;
        string folder = sm.IsRecording && !string.IsNullOrWhiteSpace(sm.SessionDir)
            ? SessionLayout.CapturesDir(sm.SessionDir!)
            : Path.Combine(_s.Paths.RecordingsDir, "Captures manuelles");
        Fire(async () =>
        {
            await Task.Delay(120);   // let a menu/overlay close before grabbing (Python 0.12s)
            var p = ScreenCapture.CaptureNow(folder, "Capture",
                windowOnly: _s.Settings.Current.CaptureArea != "screen");
            _s.Notifier.Notify(_s.I18n.T(p is not null ? "nf_capture_done" : "nf_capture_fail"));
        });
        return true;
    }

    private object? GetLastCapture()
    {
        var sm = _s.Session;
        if (!sm.IsRecording || string.IsNullOrWhiteSpace(sm.SessionDir)) return null;
        var capDir = SessionLayout.CapturesDir(sm.SessionDir!);
        if (!Directory.Exists(capDir)) return null;
        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(capDir)
                .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch { return null; }
        if (files.Count == 0) return null;
        var fp = files.OrderByDescending(File.GetLastWriteTimeUtc).First();
        long ticks; try { ticks = File.GetLastWriteTimeUtc(fp).Ticks; } catch { ticks = 0; }
        string? dataurl;
        lock (CapCacheGate)
        {
            if (_capCacheUri is not null && _capCachePath == fp && _capCacheTicks == ticks)
                dataurl = _capCacheUri;                       // unchanged → reuse the encoded thumbnail
            else
            {
                dataurl = ScreenCapture.ThumbnailDataUri(fp, 420, 70);
                if (dataurl is not null) { _capCachePath = fp; _capCacheTicks = ticks; _capCacheUri = dataurl; }
            }
        }
        if (dataurl is null) return null;
        string ts = "";
        try
        {
            var elapsed = (File.GetLastWriteTimeUtc(fp) - sm.StartedWallUtc).TotalSeconds;
            ts = SessionQuery.Hms((int)Math.Max(0, elapsed));
        }
        catch { ts = ""; }
        return new { name = Path.GetFileName(fp), path = fp, ts, count = files.Count, dataurl };
    }

    // ── audio clip (ffmpeg) ────────────────────────────────────────────────────

    private object AudioClip(string rawPath, double startS, double durS)
    {
        var path = SessionDir(rawPath);
        if (path is null) return Err("not_found", L("introuvable", "not found"));
        // Frame the moment: ~2.5 s of lead-in and ~2.5 s of tail so the passage is heard in
        // context (user feedback 2026-07-17), capped so a clip stays a clip.
        double start = Math.Max(0.0, startS - 2.5);
        double dur = Math.Max(1.0, Math.Min(30.0, durS + 5.0));
        string? src = null;
        // The "… - Audio.mp3" mixdown FIRST: it is kept forever, while per-track audio is
        // purged by the retention policy (seen live: retention=1 day → "audio indisponible"
        // on any meeting older than a day although the mixdown was right there). Raw tracks
        // remain as fallbacks for sessions whose mixdown is not built yet.
        src = SessionLayout.FindAudio(path)
              ?? new[] { "system.wav", "system.mp3", "mic.wav", "mic.mp3" }
                  .Select(n => SessionLayout.Find(path, n)).FirstOrDefault(File.Exists);
        if (src is null) return Err("audio_unavailable", L("audio indisponible", "audio unavailable"));
        var ffmpeg = Echo.Audio.AudioPrep.ResolveFfmpeg();
        if (ffmpeg is null) return Err("audio_extract", L("extraction ffmpeg", "ffmpeg extraction"));
        try
        {
            var psi = new ProcessStartInfo(ffmpeg)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in new[]
            {
                "-nostdin", "-ss", start.ToString("0.00", CultureInfo.InvariantCulture),
                "-t", dur.ToString("0.00", CultureInfo.InvariantCulture),
                "-i", src, "-ac", "1", "-ar", "32000", "-b:a", "64k", "-f", "mp3", "pipe:1",
            }) psi.ArgumentList.Add(arg);

            using var p = Process.Start(psi)!;
            var errTask = p.StandardError.ReadToEndAsync();      // drain stderr so the pipe never blocks
            using var ms = new MemoryStream();
            var copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
            if (!copy.Wait(TimeSpan.FromSeconds(60)))                     // a clip takes well under a second
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return Err("audio_extract", L("extraction ffmpeg", "ffmpeg extraction"));
            }
            errTask.GetAwaiter().GetResult();
            p.WaitForExit();
            if (p.ExitCode != 0 || ms.Length == 0) return Err("audio_extract", L("extraction ffmpeg", "ffmpeg extraction"));
            return new { ok = true, dataurl = "data:audio/mpeg;base64," + Convert.ToBase64String(ms.ToArray()) };
        }
        catch (Exception ex) { Log.Error("audio_clip", ex); return new { ok = false, error = ex.Message }; }
    }

    // ── help & updates ─────────────────────────────────────────────────────────

    private bool OpenHelp(string? anchor = null)
    {
        try { ShellOpen(HelpUrl(_s.Settings.Current.AppLang, anchor)); }   // system browser, not the app view
        catch (Exception ex) { Log.Error("open_help", ex); }
        return true;
    }

    /// <summary>Section ids of the online help, the same in every language (help/README*.md).</summary>
    private static readonly HashSet<string> HelpSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "getting-started", "recording", "after-the-meeting", "api-keys", "mistral", "cloudflare",
        "gemini", "openrouter", "troubleshooting", "settings",
    };

    /// <summary>Tab ids of the former local help page, still accepted from older callers.</summary>
    private static readonly Dictionary<string, string> LegacyHelpAnchors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["prise-en-main"] = "getting-started",
        ["enregistrer"] = "recording",
        ["apres"] = "after-the-meeting",
        ["cle-api"] = "api-keys",
        ["depannage"] = "troubleshooting",
    };

    /// <summary>Online help page for the app language (fr, es, otherwise English), with an
    /// optional section anchor. Unknown anchors open the top of the page.</summary>
    internal static string HelpUrl(string? lang, string? anchor)
    {
        var page = (lang ?? "").Trim().ToLowerInvariant() switch
        {
            "fr" => "README.fr.md",
            "es" => "README.es.md",
            _ => "README.md",
        };
        var a = (anchor ?? "").Trim().TrimStart('#');
        if (LegacyHelpAnchors.TryGetValue(a, out var mapped)) a = mapped;
        var url = EchoConstants.HelpBaseUrl + page;
        return HelpSections.Contains(a) ? url + "#" + a.ToLowerInvariant() : url;
    }

    private object CheckUpdate()
    {
        try
        {
            var s = _s.Settings.Current;
            var info = Updater.Check(s.UpdateUrl ?? "", EchoConstants.AppVersion);
            if (info is { Available: true })
            {
                _s.LastUpdate = info;
                _s.Notifier.Notify(_s.I18n.T("nf_update_avail").Replace("{v}", info.Version ?? ""));
            }
            if (info is null) return new { available = false };
            return new { available = info.Available, version = info.Version, url = info.Url, notes = info.Notes, sha256 = info.Sha256 };
        }
        catch (Exception ex) { Log.Error("check_update", ex); return new { available = false, error = ex.Message }; }
    }

    private void ApplyUpdate() => Fire(async () =>
    {
        var s = _s.Settings.Current;
        var info = _s.LastUpdate is { Available: true } ? _s.LastUpdate : Updater.Check(s.UpdateUrl ?? "", EchoConstants.AppVersion);
        if (info is null || !info.Available || string.IsNullOrWhiteSpace(info.Url))
        {
            _s.Notifier.Notify(_s.I18n.T("nf_update_none"));
            return;
        }
        _s.Notifier.Notify(_s.I18n.T("nf_update_dl"));
        string staging;
        try { staging = await Updater.DownloadAndStageAsync(info.Url!, info.Sha256); }
        catch (Exception ex) { Log.Error("update download", ex); _s.Notifier.Notify(_s.I18n.T("nf_update_fail")); return; }
        // Re-checked after the download (it can take minutes): SpawnApply starts a .cmd that
        // waits for Echo to exit, so it must never run while a meeting is being recorded or
        // processed (the applier would give up after ~3 min and the capture would be cut).
        if (_s.Session.IsBusy) { Log.Info("update postponed: session busy"); _s.Notifier.Notify(_s.I18n.T("nf_update_busy")); return; }
        try { Updater.SpawnApply(staging, AppContext.BaseDirectory); }
        catch (Exception ex) { Log.Error("update apply", ex); _s.Notifier.Notify(_s.I18n.T("nf_update_fail")); return; }
        _shell.QuitForUpdate();   // free locked files; the detached applier copies then relaunches
    });

    private void Rollback() => Fire(() =>
    {
        var appDir = AppContext.BaseDirectory;
        if (!Updater.HasBackup(appDir)) { _s.Notifier.Notify(_s.I18n.T("nf_no_backup")); return Task.CompletedTask; }
        if (_s.Session.IsBusy) { _s.Notifier.Notify(_s.I18n.T("nf_update_busy")); return Task.CompletedTask; }
        try { Updater.SpawnRollback(appDir); }
        catch (Exception ex) { Log.Error("rollback", ex); _s.Notifier.Notify(_s.I18n.T("nf_update_fail")); return Task.CompletedTask; }
        _shell.QuitForUpdate();
        return Task.CompletedTask;
    });

    // ── notes ──────────────────────────────────────────────────────────────

    private bool AddNote(string text, bool flagged, bool withTs, string? capture)
    {
        var dir = _s.Session.SessionDir;
        if (string.IsNullOrWhiteSpace(dir) || !_s.Session.IsRecording) return false;
        if (!string.IsNullOrEmpty(capture)) text = $"[capture: {capture}] {text}";   // Python parity
        double? elapsed = withTs ? _s.Session.ElapsedSeconds : null;
        return AppendNote(dir!, text, flagged, elapsed);
    }

    private bool AddCaptureNote(string rawPath, string captureName, string text)
    {
        var path = SessionDir(rawPath);
        if (path is null) return false;
        var body = $"[capture: {captureName}] {text}";
        return AppendNote(path, body, flagged: false, elapsed: null);
    }

    private static bool AppendNote(string sessionDir, string text, bool flagged, double? elapsed)
    {
        if (string.IsNullOrWhiteSpace(sessionDir) || string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            var p = SessionLayout.NotesPath(sessionDir);
            var ts = elapsed is not null ? SessionQuery.Hms((int)elapsed.Value) : "";
            var prefix = flagged ? "⚑ " : "";
            var line = string.IsNullOrEmpty(ts) ? $"- {prefix}{text.Trim()}" : $"- [{ts}] {prefix}{text.Trim()}";
            bool isNew = !File.Exists(p);
            using var w = new StreamWriter(p, append: true, Encoding.UTF8);
            if (isNew) w.Write("# Notes de reunion\n\n");
            w.Write(line + "\n");
            return true;
        }
        catch (Exception ex) { Log.Error("add_note", ex); return false; }
    }

    private bool WriteNotes(string rawPath, string md)
    {
        var path = SessionDir(rawPath);
        if (path is null) return false;
        try { WriteAtomic(SessionLayout.NotesPath(path), md ?? ""); return true; }
        catch (Exception ex) { Log.Error("write_notes", ex); return false; }
    }

    // ── settings & key ──────────────────────────────────────────────────────

    /// <summary>Every JSON key of <see cref="AppSettings"/>, derived from its [JsonPropertyName]
    /// attributes. Derived on purpose, never hand-listed: a hand-maintained key list already
    /// once dropped settings added later (see Bridge.GetMeta).</summary>
    private static readonly HashSet<string> SettingKeys = typeof(AppSettings).GetProperties()
        .Select(p => p.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute), false)
                      .OfType<System.Text.Json.Serialization.JsonPropertyNameAttribute>().FirstOrDefault()?.Name)
        .Where(n => !string.IsNullOrEmpty(n))
        .Select(n => n!)
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>Settings the page must never write: where updates come from, the folder every path
    /// guard is anchored to (changed only through the native folder picker), and internal state.
    /// A script injected in the page would otherwise widen its reach through them.</summary>
    private static readonly HashSet<string> NativeOnlyKeys = new(StringComparer.Ordinal)
    {
        "update_url", "recordings_dir", "key_store", "setup_done", "models_migration",
    };

    private bool SetSetting(string key, JsonElement value)
    {
        if (string.IsNullOrWhiteSpace(key) || !SettingKeys.Contains(key) || NativeOnlyKeys.Contains(key))
        {
            Log.Error($"set_setting: unknown key '{key}' refused");
            return false;
        }
        object? boxed = value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : value;
        try { _s.Settings.Set(key, boxed); }
        catch (Exception ex) { Log.Error($"set_setting {key}", ex); return false; }
        if (key is "hotkey" or "hotkey_enabled") Engine.HotkeyManager.Reapply();
        return true;
    }

    /// <summary>Let the user pick a new recordings root, persist it (<c>recordings_dir</c>) and
    /// create it. Returns the new directory, or null if cancelled / on error.</summary>
    private object? PickRecordingsDir()
    {
        var chosen = _shell.PickFolder(_s.Paths.RecordingsDir);
        if (string.IsNullOrWhiteSpace(chosen)) return null;
        if (StoragePaths.IsUnsafeRecordingsRoot(chosen))
        {
            Log.Info($"pick_recordings_dir refused: {chosen}");
            _s.Notifier.Notify(L("Choisissez un dossier dédié aux enregistrements (par exemple Documents\\Echo), pas la racine d'un disque, un dossier réseau ou un dossier système.",
                              "Choose a folder dedicated to recordings (for example Documents\\Echo), not a drive root, a network folder or a system folder."));
            return null;
        }
        try
        {
            Directory.CreateDirectory(chosen);
            _s.Settings.Set("recordings_dir", chosen);   // StoragePaths.RecordingsDir reads this override live
            var dir = _s.Paths.RecordingsDir;
            RecordingsDirChanged?.Invoke(dir);          // the audio player follows the new folder now
            return new { dir };
        }
        catch (Exception ex) { Log.Error("pick_recordings_dir", ex); return null; }
    }

    private bool SetAutostart(bool enable)
    {
        var ok = AutoStart.Set(enable);
        if (ok) _s.Settings.Set("autostart", enable);
        return ok;
    }

    private bool SaveKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        try { _s.Secret.Save(key); return true; }
        catch (Exception ex) { Log.Error("save_key", ex); return false; }
    }

    private bool SaveGoogleKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        try { _s.Secret.SaveGoogle(key); return true; }
        catch (Exception ex) { Log.Error("save_google_key", ex); return false; }
    }

    private bool SaveCloudflareKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        try { _s.Secret.SaveCloudflare(key); return true; }
        catch (Exception ex) { Log.Error("save_cloudflare_key", ex); return false; }
    }

    private bool SaveOpenRouterKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        try { _s.Secret.SaveOpenRouter(key); return true; }
        catch (Exception ex) { Log.Error("save_openrouter_key", ex); return false; }
    }

    /// <summary>Validate an OpenRouter key (GET /key, spends nothing), then probe the configured
    /// CR model so the user learns at once whether the CR can run there.</summary>
    private async Task<object> TestOpenRouterKeyAsync(string? key)
    {
        var k = string.IsNullOrWhiteSpace(key) ? _s.Secret.ResolveOpenRouterKey() : key!.Trim();
        if (string.IsNullOrWhiteSpace(k))
            return new { ok = false, msg = L("Clé OpenRouter absente.", "OpenRouter key missing."), tier = "unknown", cr_model_ok = false };
        try
        {
            var probe = await CatalogOpenRouter.ProbeKeyAsync(k, CancellationToken.None);
            if (!probe.Ok)
            {
                var failMsg = probe.Status is 401 or 403
                    ? L("Clé OpenRouter refusée (HTTP " + probe.Status + "). Vérifiez la clé sur openrouter.ai/keys.",
                        "OpenRouter key refused (HTTP " + probe.Status + "). Check the key on openrouter.ai/keys.")
                    : StatusMessage(probe.Status, probe.ServerMessage, null);
                return new { ok = false, msg = failMsg, tier = "unknown", cr_model_ok = false, status = probe.Status };
            }
            var crModel = Echo.Ai.LlmProvider.NonEmpty(_s.Settings.Current.OpenRouterCrModel, EchoConstants.DefaultOpenRouterCrModel);
            var cr = await CatalogOpenRouter.ProbeChatModelAsync(k, crModel, CancellationToken.None);
            string msg = cr.Ok
                ? L($"Clé valide. Le modèle CR {crModel} répond.", $"Valid key. The minutes model {crModel} answers.")
                : L($"Clé valide. Modèle CR {crModel} non vérifié ({ProbeDetail(cr)}).",
                    $"Valid key. Minutes model {crModel} not verified ({ProbeDetail(cr)}).");
            return new { ok = true, msg, tier = "unknown", cr_model_ok = cr.Ok, cr_model = crModel, status = cr.Status };
        }
        catch (Exception ex)
        {
            Log.Error("test_openrouter_key", ex);
            return new { ok = false, msg = L("Test OpenRouter échoué : ", "OpenRouter test failed: ") + ex.Message, tier = "unknown", cr_model_ok = false };
        }
    }

    /// <summary>
    /// Validate a Cloudflare Workers AI token together with the account id (the REST URL
    /// needs both): stores a non-empty <paramref name="accountId"/> first, then makes one
    /// account-scoped models/search call, caches the catalogue, and probes the configured
    /// chat model so the user learns at once whether the CR can run there.
    /// </summary>
    private async Task<object> TestCloudflareKeyAsync(string? key, string? accountId)
    {
        if (!string.IsNullOrWhiteSpace(accountId) && accountId!.Trim() != (_s.Settings.Current.CloudflareAccountId ?? ""))
            _s.Settings.Set("cloudflare_account_id", accountId.Trim());
        var k = string.IsNullOrWhiteSpace(key) ? _s.Secret.ResolveCloudflareKey() : key!.Trim();
        if (string.IsNullOrWhiteSpace(k))
            return new { ok = false, msg = L("Jeton Cloudflare absent.", "Cloudflare token missing."), tier = "unknown", cr_model_ok = false };
        if (string.IsNullOrWhiteSpace(_s.Settings.Current.CloudflareAccountId))
            return new { ok = false, msg = L("Identifiant de compte Cloudflare absent (page Workers AI du tableau de bord).",
                                             "Cloudflare account id missing (Workers AI page of the dashboard)."), tier = "unknown", cr_model_ok = false };
        try
        {
            var probe = await CatalogCloudflare.ProbeAccountAsync(k, CancellationToken.None);
            if (!probe.Ok)
            {
                var failMsg = probe.Status switch
                {
                    401 or 403 => L("Jeton Cloudflare refusé (HTTP " + probe.Status + "). Le jeton doit avoir la permission Workers AI (lecture et modification).",
                                    "Cloudflare token refused (HTTP " + probe.Status + "). The token needs the Workers AI permission (read and edit)."),
                    404 => L("Compte Cloudflare introuvable (404). Vérifiez l'identifiant de compte.",
                             "Cloudflare account not found (404). Check the account id."),
                    _ => StatusMessage(probe.Status, probe.ServerMessage, null),
                };
                return new { ok = false, msg = failMsg, tier = "unknown", cr_model_ok = false, status = probe.Status };
            }
            try
            {
                var models = await CatalogCloudflare.ListModelsAsync(k, CancellationToken.None);
                CacheModels(Echo.Ai.LlmProvider.Cloudflare, k, models);
            }
            catch { /* listing is best-effort here */ }

            var crModel = Echo.Ai.LlmProvider.NonEmpty(_s.Settings.Current.CloudflareCrModel, EchoConstants.DefaultCloudflareCrModel);
            var cr = await CatalogCloudflare.ProbeChatModelAsync(k, crModel, CancellationToken.None);
            string msg = cr.Ok
                ? L($"Jeton et compte valides. Le modèle CR {crModel} répond.", $"Token and account valid. The minutes model {crModel} answers.")
                : L($"Jeton et compte valides. Modèle CR {crModel} non vérifié ({ProbeDetail(cr)}).",
                    $"Token and account valid. Minutes model {crModel} not verified ({ProbeDetail(cr)}).");
            return new { ok = true, msg, tier = "unknown", cr_model_ok = cr.Ok, cr_model = crModel, status = cr.Status };
        }
        catch (Exception ex)
        {
            Log.Error("test_cloudflare_key", ex);
            return new { ok = false, msg = L("Test Cloudflare échoué : ", "Cloudflare test failed: ") + ex.Message, tier = "unknown", cr_model_ok = false };
        }
    }

    /// <summary>Validate a Google AI Studio (Gemini) key with a read-only models list call.</summary>
    private async Task<object> TestGoogleKeyAsync(string? key)
    {
        var k = string.IsNullOrWhiteSpace(key) ? _s.Secret.ResolveGoogleKey() : key!.Trim();
        if (string.IsNullOrWhiteSpace(k))
            return new { ok = false, msg = _s.I18n.T("gkey_invalid").Replace("{e}", L("clé absente", "key missing")) };
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                "https://generativelanguage.googleapis.com/v1beta/models");
            req.Headers.TryAddWithoutValidation("x-goog-api-key", k);   // header, not URL
            using var resp = await Http.SendAsync(req);
            if (resp.IsSuccessStatusCode) return new { ok = true, msg = _s.I18n.T("gkey_valid") };
            return new { ok = false, msg = _s.I18n.T("gkey_invalid").Replace("{e}", ((int)resp.StatusCode).ToString()) };
        }
        catch (Exception ex)
        {
            return new { ok = false, msg = _s.I18n.T("gkey_invalid").Replace("{e}", ex.Message) };
        }
    }

    /// <summary>Validate a Mistral key (GET /v1/models), then probe the configured CR model so
    /// the user learns right away whether their tier can run it. <c>tier</c> = "free" when the
    /// CR model answers 403 (not in this subscription tier), "paid" when it answers 200,
    /// "unknown" otherwise (429, network).</summary>
    private async Task<object> TestKeyAsync(string? key)
    {
        var k = string.IsNullOrWhiteSpace(key) ? _s.Secret.ResolveKey() : key!.Trim();
        if (string.IsNullOrWhiteSpace(k))
            return new { ok = false, msg = _s.I18n.T("key_invalid").Replace("{e}", L("clé absente", "key missing")), tier = "unknown", cr_model_ok = false };
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.mistral.ai/v1/models");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", k);
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                var failMsg = _s.I18n.T("key_invalid").Replace("{e}", ((int)resp.StatusCode).ToString());
                ApiHealth.Report(failMsg);
                return new { ok = false, msg = failMsg, tier = "unknown", cr_model_ok = false };
            }
            ApiHealth.Ok();
            // Cache the catalogue while we have the body (same call the UI would make next).
            try
            {
                var body = await resp.Content.ReadAsStringAsync();
                CacheModels(Echo.Ai.LlmProvider.Mistral, k, Echo.Ai.MistralClient.ParseModelList(body));
            }
            catch { /* listing is best-effort here */ }

            var crModel = Echo.Ai.LlmProvider.NonEmpty(_s.Settings.Current.MistralCrModel, Echo.Ai.MinutesGenerator.DefaultModel);
            var probe = await CatalogMistral.ProbeChatModelAsync(k, crModel, CancellationToken.None);
            string tier = probe.Ok ? "paid" : probe.Status == 403 ? "free" : "unknown";
            string msg = probe.Ok
                ? L($"Clé valide. Le modèle CR {crModel} est disponible sur cette clé.",
                    $"Valid key. The minutes model {crModel} is available on this key.")
                : probe.Status == 403
                    ? L($"Clé valide. Palier gratuit : {crModel} indisponible, choisissez un modèle marqué Free (ministral-8b-latest) ou Cloudflare.",
                        $"Valid key. Free tier: {crModel} unavailable, pick a model tagged Free (ministral-8b-latest) or Cloudflare.")
                    : probe.Status == 429
                        ? L($"Clé valide mais saturée (429) : le modèle {crModel} n'a pas pu être vérifié. Réessayez dans une minute.",
                            $"Valid key but saturated (429): the model {crModel} could not be checked. Try again in a minute.")
                        : L($"Clé valide. Modèle {crModel} non vérifié ({ProbeDetail(probe)}).",
                            $"Valid key. Model {crModel} not verified ({ProbeDetail(probe)}).");
            return new { ok = true, msg, tier, cr_model_ok = probe.Ok, cr_model = crModel, status = probe.Status };
        }
        catch (Exception ex)
        {
            var failMsg = _s.I18n.T("key_invalid").Replace("{e}", ex.Message);
            ApiHealth.Report(failMsg);
            return new { ok = false, msg = failMsg, tier = "unknown", cr_model_ok = false };
        }
    }

    // ── model catalogue & probe (Settings "Providers & models" card) ────────────

    private static readonly Echo.Ai.MistralClient CatalogMistral = new(Http);
    private static readonly Echo.Ai.GeminiClient CatalogGemini = new(Http);
    private static readonly Echo.Ai.OpenRouterClient CatalogOpenRouter = new(Http);
    private Echo.Ai.CloudflareClient? _catalogCloudflare;
    /// <summary>Account-scoped, hence per Bridge (reads the account id from settings at call time).</summary>
    private Echo.Ai.CloudflareClient CatalogCloudflare
        => _catalogCloudflare ??= new(() => _s.Settings.Current.CloudflareAccountId, Http);

    // 10-minute cache per (provider, key): the Settings page re-renders often and every
    // model <select> asks for the same list.
    private static readonly TimeSpan ModelCacheTtl = TimeSpan.FromMinutes(10);
    private static readonly object ModelCacheGate = new();
    private static readonly Dictionary<string, (DateTime At, IReadOnlyList<Echo.Ai.ModelInfo> Models)> ModelCache = new();

    /// <summary>Pick the FR or EN text according to the UI language (these strings live here
    /// rather than in strings.json, which belongs to another workstream).</summary>
    private string L(string fr, string en) => _s.I18n.Lang == "en" ? en : fr;

    private static string ModelCacheKey(string provider, string apiKey)
    {
        // Never keep the raw key as a dictionary key: hash it.
        var h = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        return provider.ToLowerInvariant() + ":" + Convert.ToHexString(h, 0, 8);
    }

    private static void CacheModels(string provider, string apiKey, IReadOnlyList<Echo.Ai.ModelInfo> models)
    {
        lock (ModelCacheGate) ModelCache[ModelCacheKey(provider, apiKey)] = (DateTime.UtcNow, models);
    }

    private static IReadOnlyList<Echo.Ai.ModelInfo>? CachedModels(string provider, string apiKey)
    {
        lock (ModelCacheGate)
        {
            if (ModelCache.TryGetValue(ModelCacheKey(provider, apiKey), out var e) && DateTime.UtcNow - e.At < ModelCacheTtl)
                return e.Models;
            return null;
        }
    }

    /// <summary>Resolve (catalogue client, key) for a provider name; key = explicit arg or the stored one.</summary>
    private (Echo.Ai.IModelCatalog? Client, string? Key, string Provider) ResolveProvider(string provider, string? explicitKey)
    {
        var p = (provider ?? "").Trim().ToLowerInvariant();
        if (p == Echo.Ai.LlmProvider.Gemini)
            return (CatalogGemini, string.IsNullOrWhiteSpace(explicitKey) ? _s.Secret.ResolveGoogleKey() : explicitKey!.Trim(), p);
        if (p == Echo.Ai.LlmProvider.Mistral)
            return (CatalogMistral, string.IsNullOrWhiteSpace(explicitKey) ? _s.Secret.ResolveKey() : explicitKey!.Trim(), p);
        if (p == Echo.Ai.LlmProvider.Cloudflare)
            return (CatalogCloudflare, string.IsNullOrWhiteSpace(explicitKey) ? _s.Secret.ResolveCloudflareKey() : explicitKey!.Trim(), p);
        if (p == Echo.Ai.LlmProvider.OpenRouter)
            return (CatalogOpenRouter, string.IsNullOrWhiteSpace(explicitKey) ? _s.Secret.ResolveOpenRouterKey() : explicitKey!.Trim(), p);
        return (null, null, p);
    }

    /// <summary>list_models(provider, key?) → { ok, models:[{id,chat,transcribe,free}], msg }.
    /// OpenRouter's catalogue is public: it is listed even before a key is stored.</summary>
    private async Task<object> ListModelsAsync(string provider, string? explicitKey)
    {
        var (client, key, p) = ResolveProvider(provider, explicitKey);
        if (client is null)
            return new { ok = false, models = Array.Empty<object>(), msg = L("Fournisseur inconnu.", "Unknown provider.") };
        if (string.IsNullOrWhiteSpace(key))
        {
            if (p != Echo.Ai.LlmProvider.OpenRouter)
                return new { ok = false, models = Array.Empty<object>(), msg = L("Enregistrez une clé pour charger la liste.", "Save a key to load the list.") };
            key = "";
        }
        try
        {
            var models = CachedModels(p, key);
            if (models is null)
            {
                models = await client.ListModelsAsync(key, CancellationToken.None);
                CacheModels(p, key, models);
            }
            return new
            {
                ok = true,
                models = models.Select(m => new { id = m.Id, chat = m.Chat, transcribe = m.Transcribe, free = m.Free }).ToList(),
                msg = "",
            };
        }
        catch (Echo.Ai.LlmApiException ex)
        {
            Log.Error($"list_models {p}: HTTP {ex.StatusCode} {ex.ServerMessage}");
            return new { ok = false, models = Array.Empty<object>(), msg = StatusMessage(ex.StatusCode, ex.ServerMessage, null) };
        }
        catch (Exception ex)
        {
            Log.Error("list_models", ex);
            return new { ok = false, models = Array.Empty<object>(), msg = L("Liste indisponible : ", "List unavailable: ") + ex.Message };
        }
    }

    /// <summary>probe_model(provider, model) → { ok, status, msg } (never throws).</summary>
    private async Task<object> ProbeModelAsync(string provider, string model)
    {
        var (client, key, p) = ResolveProvider(provider, null);
        model = (model ?? "").Trim();
        if (client is null) return new { ok = false, status = 0, msg = L("Fournisseur inconnu.", "Unknown provider.") };
        if (model.Length == 0) return new { ok = false, status = 0, msg = L("Nom de modèle vide.", "Empty model name.") };
        if (string.IsNullOrWhiteSpace(key))
            return new { ok = false, status = 0, msg = L("Aucune clé enregistrée pour ce fournisseur.", "No key stored for this provider.") };
        try
        {
            var r = await client.ProbeChatModelAsync(key, model, CancellationToken.None);
            var msg = r.Ok
                ? L("Modèle disponible.", "Model available.")
                : StatusMessage(r.Status, r.ServerMessage, model);
            return new { ok = r.Ok, status = r.Status, msg };
        }
        catch (Exception ex)
        {
            Log.Error("probe_model", ex);
            return new { ok = false, status = 0, msg = ex.Message };
        }
    }

    /// <summary>
    /// Check a TRANSCRIPTION model for real, by sending it half a second of silence the way a
    /// recording would be sent. Before this, an unsupported model (Workers AI does not use one
    /// request format for all its Whisper models) only surfaced as an HTTP 400 once the meeting
    /// was over and the audio already recorded — 2026-09-18, one hour of meeting lost that way.
    /// Only Cloudflare needs the live probe: Mistral and Gemini take one request shape.
    /// </summary>
    private async Task<object> ProbeTranscribeModelAsync(string provider, string model)
    {
        var p = Echo.Ai.LlmProvider.Normalize(provider);
        model = (model ?? "").Trim();
        if (model.Length == 0) return new { ok = false, msg = L("Nom de modèle vide.", "Empty model name.") };
        if (p != Echo.Ai.LlmProvider.Cloudflare)
            return new { ok = true, msg = "" };                  // nothing to verify, stay silent

        var key = _s.Secret.ResolveKeyFor(p);
        if (string.IsNullOrWhiteSpace(key))
            return new { ok = false, msg = L("Aucun jeton Cloudflare enregistré.", "No Cloudflare token stored.") };
        if (string.IsNullOrWhiteSpace(_s.Settings.Current.CloudflareAccountId))
            return new { ok = false, msg = L("Identifiant de compte Cloudflare absent.", "Cloudflare account id missing.") };
        try
        {
            var r = await CatalogCloudflare.ProbeTranscribeModelAsync(key!, model, CancellationToken.None);
            Log.Info($"[models] probe transcription cloudflare/{model} → ok={r.Ok} {r.Message}");
            return new { ok = r.Ok, msg = r.Message, timestamps = r.Timestamps };
        }
        catch (Exception ex)
        {
            Log.Error("probe_transcribe_model", ex);
            return new { ok = false, msg = ex.Message };
        }
    }

    // ── disk usage ───────────────────────────────────────────────────────────

    /// <summary>Recordings folder weight, the breakdown, the heaviest sessions, and how much
    /// the current retention rules would reclaim right now (measured, nothing deleted).</summary>
    private object StorageStats()
    {
        var dir = _s.Paths.RecordingsDir;
        var s = _s.Settings.Current;
        var rep = Echo.Core.Storage.StorageReport.Build(dir);
        var dry = Echo.Core.Storage.RetentionSweeper.Sweep(
            dir, s.AudioRetentionDays, s.FailedAudioRetentionDays, DateTime.Now, log: null, dryRun: true);
        return new
        {
            dir,
            total = rep.TotalBytes,
            raw = rep.RawTrackBytes,
            archive = rep.TrackArchiveBytes,
            mixdown = rep.MixdownBytes,
            other = rep.OtherBytes,
            sessions = rep.SessionCount,
            untranscribed = rep.UntranscribedCount,
            untranscribed_bytes = rep.UntranscribedBytes,
            reclaimable = dry.BytesFreed,
            reclaimable_sessions = dry.SessionsSwept,
            heaviest = rep.Heaviest.Select(h => new
            {
                name = h.Name, path = h.Path, bytes = h.Bytes, has_tr = h.HasTranscript,
            }).ToArray(),
        };
    }

    /// <summary>Apply the retention rules now (or measure them with <paramref name="apply"/>=false).</summary>
    private object SweepStorage(bool apply)
    {
        var s = _s.Settings.Current;
        var r = Echo.Core.Storage.RetentionSweeper.Sweep(
            _s.Paths.RecordingsDir, s.AudioRetentionDays, s.FailedAudioRetentionDays,
            DateTime.Now, apply ? Log.Info : null, dryRun: !apply);
        if (apply) Log.Info($"[retention] nettoyage manuel : {r.SessionsSwept} session(s), {r.BytesFreed / (1024 * 1024)} Mo.");
        return new { sessions = r.SessionsSwept, bytes = r.BytesFreed };
    }

    private string ProbeDetail(Echo.Ai.ModelProbeResult r)
        => r.Status == 0 ? (r.ServerMessage ?? L("réseau", "network")) : $"HTTP {r.Status}";

    /// <summary>Actionable message for an HTTP status coming from a catalogue / probe call.</summary>
    private string StatusMessage(int status, string? serverMessage, string? model)
    {
        var detail = string.IsNullOrWhiteSpace(serverMessage) ? "" : " " + serverMessage!.Trim();
        return status switch
        {
            403 => L("Modèle indisponible sur le palier de cette clé. Choisissez un autre modèle ou un autre fournisseur.",
                     "Model unavailable on this key's tier. Pick another model or another provider."),
            429 => L("Clé saturée (429). Attendez une minute puis réessayez.", "Key saturated (429). Wait a minute and retry."),
            401 => L("Clé refusée (401). Vérifiez la clé enregistrée.", "Key refused (401). Check the stored key."),
            404 => L($"Modèle introuvable{(model is null ? "" : " : " + model)} (404). Vérifiez l'identifiant.",
                     $"Model not found{(model is null ? "" : ": " + model)} (404). Check the identifier."),
            400 => L("Requête refusée (400)." + detail, "Request refused (400)." + detail),
            0 => L("Fournisseur injoignable." + detail, "Provider unreachable." + detail),
            >= 500 => L($"Service indisponible (HTTP {status}). Réessayez plus tard.", $"Service unavailable (HTTP {status}). Try again later."),
            _ => L($"Refusé (HTTP {status}).", $"Refused (HTTP {status}).") + detail,
        };
    }

    // ── business vocabulary (global context_bias.txt + per-mission vocab\<id>.txt) ──

    /// <summary>The GLOBAL client lexicon (all missions), alongside settings.json in
    /// %APPDATA%\Echo. Selected by an empty/null mission id (legacy behaviour). Its contents
    /// are user-supplied and never redistributed.</summary>
    private string GlobalVocabPath => Path.Combine(_s.Paths.SettingsDir, "context_bias.txt");

    /// <summary>Resolve the vocab file for an optional mission id. Empty/null id -> the global
    /// <c>context_bias.txt</c>; a non-empty id -> <c>%APPDATA%\Echo\vocab\&lt;id&gt;.txt</c>
    /// (per-mission, layered ON TOP at transcribe/generate time). Path-traversal guarded:
    /// returns null for an unsafe id.</summary>
    private string? VocabPathFor(string? missionId)
    {
        var id = (missionId ?? "").Trim();
        if (id.Length == 0) return GlobalVocabPath;
        if (id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        return Path.Combine(_s.Paths.SettingsDir, "vocab", id + ".txt");
    }

    /// <summary>Commented starter shown when a vocab file does not exist yet.
    /// Returned by <see cref="ReadVocab"/> WITHOUT creating the file.</summary>
    private const string VocabStarter =
        "# Vocabulaire metier : corrections de transcription (fichier LOCAL).\n" +
        "# Un terme (ou une correction) par ligne. Les lignes commencant par # sont ignorees.\n" +
        "#\n" +
        "# Deux formats :\n" +
        "#   • terme simple                -> SteerCo\n" +
        "#   • variantes deformees=>voulu  -> stirco|stir co=>SteerCo\n" +
        "#\n" +
        "# Exemples (decommentez / adaptez a votre mission) :\n" +
        "# stirco|stir co|steer co=>SteerCo\n" +
        "# comex|co mex=>COMEX\n" +
        "# Nom du projet ou du client\n";

    private object ReadVocab(string? missionId)
    {
        var path = VocabPathFor(missionId);
        if (path is null) return new { path = "", text = VocabStarter };
        try
        {
            return File.Exists(path)
                ? new { path, text = File.ReadAllText(path) }
                : new { path, text = VocabStarter };
        }
        catch (Exception ex) { Log.Error("read_vocab", ex); return new { path, text = VocabStarter }; }
    }

    private object WriteVocab(string text, string? missionId)
    {
        var path = VocabPathFor(missionId);
        if (path is null) return Err("invalid_mission", L("identifiant de mission invalide", "invalid mission id"));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);   // creates %APPDATA%\Echo\vocab on the first per-mission write
            File.WriteAllText(path, text ?? "", new UTF8Encoding(false));
            _s.Vocab.Reload();   // refresh the shared (global) deterministic pass; per-mission files are read fresh per session
            // Parsed feedback for the save toast: what the engine ACTUALLY reads from
            // this file — a mistyped line (e.g. "->" instead of "=>") shows up as a
            // missing correction count instead of failing silently.
            var entries = Echo.Ai.Lexicon.LoadEntries(new[] { path });
            return new { ok = true, terms = entries.Count, corrections = entries.Count(e => e.Variants.Count > 0) };
        }
        catch (Exception ex) { Log.Error("write_vocab", ex); return new { ok = false, error = ex.Message }; }
    }

    private object ImportVocab(string? missionId)
    {
        var path = VocabPathFor(missionId);
        if (path is null) return new { ok = false, added = 0, error_code = "invalid_mission", error = L("identifiant de mission invalide", "invalid mission id") };
        var src = _shell.PickVocabFile();
        if (src is null) return new { ok = false, added = 0 };
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var existingText = File.Exists(path) ? File.ReadAllText(path) : "";
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in existingText.Split('\n'))
            {
                var t = l.Trim();
                if (t.Length > 0) seen.Add(t);
            }
            var toAdd = new List<string>();
            foreach (var raw in File.ReadLines(src))
            {
                var t = raw.Trim();
                if (t.Length == 0) continue;
                if (seen.Add(t)) toAdd.Add(t);   // de-dupe against existing + within the import
            }
            if (toAdd.Count == 0) return new { ok = true, added = 0 };
            var sb = new StringBuilder(existingText);
            if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
            foreach (var line in toAdd) sb.Append(line).Append('\n');
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            _s.Vocab.Reload();
            return new { ok = true, added = toAdd.Count };
        }
        catch (Exception ex) { Log.Error("import_vocab", ex); return new { ok = false, added = 0, error = ex.Message }; }
    }

    // ── flags ──────────────────────────────────────────────────────────────

    // Flags and templates are lists inside the settings model: mutate them through
    // ISettingsStore.Update (a clone under the store lock), never in place on Current, which
    // other threads may be reading or saving at the same moment.

    private bool AddFlag(string name, string? color)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return false;
        try
        {
            _s.Settings.Update(s => s.Flags.Add(new Flag
            {
                Id = "f" + DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                Name = name,
                Color = string.IsNullOrWhiteSpace(color) ? EchoConstants.DefaultFlagColor : color!,
            }));
            return true;
        }
        catch (Exception ex) { Log.Error("add_flag", ex); return false; }
    }

    private bool DeleteFlag(string fid)
    {
        try { _s.Settings.Update(s => s.Flags.RemoveAll(f => f.Id == fid)); return true; }
        catch (Exception ex) { Log.Error("delete_flag", ex); return false; }
    }

    private bool SetSessionFlag(string rawPath, string? fid)
    {
        var path = SessionDir(rawPath);
        if (path is null) return false;
        _s.Query.SetSessionFlag(path, fid);
        return true;
    }

    // ── templates ────────────────────────────────────────────────────────────

    private bool SaveTemplate(JsonElement tpl)
    {
        if (tpl.ValueKind != JsonValueKind.Object) return false;
        try
        {
            string? id = tpl.TryGetProperty("id", out var idE) && idE.ValueKind == JsonValueKind.String ? idE.GetString() : null;
            string name = tpl.TryGetProperty("name", out var nE) && nE.ValueKind == JsonValueKind.String ? nE.GetString() ?? "" : "";
            string bas = tpl.TryGetProperty("base", out var bE) && bE.ValueKind == JsonValueKind.String ? bE.GetString() ?? "detailed" : "detailed";
            bas = bas == "general" ? "general" : "detailed";
            string instr = tpl.TryGetProperty("instructions", out var iE) && iE.ValueKind == JsonValueKind.String ? iE.GetString() ?? "" : "";
            string sample = tpl.TryGetProperty("sample", out var sE) && sE.ValueKind == JsonValueKind.String ? sE.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(name)) return false;

            _s.Settings.Update(s =>
            {
                var existing = id is not null ? s.CrTemplates.FirstOrDefault(t => t.Id == id) : null;
                if (existing is not null)
                {
                    existing.Name = name;
                    existing.Base = bas;
                    existing.Instructions = instr;
                    existing.Sample = sample;
                    // builtin flag preserved
                }
                else
                {
                    s.CrTemplates.Add(new CrTemplate
                    {
                        Id = "t" + DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                        Name = name,
                        Base = bas,
                        Instructions = instr,
                        Sample = sample,
                        Builtin = false,
                    });
                }
            });
            return true;
        }
        catch (Exception ex) { Log.Error("save_template", ex); return false; }
    }

    private bool DeleteTemplate(string tid)
    {
        try
        {
            _s.Settings.Update(s =>
            {
                var t = s.CrTemplates.FirstOrDefault(x => x.Id == tid);
                if (t is null || t.Builtin) return;
                s.CrTemplates.Remove(t);
                if (s.CrTemplateId == tid) s.CrTemplateId = "detailed";
            });
            return true;
        }
        catch (Exception ex) { Log.Error("delete_template", ex); return false; }
    }

    // ── path guards ───────────────────────────────────────────────────────────
    // Every path the page sends is canonicalised and must sit inside the recordings folder:
    // the bridge deletes folders, rewrites files and opens documents, so a path that merely
    // "exists" is not enough.

    private static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try { return Echo.Core.Storage.SessionFiles.SamePath(a, b); }
        catch { return false; }
    }

    /// <summary>Canonical form of <paramref name="path"/> when it lies inside the recordings
    /// folder (the folder itself only if <paramref name="allowRoot"/>); null otherwise.</summary>
    private string? UnderRecordings(string? path, bool allowRoot)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains("://")) return null;
        try
        {
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            var root = Path.GetFullPath(_s.Paths.RecordingsDir).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return allowRoot ? full : null;
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch { return null; }
    }

    /// <summary>An existing meeting folder strictly inside the recordings folder, canonical; else null.</summary>
    private string? SessionDir(string? path)
    {
        var full = UnderRecordings(path, allowRoot: false);
        return full is not null && Directory.Exists(full) ? full : null;
    }

    private static object Err(string code, string fallback) => new { ok = false, error_code = code, error = fallback };

    // ── shell helpers ─────────────────────────────────────────────────────────

    /// <summary>The only file types the page may open through the shell: what Echo writes in a
    /// meeting folder (minutes, transcript, notes, audio, captures). An allow-list, because the
    /// list of types that run something when opened (.chm, .xll, .iso, .rdp...) never ends.</summary>
    private static readonly HashSet<string> OpenableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".eml", ".docx", ".md", ".txt", ".pdf", ".json", ".vtt", ".csv",
        ".mp3", ".wav", ".m4a", ".ogg", ".flac", ".png", ".jpg", ".jpeg",
    };

    /// <summary>open_path from the page: an http(s) link, or an existing folder or file of an
    /// allowed type inside the recordings folder.</summary>
    private bool OpenUserPath(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return false;
        if (Uri.TryCreate(p, UriKind.Absolute, out var u) && !u.IsFile)
        {
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)
            {
                Log.Error($"open_path refused (scheme): {u.Scheme}");
                return false;
            }
            ShellOpen(u.AbsoluteUri);
            return true;
        }
        var full = UnderRecordings(p, allowRoot: true);
        if (full is null || !(File.Exists(full) || Directory.Exists(full)))
        {
            Log.Error($"open_path refused (outside the recordings folder or missing): {p}");
            return false;
        }
        if (full.IndexOf(':', 2) >= 0                                       // alternate data stream
            || File.Exists(full) && !OpenableExtensions.Contains(Path.GetExtension(full)))
        {
            Log.Error($"open_path refused (file type): {p}");
            return false;
        }
        ShellOpen(full);
        return true;
    }

    /// <summary>Shell-open a path the APP chose (recordings root, online help): no guard.</summary>
    private static void ShellOpen(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return;
        try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error($"open_path {p}", ex); }
    }

    /// <summary>Write via a temp file then an atomic rename: a crash mid-write never leaves a
    /// truncated transcript, notes or metadata file behind.</summary>
    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    /// <summary>The widget's real "always on top" state (its page is recreated on every show).</summary>
    private static bool WidgetPinned()
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app is null) return true;
            return app.Dispatcher.Invoke(() => app.Windows.OfType<WidgetWindow>().FirstOrDefault()?.Topmost ?? true);
        }
        catch { return true; }
    }

    private static void Fire(Func<Task> work) => _ = Task.Run(async () =>
    {
        try { await work(); }
        catch (Exception ex) { Log.Error("bridge bg task", ex); }
    });

    // ── argument helpers ────────────────────────────────────────────────────

    private static string Str(JsonElement[] a, int i) => StrN(a, i) ?? "";

    private static string? StrN(JsonElement[] a, int i)
    {
        if (i >= a.Length) return null;
        return a[i].ValueKind switch
        {
            JsonValueKind.String => a[i].GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => a[i].ToString(),
        };
    }

    private static int IntOr(JsonElement[] a, int i, int def)
    {
        if (i >= a.Length) return def;
        return a[i].ValueKind == JsonValueKind.Number && a[i].TryGetInt32(out var v) ? v
             : a[i].ValueKind == JsonValueKind.String && int.TryParse(a[i].GetString(), out var v2) ? v2
             : def;
    }

    private static double DblOr(JsonElement[] a, int i, double def)
    {
        if (i >= a.Length) return def;
        return a[i].ValueKind == JsonValueKind.Number && a[i].TryGetDouble(out var v) ? v
             : a[i].ValueKind == JsonValueKind.String && double.TryParse(a[i].GetString(), out var v2) ? v2
             : def;
    }

    private static bool BoolOr(JsonElement[] a, int i, bool def)
    {
        if (i >= a.Length) return def;
        return a[i].ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(a[i].GetString(), out var b) ? b : def,
            _ => def,
        };
    }
}
