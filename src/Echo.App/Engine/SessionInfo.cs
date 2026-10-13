using System.IO;
using System.Text;
using System.Text.Json;
using Echo.Core.Storage;

namespace Echo.App.Engine;

/// <summary>
/// Writes the human-readable <c>Infos réunion.txt</c> sidecar into a session folder
/// (faithful port of <c>tray_app.write_session_info</c> + <c>_session_attendees</c>).
/// Stable filename regardless of UI language, so switching language never creates a
/// duplicate. Content: name, type (Teams / physical), date, duration, attendees.
/// </summary>
public static class SessionInfo
{
    public const string InfoFile = SessionLayout.Info;

    // Month names, generic-name list, attendees and duration are SessionQuery's (one copy).
    private static string[] FrMonths => SessionQuery.FrMonths;
    private static HashSet<string> GenericNames => SessionQuery.GenericNames;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Write/refresh the info sheet. Skips writing if it already exists unless
    /// <paramref name="force"/> (used when a recording stops).</summary>
    public static string Write(string path, I18n i18n, bool force = false)
    {
        if (!force && SessionLayout.Exists(path, InfoFile)) return SessionLayout.Find(path, InfoFile);
        var dest = SessionLayout.ForWrite(path, InfoFile);

        var folder = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var parts = folder.Split(" - ", 3);
        string hhmm = parts.Length >= 3 ? parts[1] : "";
        string name = folder, dateStr = "";
        bool physical = false;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(SessionLayout.Find(path, SessionLayout.Meeting)));
            var mj = doc.RootElement;
            if (mj.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString()))
                name = n.GetString()!;
            if (mj.TryGetProperty("physical", out var ph)) physical = ph.ValueKind == JsonValueKind.True;
            if (mj.TryGetProperty("date_str", out var d) && d.ValueKind == JsonValueKind.String) dateStr = d.GetString() ?? "";
        }
        catch { /* no meeting.json */ }

        string typ = i18n.T(physical ? "meeting_physical" : "meeting_teams");
        if (GenericNames.Contains((name ?? "").Trim()))
            name = string.IsNullOrEmpty(hhmm) ? typ : $"{typ} · {hhmm}";

        string dateDisp = dateStr;
        if (string.IsNullOrEmpty(dateDisp) && parts.Length >= 3)
        {
            try
            {
                var d = parts[0];
                int y = int.Parse(d[..4]), mo = int.Parse(d.Substring(4, 2)), da = int.Parse(d.Substring(6, 2));
                dateDisp = $"{da} {FrMonths[mo - 1]} {y}" + (string.IsNullOrEmpty(hhmm) ? "" : $" · {hhmm}");
            }
            catch { dateDisp = hhmm; }
        }

        int durSec = SessionQuery.ReadDurationSeconds(path);
        string dur = durSec > 0 ? SessionQuery.Hms(durSec) : "";

        var att = Attendees(path);
        string attS = att.Count > 0 ? string.Join(", ", att) : i18n.T("info_none");
        string title = i18n.T("info_title");
        var lines = new List<string>
        {
            title, new string('=', title.Length), "",
            $"{i18n.T("info_meeting")} : {name}",
            $"{i18n.T("info_type")} : {typ}",
            $"{i18n.T("info_date")} : {(string.IsNullOrEmpty(dateDisp) ? "-" : dateDisp)}",
            $"{i18n.T("info_duration")} : {(string.IsNullOrEmpty(dur) ? "-" : dur)}",
            $"{i18n.T("info_attendees")} : {attS}",
            "", i18n.T("info_generated").Replace("{t}", DateTime.Now.ToString("yyyy-MM-dd HH:mm")),
        };

        try { File.WriteAllText(dest, string.Join("\n", lines) + "\n", Utf8NoBom); }
        catch (Exception ex) { Log.Error("info file", ex); }
        return dest;
    }

    /// <summary>Best-effort ensure (never throws), used from read paths.</summary>
    public static void Ensure(string path, I18n i18n)
    {
        try { Write(path, i18n, force: false); } catch { }
    }

    private static List<string> Attendees(string path) => SessionQuery.ReadAttendees(path);
}
