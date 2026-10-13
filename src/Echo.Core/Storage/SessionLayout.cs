using System.Text.Json;
using System.Text.RegularExpressions;

namespace Echo.Core.Storage;

/// <summary>
/// THE disposition of a meeting folder, and the only code that knows it.
/// At the root, what a user opens: "&lt;Titre&gt; - Compte rendu.*" (EN "Minutes"),
/// "&lt;Titre&gt; - Transcript.txt" (the corrected one), "&lt;Titre&gt; - Résumé.md" (EN "Summary"),
/// "&lt;Titre&gt; - Audio.mp3", notes.md and Captures\. Everything technical (json sidecars,
/// status, tracks, intermediates, raw transcript, logs) lives in the hidden ".echo" sub-folder.
/// Writes always go to the new place; reads try it first, then the folder root, where sessions
/// recorded before this layout kept everything until <see cref="SessionLayoutMigration"/> ranges them.
/// </summary>
public static class SessionLayout
{
    public const string HiddenDirName = ".echo";
    /// <summary>".echo\layout": "1" once the folder follows this layout, "partial" while a
    /// migration could not move everything (retried at the next start).</summary>
    public const string MarkerFile = "layout";
    public const string Version = "1";

    // Technical files, in .echo\.
    public const string Meeting = "meeting.json";
    public const string Session = "session.json";
    public const string Speakers = "speakers.jsonl";
    public const string Roster = "roster.jsonl";
    public const string MeetingTitle = "meeting_title.txt";
    public const string TranscriptJson = "transcript.json";
    public const string PostprocessLog = "postprocess_log.json";
    public const string Info = "Infos réunion.txt";
    public const string Emails = "emails.json";
    public const string Flag = "flag.txt";
    public const string SpeakerMap = "speaker_map.json";
    /// <summary>Raw (uncorrected) transcript, kept aside once the corrected one exists.</summary>
    public const string RawTranscript = "transcript-raw.txt";
    /// <summary>Pre-layout name of the short summary, at the root.</summary>
    public const string LegacySummary = "resume.md";

    // Visible, at the root.
    public const string Notes = "notes.md";
    public const string CapturesDirName = "Captures";

    public const int MaxTitle = 80;

    public static readonly string[] Tags = { "mic", "system" };

