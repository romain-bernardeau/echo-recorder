using System.IO;
using System.Text.Json;
using Echo.App.Engine;
using Echo.Core.Abstractions;
using Echo.Core.Storage;

namespace Echo.App;

/// <summary>
/// Read-only scanner over the recordings root, ported from the Python engine
/// (_scan_sessions / _session_meta / _session_state / session_detail). A directory
/// is a session iff it contains mic.wav or system.wav; "done" iff a *Minutes.eml /
/// *Summary.eml exists (or the .echo_status sentinel says running/error).
/// Backs the recents, all-recents, total and session_detail bridge methods.
/// </summary>
public sealed class SessionQuery
{
    // Shared with SessionInfo (the "Infos réunion.txt" sheet), which used to carry copies.
    internal static readonly string[] FrMonths =
        { "janv.", "févr.", "mars", "avr.", "mai", "juin", "juil.", "août", "sept.", "oct.", "nov.", "déc." };

    internal static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
        { "reunion", "réunion", "reunion physique", "réunion physique", "meeting", "in-person meeting", "" };

    private readonly StoragePaths _paths;
    private readonly IContactsStore _contacts;
    private readonly I18n _i18n;

    public SessionQuery(StoragePaths paths, IContactsStore contacts, I18n i18n)
    {
        _paths = paths;
        _contacts = contacts;
        _i18n = i18n;
    }

    // ── enumeration ──────────────────────────────────────────────────────────

    public List<string> ScanSessions()
    {
        var subs = new List<string>();
        try
        {
            var root = _paths.RecordingsDir;
            if (!Directory.Exists(root)) return subs;
            foreach (var d in Directory.EnumerateDirectories(root))
            {
                // A session's tracks may be raw WAVs, MP3 archives (post-transcription
                // compression), or entirely purged by the retention policy — meeting.json
                // then still marks the folder as a session (transcripts/CR remain).
                if (File.Exists(Path.Combine(d, "mic.wav")) || File.Exists(Path.Combine(d, "system.wav"))
                    || File.Exists(Path.Combine(d, "mic.mp3")) || File.Exists(Path.Combine(d, "system.mp3"))
                    || File.Exists(Path.Combine(d, "meeting.json")))
                    subs.Add(d);
            }
        }
        catch (Exception ex) { Log.Error("scan sessions", ex); }
        return subs;
    }

    public int Total() => ScanSessions().Count;

    public List<object> AllRecents()
    {
        var paths = ScanSessions();
        paths.Sort((a, b) => LastWrite(b).CompareTo(LastWrite(a)));
        return paths.Select(MetaCached).ToList();
    }

    // ── Meta cache (all_recents) ─────────────────────────────────────────────────
    // all_recents rebuilds the metadata of EVERY session (3 JSON parses + several directory
    // listings each) on each call. Cached per folder, keyed on the timestamps of what Meta
    // reads; a short TTL bounds the time-dependent part (a "running" badge going stale).
    private readonly Dictionary<string, (string Key, DateTime At, object Meta)> _metaCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan MetaCacheTtl = TimeSpan.FromSeconds(30);

    private object MetaCached(string path)
    {
        var key = MetaKey(path);
        lock (_metaCache)
        {
            if (_metaCache.TryGetValue(path, out var c) && c.Key == key && DateTime.UtcNow - c.At < MetaCacheTtl)
                return c.Meta;
        }
        var meta = Meta(path);
        lock (_metaCache) _metaCache[path] = (key, DateTime.UtcNow, meta);
        return meta;
    }

    private static string MetaKey(string path)
    {
        static long T(string p) { try { return File.Exists(p) ? File.GetLastWriteTimeUtc(p).Ticks : 0; } catch { return -1; } }
        long dir;
        try { dir = Directory.GetLastWriteTimeUtc(path).Ticks; } catch { dir = -1; }
        // Overwriting an existing file does not touch the folder's timestamp: the files Meta
        // reads in place (rename, badge, flag, duration) are keyed individually.
        return string.Join("|", dir,
            T(Path.Combine(path, "meeting.json")), T(Path.Combine(path, SessionStatus.FileName)),
            T(Path.Combine(path, "flag.txt")), T(Path.Combine(path, "session.json")));
    }

    public List<object> Recents(int limit)
    {
        var paths = ScanSessions();
        paths.Sort((a, b) => LastWrite(b).CompareTo(LastWrite(a)));
        var shown = paths.Take(Math.Max(0, limit)).ToList();
        foreach (var p in shown) SessionInfo.Ensure(p, _i18n);   // drop the info sheet if missing
        return shown.Select(Meta).ToList();
    }

