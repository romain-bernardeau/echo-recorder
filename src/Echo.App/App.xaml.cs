using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Echo.Core;
using Echo.Core.Storage;
using Microsoft.Web.WebView2.Core;
using WinForms = System.Windows.Forms;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace Echo.App;

/// <summary>
/// Application entry point / host. Single-instance tray app: builds the services,
/// creates the shared WebView2 environment, hosts the frameless main window and the
/// floating widget, wires the tray icon, and runs the Teams auto-detect loop.
/// Implements <see cref="IAppShell"/> for the bridge's window/widget operations.
/// </summary>
public partial class App : Application, IAppShell
{
    private Mutex? _mutex;
    private AppServices _services = null!;
    private CoreWebView2Environment _env = null!;
    private MainWindow? _main;
    private WidgetWindow? _widget;
    private WinForms.NotifyIcon? _tray;
    private readonly CancellationTokenSource _cts = new();

    // Window visibility + one-shot "still running in background" balloon (perception of a dead app).
    private bool _mainVisible;
    private bool _widgetVisible;
    private bool _bgNotified;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = new StoragePaths(); // for the log path only; real paths built in AppServices
        Log.Init(paths.LogFile);
        Log.Info($"Echo {EchoConstants.AppVersion} starting.");

        DispatcherUnhandledException += (_, ex) => { Log.Error("dispatcher exception", ex.Exception); ex.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("domain exception", (Exception)ex.ExceptionObject);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("unobserved task", ex.Exception); ex.SetObserved(); };

        // Single instance. ECHO_ALLOW_MULTI=1 bypasses the guard (dev/test only —
        // e.g. running alongside the legacy Python Echo which holds the same mutex).
        _mutex = new Mutex(initiallyOwned: true, EchoConstants.SingleInstanceMutex, out bool createdNew);
        var allowMulti = Environment.GetEnvironmentVariable("ECHO_ALLOW_MULTI") == "1";
        if (!createdNew && !allowMulti)
        {
            MessageBox.Show("Echo est déjà en cours d'exécution.", "Echo",
                MessageBoxButton.OK, MessageBoxImage.Information);
            _exitReason = "second instance";
            Shutdown();
            return;
        }

        // Windows logoff / shutdown: the process is about to be killed. Finalise the capture
        // NOW (stop without transcription; the session is queued for the next launch).
        SessionEnding += OnWindowsSessionEnding;

