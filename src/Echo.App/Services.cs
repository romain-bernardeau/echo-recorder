using Echo.Ai;
using Echo.App.Engine;
using Echo.Audio;
using Echo.Core.Abstractions;
using Echo.Core.Storage;
using Echo.Teams;

namespace Echo.App;

/// <summary>
/// Composition root — builds every concrete service and wires the
/// <see cref="SessionManager"/> orchestrator (see docs/contract/platform.md).
/// Resolves the circular StoragePaths↔SettingsStore dependency (paths needs the
/// current recordings dir, which lives in settings).
/// </summary>
public sealed class AppServices
{
    public StoragePaths Paths { get; }
    public SettingsStore Settings { get; }
    public SecretStore Secret { get; }
    public ContactsStore Contacts { get; }
    public I18n I18n { get; }
    public SessionQuery Query { get; }
    public TrayNotifier Notifier { get; }
    public SessionManager Session { get; }
    public MistralClient Mistral { get; }
    public CloudflareClient Cloudflare { get; }
    public Vocabulary Vocab { get; }
    public TeamsMonitor Teams { get; }
    public TeamsTranscriptImporter Importer { get; }
    public MinutesGenerator Minutes { get; }
    public Summarizer Summarizer { get; }

    /// <summary>Last available update detected by check/startup (shared with apply), mirroring
    /// the Python <c>_update_info</c> module global.</summary>
    public Echo.Core.Models.UpdateInfo? LastUpdate { get; set; }