    public string? Latest()
    {
        var paths = ScanSessions();
        return paths.Count == 0 ? null : paths.OrderByDescending(LastWrite).First();
    }

    // ── global full-text search (home) ─────────────────────────────────────────

    /// <summary>Files a session is searched through, most useful first. The first
    /// element of each tuple is a filename SUFFIX (via <see cref="FindFiles"/>).</summary>
    private static readonly (string Suffix, string Kind)[] SearchTargets =
    {
        ("Processed Transcript.txt", "transcript"),
        ("Minutes.md", "cr"), ("Summary.md", "cr"),
        ("notes.md", "notes"),
        ("resume.md", "resume"),
    };

    /// <summary>
    /// Accent/case-insensitive full-text search over every session: meeting names,
    /// transcripts (processed preferred, else raw), CR markdown, notes and summary.
    /// Returns at most <paramref name="maxHits"/> sessions, newest first, each with up
    /// to 2 match snippets carrying (start, length) offsets so the UI can highlight.
    /// Linguistic matching via CompareInfo (IgnoreCase|IgnoreNonSpace): "reunion"
    /// finds "Réunion". Files larger than 2 MB are skipped (nothing we write is that big).
    /// </summary>
    // Generation of the latest search: each keystroke starts a new full scan; an older scan
    // still walking the disk stops as soon as a newer one begins (the UI keeps only the last).
    private int _searchGen;