        _ = InitializeAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info($"[app] exit ({_exitReason ?? "shutdown"}, code {e.ApplicationExitCode})");
        base.OnExit(e);
    }

    private async System.Threading.Tasks.Task InitializeAsync()
    {
        try
        {
            _services = new AppServices();
            Log.Info("services built; creating WebView2 environment.");

            var opts = new CoreWebView2EnvironmentOptions();
            // ECHO_WEBVIEW_DIR overrides the user-data folder (dev/test only — lets the
            // app run beside the legacy Python Echo which locks the default folder).
            var udf = Environment.GetEnvironmentVariable("ECHO_WEBVIEW_DIR");
            if (string.IsNullOrWhiteSpace(udf)) udf = _services.Paths.WebViewDir;
            _env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: udf,
                options: opts);
            Log.Info("WebView2 environment created.");

            // Whole-environment browser-process death: every hosted view dies at once. Each
            // view's ProcessFailed(BrowserProcessExited) drives the actual in-place recreation
            // (WebViewController.HealAsync); we log the environment signal for post-mortem.
            // kind=Normal is the deliberate close (last view disposed on hide/quit): not an error.
            _env.BrowserProcessExited += (_, ev) =>
            {
                var msg = $"WebView2 browser process exited: kind={ev.BrowserProcessExitKind}";
                if (ev.BrowserProcessExitKind == CoreWebView2BrowserProcessExitKind.Normal) Log.Info(msg);
                else Log.Error(msg);
            };

            CreateTray();

            _main = new MainWindow(_services, this, _env);
            _main.HideToTrayRequested += (_, _) => HideMainToTray();

            _widget = new WidgetWindow(_services, this, _env);

            _services.Session.StateChanged += OnSessionStateChanged;

            // Global start/stop hotkey (message-only window; must be created on this UI thread).
            Engine.HotkeyManager.Init(_services);

            _main.Show();
            _main.Activate();
            _mainVisible = true;
            _services.Notifier.SetUiVisible("main", true);

            // Teams auto-detect / auto-stop loop.
            _ = System.Threading.Tasks.Task.Run(() =>
                _services.Session.RunAutoDetectAsync(AskYesNo, _cts.Token));

            // Deferred-retry queue (sessions parked after a 429/5xx): background pump.
            _services.Session.StartQueue(_cts.Token);

            // Orphaned-capture recovery (5 s after launch): folders with WAVs but no
            // session.json (killed app, or the pre-5.2 orphaned-start bug) get repaired
            // headers, sidecars and a session.json so they list and can be transcribed.
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(5), _cts.Token);
                    var r = Echo.Core.Storage.OrphanRecovery.Run(_services.Paths.RecordingsDir, _services.Session.SessionDir, Log.Info);
                    if (r.Recovered > 0)
                        _services.Notifier.Notify(_services.I18n.T("nf_orphans").Replace("{n}", r.Recovered.ToString()));
                }
                catch (Exception ex) { Log.Error("orphan recovery", ex); }
            });

            // Startup update check: after 8 s, if update_auto and update_url are set,
            // check the remote version.json and notify (Python echo_ui.main / tray_app).
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(8), _cts.Token);
                    var s = _services.Settings.Current;
                    if (s.UpdateAuto && !string.IsNullOrWhiteSpace(s.UpdateUrl))
                    {
                        var info = Engine.Updater.Check(s.UpdateUrl!, EchoConstants.AppVersion);
                        if (info is { Available: true })
                        {
                            _services.LastUpdate = info;
                            _services.Notifier.Notify(_services.I18n.T("nf_update_avail").Replace("{v}", info.Version ?? ""));
                        }
                    }
                }
                catch { /* startup check is best-effort */ }
            });

            // Startup disk housekeeping (30 s after launch, off the UI thread): purge
            // regenerable _16k intermediates everywhere, and apply the configured
            // audio retention (audio_retention_days). Log-only — no toast.
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(30), _cts.Token);
                    var s = _services.Settings.Current;
                    // The sweeper logs its own summary line (sessions + MB), even when nothing was freed.
                    Echo.Core.Storage.RetentionSweeper.Sweep(
                        _services.Paths.RecordingsDir, s.AudioRetentionDays, s.FailedAudioRetentionDays,
                        DateTime.Now, Log.Info);
                }
                catch (Exception ex) { Log.Error("retention sweep", ex); /* housekeeping is best-effort */ }
            });

            // Per-user "Installed apps" registration — installed copy only, refreshed each
            // launch so the shown version tracks silent auto-updates. Off-thread, best-effort.
            _ = System.Threading.Tasks.Task.Run(() => Engine.UninstallRegistry.EnsureRegistered());

            Log.Info("Echo initialized.");
        }
        catch (Exception ex)
        {
            Log.Error("initialization failed", ex);
            MessageBox.Show($"Échec du démarrage d'Echo : {ex.Message}\n\nVoir le journal : {_services?.Paths.LogFile}",
                "Echo", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    // ── tray ────────────────────────────────────────────────────────────────

    private void CreateTray()
    {
        Icon icon;
        try { icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application; }
        catch { icon = SystemIcons.Application; }

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(_services.I18n.T("tray_open"), null, (_, _) => Dispatcher.Invoke(ShowMainWindow));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_services.I18n.T("tray_quit"), null, (_, _) => Dispatcher.Invoke(() => QuitApp("tray menu")));

        _tray = new WinForms.NotifyIcon
        {
            Icon = icon,
            Text = "Echo",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMainWindow);

        _services.Notifier.ShowBalloon = msg => Dispatcher.Invoke(() =>
        {
            try
            {
                _tray!.BalloonTipTitle = "Echo";
                _tray.BalloonTipText = msg;
                _tray.ShowBalloonTip(4000);
            }
            catch (Exception ex) { Log.Error("balloon", ex); }
        });
    }

    // ── session state → widget visibility ─────────────────────────────────────

    private void OnSessionStateChanged(object? sender, EventArgs e)
    {
        var recording = _services.Session.IsRecording;
        Dispatcher.Invoke(() =>
        {
            if (recording) ShowWidget();
            else HideWidgetWindow();
            // Also catches the transcribe/CR phase that begins AFTER the widget auto-hides:
            // work continues with no window visible → balloon.
            UpdateBackgroundPresence();
        });
    }

    // ── IAppShell (window / widget / lifecycle) ────────────────────────────────

    public void WinMinimize() => Dispatcher.Invoke(() => { if (_main is not null) _main.WindowState = WindowState.Minimized; });

    public void WinMaximizeToggle() => Dispatcher.Invoke(() =>
    {
        if (_main is null) return;
        _main.WindowState = _main.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    });

    public void WinCloseToTray() => Dispatcher.Invoke(HideMainToTray);

    public void WinResize(double w, double h) => Dispatcher.Invoke(() =>
    {
        if (_main is null) return;
        _main.Width = Math.Max(800, w);
        _main.Height = Math.Max(560, h);
    });

    public bool WidgetShow()
    {
        if (!_services.Session.IsRecording) return false;
        Dispatcher.Invoke(ShowWidget);
        return true;
    }

    public void WidgetHide() => Dispatcher.Invoke(HideWidgetWindow);

    public bool WidgetPin(bool on)
    {
        Dispatcher.Invoke(() => _widget?.SetPinned(on));
        return on;
    }

    public void WidgetResize(double w, double? h) => Dispatcher.Invoke(() => _widget?.ResizeWidget(w, h));

    public void ShowMainWindow() => Dispatcher.Invoke(() =>
    {
        if (_main is null) return;
        _main.Show();
        _main.WindowState = WindowState.Normal;
        _ = _main.EnsureWebViewAsync();   // recreate the WebView2 if it was torn down on hide
        _main.Activate();
        _mainVisible = true;
        _services.Notifier.SetUiVisible("main", true);
        UpdateBackgroundPresence();
    });

    public string? PickTranscriptFile()
    {
        return Dispatcher.Invoke(() =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Importer une réunion (transcript Teams ou fichier audio)",
                Filter = "Transcripts Teams et fichiers audio|*.docx;*.vtt;*.mp3;*.wav;*.m4a;*.aac;*.wma;*.ogg;*.opus;*.flac;*.amr;*.3gp;*.mp4;*.m4v;*.mov;*.mkv;*.webm;*.avi"
                       + "|Transcripts Teams (*.docx;*.vtt)|*.docx;*.vtt"
                       + "|Fichiers audio / vidéo|*.mp3;*.wav;*.m4a;*.aac;*.wma;*.ogg;*.opus;*.flac;*.amr;*.3gp;*.mp4;*.m4v;*.mov;*.mkv;*.webm;*.avi"
                       + "|Tous les fichiers (*.*)|*.*",
            };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        });
    }

    public string? PickVocabFile()
    {
        return Dispatcher.Invoke(() =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Importer un vocabulaire",
                Filter = "Vocabulaire (*.txt;*.md)|*.txt;*.md|Tous les fichiers (*.*)|*.*",
            };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        });
    }

    public string? PickFolder(string? initialDir)
    {
        return Dispatcher.Invoke(() =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Dossier des enregistrements",
            };
            if (!string.IsNullOrWhiteSpace(initialDir) && System.IO.Directory.Exists(initialDir))
                dlg.InitialDirectory = initialDir;
            return dlg.ShowDialog() == true ? dlg.FolderName : null;
        });
    }

    public bool AskYesNo(string message)
    {
        return Dispatcher.Invoke(() =>
            MessageBox.Show(message, "Echo", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
    }

    public void QuitForUpdate() => Dispatcher.Invoke(() => QuitApp("update"));

    // ── helpers ──────────────────────────────────────────────────────────────

    private void ShowWidget()
    {
        if (_widget is null) return;
        _widget.Show();
        _ = _widget.EnsureWebViewAsync();   // lazy-create the widget WebView2 on first show
        _widgetVisible = true;
        _services.Notifier.SetUiVisible("widget", true);
        UpdateBackgroundPresence();
    }

    private void HideWidgetWindow()
    {
        if (_widget is null) return;
        _widget.TeardownWebView();          // release the widget's renderer process while hidden
        _widget.Hide();
        _widgetVisible = false;
        _services.Notifier.SetUiVisible("widget", false);
        UpdateBackgroundPresence();
    }

    private void HideMainToTray()
    {
        var before = WebViewController.MemorySnapshot();
        _services.Notifier.SetUiVisible("main", false);
        _mainVisible = false;
        _main?.TeardownWebView();           // dispose the main WebView2 → recreate on ShowMainWindow
        _main?.Hide();
        Log.Info($"hide main to tray: WS {before.WorkingSetMB} MB, msedgewebview2 x{before.WebViewProcs} (before teardown)");
        // The renderer process exits shortly after Dispose — snapshot again so the gain is visible.
        _ = Dispatcher.InvokeAsync(async () =>
        {
            await System.Threading.Tasks.Task.Delay(1500);
            var after = WebViewController.MemorySnapshot();
            Log.Info($"hide main to tray: WS {after.WorkingSetMB} MB, msedgewebview2 x{after.WebViewProcs} (after teardown)");
        });
        UpdateBackgroundPresence();
    }

    /// <summary>
    /// Perception-of-a-dead-app guard: when the last visible window disappears while work
    /// continues (recording or transcribing/CR), fire ONE tray balloon pointing at the tray
    /// icon. Reset once any window becomes visible again, so it fires at most once per transition.
    /// </summary>
    private void UpdateBackgroundPresence()
    {
        if (_mainVisible || _widgetVisible) { _bgNotified = false; return; }
        bool working = _services.Session.IsBusy;
        if (working && !_bgNotified)
        {
            _bgNotified = true;
            _services.Notifier.Notify(
                "Echo continue en arrière-plan (enregistrement/traitement en cours) : icône près de l'horloge.");
        }
    }

    // ── exit paths ───────────────────────────────────────────────────────────

    private string? _exitReason;
    private bool _quitting;

    /// <summary>Longest wait for the capture to finalise before quitting anyway.</summary>
    private static readonly TimeSpan QuitStopTimeout = TimeSpan.FromSeconds(20);
    /// <summary>Windows gives a few seconds at logoff/shutdown: finalise fast or not at all.</summary>
    private static readonly TimeSpan SessionEndingStopTimeout = TimeSpan.FromSeconds(8);

    private bool English => string.Equals(_services?.Settings.Current.AppLang, "en", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Quit (tray menu, update). Quitting used to cut everything at once: a recording in
    /// progress lost its WAV headers (unreadable until OrphanRecovery) and a running
    /// transcription simply vanished. Now: confirm while busy, finalise the capture (bounded
    /// wait), queue its processing for the next launch, then exit.
    /// </summary>
    private async void QuitApp(string reason)
    {
        if (_quitting) return;
        var sm = _services?.Session;
        try
        {
            if (sm is not null && sm.IsBusy)
            {
                bool capturing = sm.IsRecording || sm.CaptureAlive || sm.IsStopping;
                string msg = (English, capturing, reason == "update") switch
                {
                    (false, true, true) => "Un enregistrement est en cours. Pour installer la mise à jour, Echo va l'arrêter et conserver l'audio ; la transcription se fera au redémarrage.\n\nContinuer ?",
                    (false, true, false) => "Un enregistrement est en cours. Echo va l'arrêter et conserver l'audio avant de quitter ; la transcription se fera au prochain démarrage.\n\nQuitter quand même ?",
                    (false, false, _) => "Un traitement (transcription ou compte rendu) est en cours. Il sera interrompu et repris au prochain démarrage d'Echo.\n\nQuitter quand même ?",
                    (true, true, true) => "A recording is in progress. To install the update, Echo will stop it and keep the audio; transcription will run after the restart.\n\nContinue?",
                    (true, true, false) => "A recording is in progress. Echo will stop it and keep the audio before quitting; transcription will run at the next start.\n\nQuit anyway?",
                    (true, false, _) => "Processing (transcription or minutes) is in progress. It will be interrupted and resumed the next time Echo starts.\n\nQuit anyway?",
                };
                if (MessageBox.Show(msg, "Echo", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    Log.Info($"[app] quit cancelled by the user ({reason}, busy)");
                    return;
                }
            }
            _quitting = true;
            if (sm is not null && (sm.IsRecording || sm.CaptureAlive || sm.IsStopping))
            {
                Log.Info($"[app] quit ({reason}): finalising the recording first");
                var stop = System.Threading.Tasks.Task.Run(() => sm.StopForExitAsync(reason));
                var done = await System.Threading.Tasks.Task.WhenAny(stop, System.Threading.Tasks.Task.Delay(QuitStopTimeout));
                if (done != stop) Log.Error($"[app] quit ({reason}): capture not finalised after {QuitStopTimeout.TotalSeconds:0} s, quitting anyway");
            }
        }
        catch (Exception ex) { Log.Error("quit: stop", ex); }
        _exitReason = reason;
        ShutdownNow();
    }

    private void OnWindowsSessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        _exitReason = $"Windows {e.ReasonSessionEnding}";
        Log.Info($"[app] Windows session ending ({e.ReasonSessionEnding}): finalising");
        _quitting = true;
        try
        {
            var sm = _services?.Session;
            if (sm is not null && (sm.IsRecording || sm.CaptureAlive || sm.IsStopping))
            {
                // Off the UI thread: the stop awaits internally and would deadlock on this
                // dispatcher if we blocked it with a continuation posted back here.
                var stop = System.Threading.Tasks.Task.Run(() => sm.StopForExitAsync(e.ReasonSessionEnding.ToString()));
                if (!stop.Wait(SessionEndingStopTimeout))
                    Log.Error($"[app] capture not finalised within {SessionEndingStopTimeout.TotalSeconds:0} s of the session end");
            }
        }
        catch (Exception ex) { Log.Error("session ending: stop", ex); }
        try { if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); _tray = null; } } catch { }
    }

    private void ShutdownNow()
    {
        try
        {
            _cts.Cancel();
            if (_main is not null) { _main.ForceClose = true; _main.Close(); }
            _widget?.Close();
            if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        }
        catch (Exception ex) { Log.Error("quit", ex); }
        finally
        {
            try { _mutex?.ReleaseMutex(); } catch { }
            Current.Shutdown();
        }
    }
}