    public AppServices()
    {
        SettingsStore settings = null!;
        var paths = new StoragePaths(() => settings?.Current.RecordingsDir);
        settings = new SettingsStore(paths);
        // Trace every settings write (and every failed read) into echo.log: "my choice was
        // not saved" must be answerable from the log at the moment of the click.
        settings.Log = Log.Info;
        var secret = new SecretStore(paths, settings);
        var contacts = new ContactsStore(paths);

        // Startup settings fingerprint — pins the file-vs-runtime mystery (2026-07-17: the app
        // ran with auto_minutes=false / capture_mode!=endpoint although settings.json held
        // true/endpoint). Logs the EXACT path read + what Load() produced, so the next launch
        // shows whether the file, the path, or a downstream reader is at fault.
        try
        {
            var sc = settings.Current;
            // RAW disk view read in the SAME process at the SAME instant: if these differ from
            // Current, the store diverges from its own file (a bug); if raw ALREADY shows the
            // "wrong" values while an outside shell sees others, the process is served a
            // DIFFERENT file content for the same path (container/virtualization redirection —
            // the 2026-07-17 15:33 startup read auto_minutes=false from a file holding true).
            string rawAm = "?", rawCm = "?", rawLen = "?";
            try
            {
                var raw = System.IO.File.ReadAllText(paths.SettingsFile);
                rawLen = raw.Length.ToString();
                var m1 = System.Text.RegularExpressions.Regex.Match(raw, "\"auto_minutes\"\\s*:\\s*(\\w+)");
                var m2 = System.Text.RegularExpressions.Regex.Match(raw, "\"capture_mode\"\\s*:\\s*\"([^\"]*)\"");
                if (m1.Success) rawAm = m1.Groups[1].Value;
                if (m2.Success) rawCm = m2.Groups[1].Value;
            }
            catch (Exception rex) { rawAm = "read-error: " + rex.GetType().Name; }
            Log.Info($"[settings] startup: file={paths.SettingsFile} exists={System.IO.File.Exists(paths.SettingsFile)} " +
                     $"auto_transcribe={sc.AutoTranscribe} auto_minutes={sc.AutoMinutes} capture_mode={sc.CaptureMode} " +
                     $"raw(auto_minutes={rawAm}, capture_mode={rawCm}, len={rawLen}) " +
                     $"recordings_dir={paths.RecordingsDir}");
        }
        catch (Exception ex) { Log.Error("settings fingerprint", ex); }

        paths.EnsureBaseDirs();

        var i18n = new I18n(() => settings.Current.AppLang);
        var notifier = new TrayNotifier();
        var query = new SessionQuery(paths, contacts, i18n);

        // Retry traces (429/5xx backoff) go to echo.log — diagnosable, not toast spam.
        // On a rate limit (429), also surface ONE throttled toast: a free-tier key can turn
        // post-meeting processing into a several-minute wait, and a silent "traitement en
        // cours" reads as a freeze (user feedback 2026-07-16). Throttled to 4 min.
        var lastRateToast = DateTime.MinValue;
        Action<string> retryLog = msg =>
        {
            Log.Info(msg);
            if (msg.Contains("limite de débit", StringComparison.Ordinal))
            {
                var now = DateTime.UtcNow;
                if (now - lastRateToast > TimeSpan.FromMinutes(4))
                {
                    lastRateToast = now;
                    notifier.Notify("L'API limite le débit (quota de la clé). Le traitement continue, "
                                    + "cela peut prendre quelques minutes.");
                }
            }
        };
        var mistral = new MistralClient(retryLog: retryLog);
        // The key's plan does not include the requested model and Mistral answered with
        // another one: always logged, toasted at most every 30 min (one per meeting otherwise).
        var lastModelToast = DateTime.MinValue;
        mistral.ModelFallback += fb =>
        {
            Log.Info($"[providers] {fb.Provider} : modèle {fb.RequestedModel} indisponible ({fb.Reason}), {fb.UsedModel} utilisé à la place");
            var now = DateTime.UtcNow;
            if (now - lastModelToast < TimeSpan.FromMinutes(30)) return;
            lastModelToast = now;
            var text = i18n.T("nf_model_fallback");
            if (string.IsNullOrEmpty(text) || text == "nf_model_fallback")
                text = "Le modèle {requested} n'est pas inclus dans l'offre de votre clé Mistral : {used} a été utilisé à la place. "
                       + "Vous pouvez changer de modèle dans Paramètres > Fournisseurs.";
            notifier.Notify(text.Replace("{requested}", fb.RequestedModel).Replace("{used}", fb.UsedModel));
        };
        // Optional Gemini backend (same retry/toast behaviour); only exercised when the
        // user selects the Gemini provider for transcription and/or CR in Settings.
        // transcription_lang: "auto" imposes nothing, "fr"/"en" are passed to the model.
        var gemini = new GeminiClient(retryLog: retryLog, language: () => settings.Current.TranscriptionLang);
        // Cloudflare Workers AI (Whisper + OpenAI-compatible chat). The account id is read
        // from settings at call time so a change in the Settings page applies at once.
        var cloudflare = new CloudflareClient(() => settings.Current.CloudflareAccountId, retryLog: retryLog,
            // transcription_lang (NOT cr_lang, the language the minutes are WRITTEN in): "auto"
            // lets Whisper detect; an explicit FR/EN choice is passed on, which keeps a French
            // meeting from being decoded as English on a noisy passage.
            language: () => settings.Current.TranscriptionLang is "fr" or "en" ? settings.Current.TranscriptionLang : null);
        var vocab = new Vocabulary();
        var transcriber = new Transcriber(mistral, mistral, gemini, gemini, vocab,
            cloudflareTranscribe: cloudflare, cloudflareChat: cloudflare);
        var minutes = new MinutesGenerator(mistral, gemini, vocab, cloudflareChat: cloudflare);
        var summarizer = new Summarizer(mistral, gemini, cloudflareChat: cloudflare);
        // Log wired (it was NOT: `new TeamsMonitor()` left every [Echo.Teams] diagnostic
        // silent in prod — the 2026-07-16 compact-view meeting left no trace). Alert = the
        // rare user-facing toast (compact view → "agrandissez la fenêtre").
        var teams = new TeamsMonitor(log: Log.Info);
        teams.Alert += msg => notifier.Notify(msg);
        var importer = new TeamsTranscriptImporter();

        // Audio events: "[Echo.Audio] …" messages are diagnostics (loopback drift every
        // 30 s, restart details) — log-only. The rest (mode fallback, inaudible mic/
        // participants) are real user alerts and go through the notifier.
        Action<string> audioNotify = msg =>
        {
            if (msg.StartsWith("[Echo.Audio]", StringComparison.Ordinal)) Log.Info(msg);
            else notifier.Notify(msg);
        };
        Func<IAudioCaptureService> audioFactory =
            () => new AudioCaptureService(settings.Current, meetingActive: teams.InMeeting, notify: audioNotify);

        var session = new SessionManager(audioFactory, teams, transcriber, minutes, summarizer,
            vocab, settings, secret, paths, notifier, i18n);

        Paths = paths;
        Settings = settings;
        Secret = secret;
        Contacts = contacts;
        I18n = i18n;
        Query = query;
        Notifier = notifier;
        Session = session;
        Mistral = mistral;
        Cloudflare = cloudflare;
        Vocab = vocab;
        Teams = teams;
        Importer = importer;
        Minutes = minutes;
        Summarizer = summarizer;
    }
}