    /// <param name="ct">Optional extra cancellation; a newer SearchAll call always cancels
    /// the older ones by itself (an empty list is returned for a superseded search).</param>
    public List<object> SearchAll(string query, int maxHits = 30, CancellationToken ct = default)
    {
        int gen = Interlocked.Increment(ref _searchGen);
        bool Superseded() => ct.IsCancellationRequested || Volatile.Read(ref _searchGen) != gen;
        var results = new List<object>();
        var q = (query ?? "").Trim();
        if (q.Length < 2) return results;

        var ci = System.Globalization.CultureInfo.InvariantCulture.CompareInfo;
        const System.Globalization.CompareOptions opts =
            System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace;

        object? Snippet(string text, string kind)
        {
            int idx = ci.IndexOf(text.AsSpan(), q.AsSpan(), opts, out int matchLen);
            if (idx < 0 || matchLen <= 0) return null;
            int from = Math.Max(0, idx - 60), to = Math.Min(text.Length, idx + matchLen + 80);
            while (from > 0 && !char.IsWhiteSpace(text[from])) from--;      // word boundaries
            while (to < text.Length && !char.IsWhiteSpace(text[to])) to++;
            var snip = (from > 0 ? "… " : "") + text[from..to].Replace('\r', ' ').Replace('\n', ' ').Trim()
                       + (to < text.Length ? " …" : "");
            // Locate the match in the FINAL snippet string so the UI offsets are exact.
            int off = ci.IndexOf(snip.AsSpan(), q.AsSpan(), opts, out int ml);
            if (off < 0) { off = 0; ml = 0; }
            return new { kind, text = snip, s = off, l = ml };
        }

        var paths = ScanSessions();
        paths.Sort((a, b) => LastWrite(b).CompareTo(LastWrite(a)));

        foreach (var path in paths)
        {
            if (results.Count >= maxHits) break;
            if (Superseded()) return new List<object>();
            var matches = new List<object>();

            // Meeting name (folder name overridden by meeting.json, like Meta()).
            var dispName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
            var parts = dispName.Split(" - ", 3);
            if (parts.Length >= 3) dispName = parts[2];
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "meeting.json")));
                var mj = doc.RootElement;
                if (mj.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(n.GetString())) dispName = n.GetString()!;
            }
            catch { /* folder name stays */ }
            if (Snippet(dispName, "titre") is { } nameHit) matches.Add(nameHit);

            bool transcriptHit = false;
            foreach (var (suffix, kind) in SearchTargets)
            {
                if (matches.Count >= 2) break;
                foreach (var file in FindFiles(path, suffix))
                {
                    try
                    {
                        if (new FileInfo(file).Length > 2_000_000) continue;
                        if (Snippet(File.ReadAllText(file), kind) is { } hit)
                        {
                            matches.Add(hit);
                            if (kind == "transcript") transcriptHit = true;
                            break;
                        }
                    }
                    catch { /* unreadable file: skip */ }
                }
            }
            // Raw transcript fallback (sessions without a processed transcript).
            if (matches.Count < 2 && !transcriptHit)
            {
                foreach (var file in FindFiles(path, "- Transcript.txt"))
                {
                    try
                    {
                        if (new FileInfo(file).Length > 2_000_000) continue;
                        if (Snippet(File.ReadAllText(file), "transcript") is { } hit) { matches.Add(hit); break; }
                    }
                    catch { }
                }
            }

            if (matches.Count > 0)
                results.Add(new { meta = MetaCached(path), matches });
        }
        return Superseded() ? new List<object>() : results;
    }

    private static DateTime LastWrite(string p)
    {
        try { return Directory.GetLastWriteTimeUtc(p); } catch { return DateTime.MinValue; }
    }

    // ── per-session metadata ───────────────────────────────────────────────────

    public object Meta(string path)
    {
        var folder = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string name = folder, when = "", dur = "", hhmm = "", date = "";
        int durSec = 0;
        bool physical = false;

        var parts = folder.Split(" - ", 3);
        if (parts.Length >= 3)
        {
            name = parts[2];
            hhmm = parts[1];
            try
            {
                var d = parts[0];
                int y = int.Parse(d[..4]), mo = int.Parse(d.Substring(4, 2)), da = int.Parse(d.Substring(6, 2));
                when = $"{da} {FrMonths[mo - 1]} · {parts[1]}";
                date = $"{y:D4}-{mo:D2}-{da:D2}";
            }
            catch { when = ""; }
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "meeting.json")));
            var mj = doc.RootElement;
            if (mj.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString()))
                name = n.GetString()!;
            if (mj.TryGetProperty("physical", out var ph)) physical = ph.ValueKind == JsonValueKind.True;
        }
        catch { /* no meeting.json */ }

        if (GenericNames.Contains((name ?? "").Trim()))
        {
            var basename = _i18n.T(physical ? "meeting_physical" : "meeting_teams");
            name = string.IsNullOrEmpty(hhmm) ? basename : $"{basename} · {hhmm}";
        }

        durSec = ReadDurationSeconds(path);
        if (durSec > 0) dur = Hms(durSec);

        var sub = string.Join(" · ", new[] { when, dur }.Where(s => !string.IsNullOrEmpty(s)));
        return new
        {
            path,
            name,
            sub,
            date,
            dur_s = durSec,
            state = SessionState(path),
            flag = SessionFlag(path),
            // Transcript present but no CR file → the UI shows an actionable "CR manquant"
            // chip (the CR silently-missing episode of 2026-07-15/16 was invisible until
            // the meeting was opened).
            has_tr = HasFileSuffix(path, "Transcript.txt"),
            has_cr = HasCr(path),
            // A track (wav/mp3) or the listening mixdown is still there: without it,
            // "Relancer la transcription" has nothing to send (audio purged by the retention).
            has_audio = SessionFiles.HasAudio(path),
        };
    }

    /// <summary>Recorded duration in whole seconds (session.json, else a track sidecar); 0 if unknown.</summary>
    internal static int ReadDurationSeconds(string path)
    {
        foreach (var jf in new[] { "session.json", "system.json", "mic.json" })
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, jf)));
                var root = doc.RootElement;
                if (root.TryGetProperty("duration_s", out var ds))
                {
                    int sec = ds.ValueKind == JsonValueKind.Number ? (int)ds.GetDouble() : 0;
                    if (sec > 0) return sec;
                }
            }
            catch { }
        }
        return 0;
    }

    private static bool HasFileSuffix(string path, string suffix)
    {
        try
        {
            return Directory.EnumerateFiles(path)
                .Any(f => Path.GetFileName(f).EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static bool HasCr(string path) => SessionFiles.HasCr(path);

    public string SessionState(string path)
    {
        try
        {
            // Same reading as the sweeper and the startup recovery (SessionStatus): "waiting"
            // and "running" only hold for THIS process; a stale "waiting" is an error, a stale
            // "running" falls through to the files.
            var eff = SessionStatus.Effective(SessionStatus.Read(path), Environment.ProcessId,
                                              DateTimeOffset.Now.ToUnixTimeSeconds());
            if (eff is not null) return eff;
        }
        catch { }
        try
        {
            foreach (var n in Directory.EnumerateFiles(path))
            {
                var f = Path.GetFileName(n);
                if (f.EndsWith("Minutes.eml", StringComparison.OrdinalIgnoreCase)
                    || f.EndsWith("Summary.eml", StringComparison.OrdinalIgnoreCase)) return "done";
            }
        }
        catch { }
        return "recorded";
    }

    public string? SessionFlag(string path)
    {
        try
        {
            var fid = File.ReadAllText(Path.Combine(path, "flag.txt")).Trim();
            return string.IsNullOrEmpty(fid) ? null : fid;
        }
        catch { return null; }
    }

    public void SetSessionFlag(string path, string? fid)
    {
        var p = Path.Combine(path, "flag.txt");
        try
        {
            if (!string.IsNullOrWhiteSpace(fid)) File.WriteAllText(p, fid);
            else if (File.Exists(p)) File.Delete(p);
        }
        catch (Exception ex) { Log.Error("flag write", ex); }
    }

    // ── full detail ──────────────────────────────────────────────────────────

    public object Detail(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return new { error = "introuvable" };

        var meta = Meta(path);
        var transcripts = FindFiles(path, "Transcript.txt");
        var crs = FindFiles(path, "Minutes.eml", "Summary.eml", "Minutes.html", "Summary.html",
                                  "Minutes.docx", "Summary.docx", "Minutes.md", "Summary.md");
        var infoTxt = FindFiles(path, "Infos réunion.txt", "Infos reunion.txt");
        var attendees = Attendees(path);

        return new
        {
            meta,
            attendees,
            has_transcript = transcripts.Count > 0,
            transcript = transcripts.Count > 0 ? ReadText(transcripts[0]) : "",
            cr_files = crs.Select(p => new { name = Path.GetFileName(p), path = p }).ToList(),
            info_files = infoTxt.Select(p => new { name = Path.GetFileName(p), path = p }).ToList(),
            captures = Captures(path),
            notes = FileText(Path.Combine(path, "notes.md")),
            summary = FileText(Path.Combine(path, "resume.md")),
            emails = EmailsFor(path, attendees),
            audio = FindFiles(path, "mic.wav", "system.wav", "mic.mp3", "system.mp3")
                .Select(p => new { name = Path.GetFileName(p), path = p }).ToList(),
            // Full-meeting player in the transcript tab: URL of the "… - Audio.mp3" mixdown
            // through the WebView2 virtual host (see WebViewHost mapping). Null when the
            // mixdown is absent or the session lives outside the mapped recordings root.
            audio_url = MixdownUrl(path),
        };
    }

    /// <summary>https://echo-recordings/&lt;relative-path&gt; of the session mixdown, or null.</summary>
    private string? MixdownUrl(string path)
    {
        try
        {
            var mix = FindFiles(path, "- Audio.mp3");
            if (mix.Count == 0) return null;
            var rel = Path.GetRelativePath(_paths.RecordingsDir, mix[0]);
            if (rel.StartsWith("..") || Path.IsPathRooted(rel)) return null;
            var encoded = string.Join('/', rel.Split('\\', '/').Select(Uri.EscapeDataString));
            return "https://echo-recordings/" + encoded;
        }
        catch { return null; }
    }

    public List<string> Attendees(string path) => ReadAttendees(path);

    /// <summary>Attendees = union of meeting.json roster + cumulative roster.jsonl + detected
    /// speakers.jsonl, de-duplicated (trimmed) and sorted. Shared with SessionInfo.</summary>
    internal static List<string> ReadAttendees(string path)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        void Add(IEnumerable<string>? seq)
        {
            if (seq is null) return;
            foreach (var n in seq)
                if (!string.IsNullOrWhiteSpace(n)) names.Add(n.Trim());
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "meeting.json")));
            var mj = doc.RootElement;
            if (mj.TryGetProperty("roster", out var r) && r.ValueKind == JsonValueKind.Array)
                Add(r.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
        }
        catch { }

        foreach (var file in new[] { "roster.jsonl", "speakers.jsonl" })
        {
            var fp = Path.Combine(path, file);
            if (!File.Exists(fp)) continue;
            try
            {
                foreach (var line in File.ReadLines(fp))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var el = doc.RootElement;
                        var key = file == "roster.jsonl" ? "roster" : "speakers";
                        if (el.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
                            Add(arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
                    }
                    catch { }
                }
            }
            catch { }
        }
        return names.ToList();
    }

    public Dictionary<string, string> EmailsFor(string path, IReadOnlyList<string> attendees)
    {
        var emails = ReadSessionEmails(path);
        foreach (var name in attendees)
            if (!emails.ContainsKey(name))
            {
                var c = _contacts.Get(name);
                if (!string.IsNullOrWhiteSpace(c)) emails[name] = c!;
            }
        return emails;
    }

    public Dictionary<string, string> ReadSessionEmails(string path)
    {
        try
        {
            var p = Path.Combine(path, "emails.json");
            if (File.Exists(p))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(p)) ?? new();
        }
        catch { }
        return new();
    }

    // ── last speakers (for get_state) ───────────────────────────────────────────

    public List<string> ReadLastSpeakers(string? sessionDir)
    {
        if (string.IsNullOrWhiteSpace(sessionDir)) return new();
        try
        {
            var p = Path.Combine(sessionDir!, "speakers.jsonl");
            if (!File.Exists(p)) return new();
            // Polled every second by get_state while recording: parse only the TAIL of the
            // file (it grows by a line per speaker change, hours long), not every line.
            // The Teams watcher holds this file open for writing: share ReadWrite or the open fails.
            using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return LastSpeakersFromTail(fs);
        }
        catch { return new(); }
    }

    /// <summary>Bytes read from the end of speakers.jsonl: a few dozen lines.</summary>
    internal const int SpeakersTailBytes = 8 * 1024;

    /// <summary>"speakers" array of the LAST parsable line, reading only the last
    /// <see cref="SpeakersTailBytes"/> bytes of the stream.</summary>
    internal static List<string> LastSpeakersFromTail(Stream fs)
    {
        long start = Math.Max(0, fs.Length - SpeakersTailBytes);
        fs.Seek(start, SeekOrigin.Begin);
        var buf = new byte[fs.Length - start];
        int read = 0;
        while (read < buf.Length)
        {
            int n = fs.Read(buf, read, buf.Length - read);
            if (n <= 0) break;
            read += n;
        }
        var text = System.Text.Encoding.UTF8.GetString(buf, 0, read);
        var lines = text.Split('\n');
        // When the window starts mid-file its first line is a fragment: skip it.
        int first = start > 0 ? 1 : 0;
        for (int k = lines.Length - 1; k >= first; k--)
        {
            var t = lines[k].Trim();
            if (t.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(t);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("speakers", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    return arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList();
            }
            catch { /* partial line being written: try the previous one */ }
        }
        return new();
    }

    // ── captures (best-effort inline data-URIs, CSP-safe) ────────────────────────

    // Up to 24 captures as resized JPEG data-URIs (≤900 px, q72) for inline display
    // without file:// (CSP-safe) — parity with Python _captures_data.
    private static List<object> Captures(string path, int limit = 24)
    {
        var outList = new List<object>();
        var dir = Path.Combine(path, "Captures");
        if (!Directory.Exists(dir)) return outList;
        try
        {
            var files = Directory.EnumerateFiles(dir)
                .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Take(limit);
            foreach (var fp in files)
                outList.Add(new { name = Path.GetFileName(fp), path = fp, dataurl = ScreenCapture.ThumbnailDataUri(fp, 900, 72) ?? "" });
        }
        catch (Exception ex) { Log.Error("captures", ex); }
        return outList;
    }

    // ── file helpers ─────────────────────────────────────────────────────────

    public List<string> FindFiles(string dir, params string[] suffixes)
    {
        var outList = new List<string>();
        try
        {
            foreach (var fn in Directory.EnumerateFiles(dir).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var lower = Path.GetFileName(fn).ToLowerInvariant();
                if (suffixes.Any(s => lower.EndsWith(s.ToLowerInvariant()))) outList.Add(fn);
            }
        }
        catch { }
        return outList;
    }

    public static string FileText(string path) => File.Exists(path) ? ReadText(path) : "";

    public static string ReadText(string path, int cap = 400_000)
    {
        try
        {
            using var r = new StreamReader(path);
            var buf = new char[cap];
            int n = r.Read(buf, 0, cap);
            return new string(buf, 0, n);
        }
        catch { return ""; }
    }

    public static string Hms(int seconds)
    {
        if (seconds < 0) seconds = 0;
        int h = seconds / 3600, m = (seconds % 3600) / 60, s = seconds % 60;
        return h > 0 ? $"{h}:{m:D2}:{s:D2}" : $"{m:D2}:{s:D2}";
    }
}
