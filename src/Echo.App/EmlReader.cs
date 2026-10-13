using System.IO;
using System.Text;

namespace Echo.App;

/// <summary>
/// Minimal reader for the .eml drafts written by the minutes generator (single-part
/// text/html with X-Unsent:1, UTF-8, 8bit). Returns the body + its kind ("html" or
/// "text") for the Compte-rendu preview tab. Not a general MIME parser — the .eml
/// shape is fixed by <c>MinutesGenerator.WriteEml</c>.
/// </summary>
public static class EmlReader
{
    public static (string kind, string text) ReadBody(string path)
    {
        var raw = File.ReadAllText(path, Encoding.UTF8);
        int sep = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        int sepLen = 4;
        if (sep < 0) { sep = raw.IndexOf("\n\n", StringComparison.Ordinal); sepLen = 2; }

        var headers = sep >= 0 ? raw[..sep] : "";
        var body = sep >= 0 ? raw[(sep + sepLen)..] : raw;

        var isHtml = headers.Contains("text/html", StringComparison.OrdinalIgnoreCase);
        return (isHtml ? "html" : "text", body);
    }
}