    private static readonly HashSet<string> TechnicalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        Meeting, Session, Speakers, Roster, MeetingTitle, TranscriptJson, PostprocessLog,
        SessionStatus.FileName, Info, "Infos reunion.txt", Emails, Flag, SpeakerMap,
    };

    // mic/system track, sidecar, _16k prep files and _cN upload pieces.
    private static readonly Regex TrackFileRx = new(@"^(mic|system)((_16k(_trim)?(_c\d+)?)?\.(wav|mp3)|\.json)$",
                                                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // ── technical files ──────────────────────────────────────────────────────

    public static string HiddenDir(string dir) => Path.Combine(dir, HiddenDirName);

    /// <summary>New location of a technical file (does not create anything).</summary>
    public static string Tech(string dir, string name) => Path.Combine(dir, HiddenDirName, name);

    /// <summary>Where to WRITE a technical file: .echo\name, the folder being created if needed.</summary>
    public static string ForWrite(string dir, string name)
    {
        EnsureHidden(dir);
        return Tech(dir, name);
    }

    /// <summary>Where to READ a technical file: .echo\name, else the legacy root copy, else
    /// .echo\name (absent).</summary>
    public static string Find(string dir, string name)
    {
        var p = Tech(dir, name);
        if (File.Exists(p)) return p;
        var legacy = Path.Combine(dir, name);
        return File.Exists(legacy) ? legacy : p;
    }

    public static bool Exists(string dir, string name) => File.Exists(Find(dir, name));

    /// <summary>Delete a technical file wherever it is (new and legacy place).</summary>
    public static void Delete(string dir, string name)
    {
        foreach (var p in new[] { Tech(dir, name), Path.Combine(dir, name) })
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }
    }

    /// <summary>Create .echo (Hidden attribute) and return its path. A folder with no file at
    /// its root is a NEW session: it is marked as following the layout from the start.</summary>
    public static string EnsureHidden(string dir)
    {
        var h = HiddenDir(dir);
        if (Directory.Exists(h)) return h;
        bool fresh;
        try { fresh = !Directory.EnumerateFiles(dir).Any(); } catch { fresh = false; }
        var di = Directory.CreateDirectory(h);
        try { di.Attributes |= FileAttributes.Hidden; } catch { /* cosmetic */ }
        if (fresh) WriteMarker(dir, Version);
        return h;
    }

    /// <summary>The folder has (started to) follow this layout. Decides what an English
    /// "… - Summary.md" is: the short summary here, a compte rendu in a legacy folder.</summary>
    public static bool IsMigrated(string dir) => File.Exists(Tech(dir, MarkerFile));

    internal static bool IsLayoutDone(string dir)
    {
        try { return File.ReadAllText(Tech(dir, MarkerFile)).Trim() == Version; } catch { return false; }
    }

    internal static void WriteMarker(string dir, string content)
    {
        try { File.WriteAllText(Tech(dir, MarkerFile), content + "\n"); } catch { /* retried next start */ }
    }

    public static bool IsTechnical(string fileName)
    {
        if (fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) fileName = fileName[..^4];
        return TechnicalNames.Contains(fileName) || TrackFileRx.IsMatch(fileName);
    }

    /// <summary>A folder holding a recording: meeting.json or a track, at either place.</summary>
    public static bool IsSession(string dir)
        => Exists(dir, Meeting) || Tags.Any(t => Exists(dir, t + ".wav") || Exists(dir, t + ".mp3"));

    /// <summary>Folders that may hold track files and their intermediates (.echo, then the
    /// legacy root): the clean-ups look at both.</summary>
    public static IEnumerable<string> TrackDirs(string dir)
        => new[] { HiddenDir(dir), dir }.Where(Directory.Exists);

    // ── visible files ────────────────────────────────────────────────────────

    public static bool IsFrench(string? lang)
        => (string.IsNullOrWhiteSpace(lang) ? "fr" : lang!.Trim()).StartsWith("fr", StringComparison.OrdinalIgnoreCase);

    public static string CrLabel(string? lang) => IsFrench(lang) ? "Compte rendu" : "Minutes";
    public static string SummaryLabel(string? lang) => IsFrench(lang) ? "Résumé" : "Summary";

    /// <summary>A meeting name made safe for a Windows file name (one rule for every visible file).</summary>
    public static string SafeTitle(string? raw)
    {
        var s = Regex.Replace(raw ?? "", "[<>:\"/\\\\|?*\\x00-\\x1f]", "");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        if (s.Length > MaxTitle) s = s[..MaxTitle];
        return s.TrimEnd(' ', ',', ';', '-', '.');
    }

    /// <summary>"&lt;Titre&gt;" prefix of the visible files: meeting.json name, else the name part of
    /// the folder ("AAAAMMJJ - HHhMM - Name"), else "Reunion".</summary>
    public static string Title(string dir)
    {
        string? name = null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Find(dir, Meeting)));
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                name = n.GetString();
        }
        catch { /* no / broken meeting.json */ }
        var t = SafeTitle(name);
        if (t.Length == 0)
        {
            var folder = Path.GetFileName(dir.TrimEnd('\\', '/'));
            var parts = folder.Split(" - ", 3);
            t = SafeTitle(parts.Length >= 3 ? parts[2] : folder);
        }
        return t.Length == 0 ? "Reunion" : t;
    }

    public static string Visible(string dir, string label, string ext) => Path.Combine(dir, $"{Title(dir)} - {label}{ext}");

    /// <summary>"&lt;dir&gt;\&lt;Titre&gt; - Compte rendu" (EN "Minutes"), without extension, whatever the CR template.</summary>
    public static string CrBase(string dir, string? lang) => Path.Combine(dir, $"{Title(dir)} - {CrLabel(lang)}");
    public static string TranscriptPath(string dir) => Visible(dir, "Transcript", ".txt");
    public static string SummaryPath(string dir, string? lang) => Visible(dir, SummaryLabel(lang), ".md");
    public static string AudioPath(string dir) => Visible(dir, "Audio", ".mp3");
    public static string NotesPath(string dir) => Path.Combine(dir, Notes);
    public static string CapturesDir(string dir) => Path.Combine(dir, CapturesDirName);

    // Longest first: "X - Processed Transcript.txt" must not read as label "Transcript".
    private static readonly string[] Labels =
        { "Processed Transcript", "Teams Transcript", "Transcript", "Compte rendu", "Minutes", "Summary", "Résumé", "Audio" };
    private static readonly string[] CrExts = { ".eml", ".docx", ".md", ".html" };

    /// <summary>"&lt;prefix&gt; - &lt;label&gt;&lt;ext&gt;" for a known visible product (any language, legacy
    /// names included), else null. Label is returned in its canonical spelling.</summary>
    public static (string Prefix, string Label, string Ext)? SplitVisible(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        var stem = fileName[..^ext.Length];
        foreach (var label in Labels)
        {
            var suf = " - " + label;
            if (stem.Length <= suf.Length || !stem.EndsWith(suf, StringComparison.OrdinalIgnoreCase)) continue;
            bool extOk = label switch
            {
                "Audio" => ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase),
                "Résumé" => ext.Equals(".md", StringComparison.OrdinalIgnoreCase),
                "Compte rendu" or "Minutes" or "Summary" => CrExts.Contains(ext, StringComparer.OrdinalIgnoreCase),
                _ => ext.Equals(".txt", StringComparison.OrdinalIgnoreCase),
            };
            if (extOk) return (stem[..^suf.Length], label, ext);
        }
        return null;
    }

    private static List<string> RootFiles(string dir)
    {
        try { return Directory.EnumerateFiles(dir).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch { return new List<string>(); }
    }

    private static string? Newest(IEnumerable<string> files)
        => files.OrderByDescending(f => { try { return File.GetLastWriteTimeUtc(f); } catch { return DateTime.MinValue; } })
                .FirstOrDefault();

    private static bool HasLabel(string file, params string[] labels)
        => SplitVisible(Path.GetFileName(file)) is { } v && labels.Contains(v.Label);

    /// <summary>A compte rendu in any language or template: "Compte rendu", "Minutes", and the
    /// legacy "Summary" CR (a "Summary.md" is the short summary once the folder is migrated).</summary>
    public static bool IsCr(string fileName, bool migrated)
    {
        if (SplitVisible(fileName) is not { } v) return false;
        return v.Label switch
        {
            "Compte rendu" or "Minutes" => true,
            "Summary" => !(migrated && v.Ext.Equals(".md", StringComparison.OrdinalIgnoreCase)),
            _ => false,
        };
    }

    /// <summary>Every CR file at the root, by name.</summary>
    public static List<string> CrFiles(string dir)
    {
        bool migrated = IsMigrated(dir);
        return RootFiles(dir).Where(f => IsCr(Path.GetFileName(f), migrated)).ToList();
    }

    public static bool HasCr(string dir) => CrFiles(dir).Count > 0;

    /// <summary>The transcript to read (corrected when there is one): the newest of the legacy
    /// "Processed Transcript" and "&lt;Titre&gt; - Transcript.txt", else any "… - Transcript.txt",
    /// else an imported "Teams Transcript", else the raw one in .echo.</summary>
    public static string? FindTranscript(string dir)
    {
        var files = RootFiles(dir);
        var main = TranscriptPath(dir);
        var best = Newest(files.Where(f => HasLabel(f, "Processed Transcript"))
                               .Concat(File.Exists(main) ? new[] { main } : Array.Empty<string>()));
        best ??= Newest(files.Where(f => HasLabel(f, "Transcript")));
        best ??= Newest(files.Where(f => HasLabel(f, "Teams Transcript")));
        if (best is null && Exists(dir, RawTranscript)) best = Find(dir, RawTranscript);
        return best;
    }

    public static bool HasTranscript(string dir) => FindTranscript(dir) is not null;

    /// <summary>The short summary: newest "… - Résumé.md" (or "… - Summary.md" in a migrated
    /// folder), else the legacy resume.md.</summary>
    public static string? FindSummary(string dir)
    {
        bool migrated = IsMigrated(dir);
        var hit = Newest(RootFiles(dir).Where(f => SplitVisible(Path.GetFileName(f)) is { } v
            && (v.Label == "Résumé" || (migrated && v.Label == "Summary" && v.Ext.Equals(".md", StringComparison.OrdinalIgnoreCase)))));
        if (hit is not null) return hit;
        return Exists(dir, LegacySummary) ? Find(dir, LegacySummary) : null;
    }

    /// <summary>The listening mixdown: "&lt;Titre&gt; - Audio.mp3", else the largest "… - Audio.mp3"
    /// (empty files ignored).</summary>
    public static string? FindAudio(string dir)
    {
        static bool Usable(string p) { try { return File.Exists(p) && new FileInfo(p).Length > 200; } catch { return false; } }
        var main = AudioPath(dir);
        if (Usable(main)) return main;
        return RootFiles(dir).Where(f => HasLabel(f, "Audio") && Usable(f))
                             .OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
    }

    // ── moves ────────────────────────────────────────────────────────────────

    public enum MoveResult { Moved, Same, Collision, Failed }

    /// <summary>Rename/move without ever overwriting: an existing target is a collision and the
    /// source stays put; a locked source is a failure (retried later).</summary>
    public static MoveResult TryMove(string src, string dst)
    {
        if (string.Equals(src, dst, StringComparison.Ordinal)) return MoveResult.Same;
        bool caseOnly = string.Equals(src, dst, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (File.Exists(dst) || Directory.Exists(dst))) return MoveResult.Collision;
        try { File.Move(src, dst); return MoveResult.Moved; }
        catch (IOException) when (!caseOnly && File.Exists(dst)) { return MoveResult.Collision; }
        catch (Exception) { return MoveResult.Failed; }
    }

    /// <summary>
    /// After a rename of the meeting: every visible product (CR, transcript, summary, audio,
    /// imported Teams transcript), whatever its old prefix (incl. the old 40-character cut),
    /// takes the current title; its label is kept. Never overwrites. Returns the files renamed.
    /// </summary>
    public static int RenameVisible(string dir, Action<string>? log = null)
    {
        var title = Title(dir);
        int n = 0;
        foreach (var f in RootFiles(dir))
        {
            var name = Path.GetFileName(f);
            if (SplitVisible(name) is not { } v || v.Prefix == title) continue;
            var dst = Path.Combine(dir, title + name[v.Prefix.Length..]);
            var r = TryMove(f, dst);
            if (r == MoveResult.Moved) n++;
            else if (r != MoveResult.Same)
                log?.Invoke($"[layout] {Path.GetFileName(dir)} : « {name} » non renommé ({(r == MoveResult.Collision ? "la cible existe déjà" : "fichier verrouillé")}).");
        }
        return n;
    }
}
