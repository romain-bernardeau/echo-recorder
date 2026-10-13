namespace Echo.Core.Storage;

/// <summary>
/// Startup tidy-up of the meeting folders into <see cref="SessionLayout"/>, run BEFORE the
/// orphan recovery, the retry queue and the retention sweep. Per session folder:
///  1. every technical file at the root moves into the hidden .echo\ (json, status, tracks,
///     intermediates, Infos réunion.txt, flag.txt…);
///  2. transcripts: with a "Processed Transcript" the raw one goes to .echo\transcript-raw.txt and
///     the corrected one becomes "&lt;Titre&gt; - Transcript.txt"; without, the raw one takes that name;
///  3. CR ("Minutes"/"Summary"/"Compte rendu", any format) become "&lt;Titre&gt; - Compte rendu.*"
///     (EN "Minutes"), resume.md becomes "&lt;Titre&gt; - Résumé.md" (EN "Summary"), the mixdown
///     "&lt;Titre&gt; - Audio.mp3": the language is app_lang at the time of the migration.
/// Only MOVES: never a copy, never an overwrite (an existing target leaves the source where it
/// is, logged), never a delete. A session being processed by this Echo is skipped, a move that
/// fails (locked file) is logged and the session is retried at the next start. Idempotent: a
/// finished folder carries .echo\layout and is not looked at again. The folder's own modified
/// time is restored so the meeting list keeps its order.
/// </summary>
public static class SessionLayoutMigration
{
    public sealed record Result(int Sessions, int Moved, int Skipped);

    public static Result Run(string recordingsDir, string? appLang, Action<string>? log = null)
    {
        int sessions = 0, moved = 0, skipped = 0;
        if (!Directory.Exists(recordingsDir)) return new Result(0, 0, 0);
        List<string> dirs;
        try { dirs = Directory.EnumerateDirectories(recordingsDir).ToList(); }
        catch { return new Result(0, 0, 0); }

        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            try
            {
                if (SessionLayout.IsLayoutDone(dir) || !LooksLikeSession(dir)) continue;
                if (IsLive(dir))
                {
                    log?.Invoke($"[layout] {name} : traitement en cours, rangement remis au prochain démarrage.");
                    continue;
                }
                var (m, k) = MigrateSession(dir, appLang, log);
                moved += m; skipped += k;
                if (m > 0) sessions++;
            }
            catch (Exception ex) { log?.Invoke($"[layout] {name} : {ex.Message}"); }
        }
        log?.Invoke($"[layout] {sessions} session(s) rangée(s), {moved} fichier(s) déplacé(s), {skipped} ignoré(s).");
        return new Result(sessions, moved, skipped);
    }

    private static bool LooksLikeSession(string dir)
        => SessionLayout.IsSession(dir) || SessionLayout.Exists(dir, SessionLayout.Session);

    /// <summary>Same reading as the sweeper: running/waiting for THIS process; unreadable = live.</summary>
    private static bool IsLive(string dir)
    {
        try
        {
            return SessionStatus.IsLive(SessionStatus.Read(dir), Environment.ProcessId,
                                        DateTimeOffset.Now.ToUnixTimeSeconds());
        }
        catch { return true; }
    }

    private static (int Moved, int Skipped) MigrateSession(string dir, string? lang, Action<string>? log)
    {
        var folder = Path.GetFileName(dir);
        DateTime? stamp = null;
        try { stamp = Directory.GetLastWriteTimeUtc(dir); } catch { }

        int moved = 0, skipped = 0;
        bool failed = false;
        // A previous run that could not finish already turned resume.md into an English
        // "… - Summary.md": in that folder a Summary.md is the short summary, not a CR.
        bool resumed = SessionLayout.IsMigrated(dir);
        var hidden = SessionLayout.EnsureHidden(dir);
        SessionLayout.WriteMarker(dir, "partial");

        void Move(string src, string dst)
        {
            switch (SessionLayout.TryMove(src, dst))
            {
                case SessionLayout.MoveResult.Moved: moved++; break;
                case SessionLayout.MoveResult.Collision:
                    skipped++;
                    log?.Invoke($"[layout] {folder} : « {Path.GetFileName(src)} » laissé en place, « {Path.GetFileName(dst)} » existe déjà.");
                    break;
                case SessionLayout.MoveResult.Failed:
                    skipped++; failed = true;
                    log?.Invoke($"[layout] {folder} : « {Path.GetFileName(src)} » non déplacé (fichier verrouillé ?), nouvel essai au prochain démarrage.");
                    break;
            }
        }

        // 1. Technical files into .echo\.
        foreach (var f in Directory.EnumerateFiles(dir).ToList())
            if (SessionLayout.IsTechnical(Path.GetFileName(f)))
                Move(f, Path.Combine(hidden, Path.GetFileName(f)));

        // 2. Transcripts.
        var visible = Visible(dir);
        var processed = Newest(visible, "Processed Transcript");
        var raw = Newest(visible, "Transcript");
        var teams = Newest(visible, "Teams Transcript");
        var target = SessionLayout.TranscriptPath(dir);
        if (processed is not null)
        {
            if (raw is not null) Move(raw, SessionLayout.Tech(dir, SessionLayout.RawTranscript));
            Move(processed, target);
        }
        else if (raw is not null) Move(raw, target);
        else if (teams is not null) Move(teams, target);

        // 3. CR, summary, mixdown and any other visible product: full title, current language.
        // (In a folder not migrated yet, "Summary.*" is a CR: the short summary was resume.md.)
        foreach (var (f, v) in Visible(dir))
        {
            bool isSummary = v.Label == "Résumé"
                             || (resumed && v.Label == "Summary" && v.Ext.Equals(".md", StringComparison.OrdinalIgnoreCase));
            var label = isSummary ? SessionLayout.SummaryLabel(lang)
                : v.Label is "Compte rendu" or "Minutes" or "Summary" ? SessionLayout.CrLabel(lang)
                : Path.GetFileName(f)[(v.Prefix.Length + 3)..^v.Ext.Length];   // other products: label kept as written
            Move(f, SessionLayout.Visible(dir, label, v.Ext));
        }
        var legacySummary = Path.Combine(dir, SessionLayout.LegacySummary);
        if (File.Exists(legacySummary))
            Move(legacySummary, SessionLayout.SummaryPath(dir, lang));

        if (!failed) SessionLayout.WriteMarker(dir, SessionLayout.Version);
        try { if (stamp is { } t) Directory.SetLastWriteTimeUtc(dir, t); } catch { }
        if (moved > 0) log?.Invoke($"[layout] {folder} : {moved} fichier(s) rangé(s).");
        return (moved, skipped);
    }

    private static List<(string File, (string Prefix, string Label, string Ext) V)> Visible(string dir)
        => Directory.EnumerateFiles(dir)
            .Select(f => (f, SessionLayout.SplitVisible(Path.GetFileName(f))))
            .Where(x => x.Item2 is not null)
            .Select(x => (x.f, x.Item2!.Value))
            .ToList();

    private static string? Newest(List<(string File, (string Prefix, string Label, string Ext) V)> files, string label)
        => files.Where(x => x.V.Label == label).Select(x => x.File)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
}
