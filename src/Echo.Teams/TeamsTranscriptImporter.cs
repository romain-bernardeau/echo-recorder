using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Echo.Core.Abstractions;
using Echo.Core.Models;

namespace Echo.Teams;

/// <summary>
/// Parses a native Teams transcript export (.docx "Transcription de la réunion" or .vtt)
/// into normalized <see cref="TranscriptTurn"/>s. Faithful port of <c>teams_transcript.py</c>:
/// same turn regex (no word boundary after the timestamp — the .docx glues text to the
/// time), same speaker-line filtering, same consecutive-same-speaker merge.
/// </summary>
public sealed class TeamsTranscriptImporter : ITeamsTranscriptImporter
{
    // "Name  M:SS text" / "Name  MM:SS text" / "Name  H:MM:SS text".
    // No \b after the timestamp: in the Teams .docx the text is glued to the time ("0:03module…").
    private static readonly Regex TurnRe =
        new(@"^(?<spk>.+?)\s+(?<ts>\d{1,2}:\d{2}(?::\d{2})?)(?<text>.*)$", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex FourDigitRe = new(@"\d{4}", RegexOptions.Compiled);
    private static readonly Regex AlphaRe = new(@"[A-Za-zÀ-ÿ]", RegexOptions.Compiled);
    private static readonly Regex VttStartRe = new(@"(\d{1,2}:\d{2}:\d{2})[.,]\d+\s*-->", RegexOptions.Compiled);
    private static readonly Regex VttVoiceRe = new(@"<v\s+([^>]+)>", RegexOptions.Compiled);
    private static readonly Regex VttVoiceTagRe = new(@"</?v[^>]*>", RegexOptions.Compiled);
    private static readonly Regex AnyTagRe = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex BareNumberRe = new(@"^\d+$", RegexOptions.Compiled);
    private static readonly Regex BlankLineSplitRe = new(@"\n\s*\n", RegexOptions.Compiled);

    // ── meta extraction (parity with teams_transcript.py parse_docx/parse_vtt) ──
    // Teams .docx title suffix "-YYYYMMDD_HHMMSS-" and the "Transcription de la réunion" boilerplate.
    private static readonly Regex TitleSuffixRe = new(@"-\d{8}_\d{6}-", RegexOptions.Compiled);
    private static readonly Regex TranscriptionBoilerplateRe = new(@"-?Transcription de la r|Transcription", RegexOptions.Compiled);
    private static readonly Regex MultiSpaceRe = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex DateRe = new(@"(\d{1,2})\s+([A-Za-zÀ-ÿ]+)\.?\s+(\d{4})", RegexOptions.Compiled);

    private static readonly Dictionary<string, int> Months = new(StringComparer.Ordinal)
    {
        ["janvier"] = 1, ["fevrier"] = 2, ["février"] = 2, ["mars"] = 3, ["avril"] = 4, ["mai"] = 5,
        ["juin"] = 6, ["juillet"] = 7, ["aout"] = 8, ["août"] = 8, ["septembre"] = 9,
        ["octobre"] = 10, ["novembre"] = 11, ["decembre"] = 12, ["décembre"] = 12,
        ["january"] = 1, ["february"] = 2, ["march"] = 3, ["april"] = 4, ["june"] = 6, ["july"] = 7,
        ["august"] = 8, ["september"] = 9, ["october"] = 10, ["november"] = 11, ["december"] = 12,
    };

    private static readonly string[] EnMonth =
    {
        "", "January", "February", "March", "April", "May", "June", "July",
        "August", "September", "October", "November", "December",
    };

    /// <inheritdoc/>
    public bool CanImport(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".vtt") return true;
        if (ext == ".docx")
        {
            // Content sanity: a real .docx is a zip with a "PK" signature.
            try
            {
                using var fs = File.OpenRead(path);
                return fs.Length >= 2 && fs.ReadByte() == 'P' && fs.ReadByte() == 'K';
            }
            catch { return false; }
        }
        return false;
    }

    /// <inheritdoc/>
    public IReadOnlyList<TranscriptTurn> Import(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var raw = ext switch
        {
            ".vtt" => ParseVtt(path),
            ".docx" => ParseDocx(path),
            _ => throw new NotSupportedException($"Unsupported transcript format: {ext} (expected .docx or .vtt)"),
        };

        var merged = MergeTurns(raw);
        return ToTurns(merged);
    }

    /// <summary>
    /// Reads the transcript's own meeting name + date without parsing turns. This is a class
    /// method (NOT on <see cref="ITeamsTranscriptImporter"/>) so the interface stays unchanged;
    /// the minutes/CR pipeline in the App layer can call it to seed the CR header, mirroring
    /// what <c>teams_transcript.py</c> <c>parse_docx</c>/<c>parse_vtt</c> return as <c>meta</c>.
    /// For .docx: name = 1st paragraph minus the "-YYYYMMDD_HHMMSS-" suffix and the
    /// "Transcription de la réunion" boilerplate; date = first FR/EN date in the first 6
    /// paragraphs, normalised to "D MonthName YYYY". For .vtt: name = file basename, no date.
    /// Never throws — returns (null, null) on any error or unsupported extension.
    /// </summary>
    public TeamsTranscriptMeta TryReadMeta(string path)
    {
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".vtt" => new TeamsTranscriptMeta(NullIfEmpty(Path.GetFileNameWithoutExtension(path)), null),
                ".docx" => DocxMeta(path),
                _ => new TeamsTranscriptMeta(null, null),
            };
        }
        catch
        {
            return new TeamsTranscriptMeta(null, null);
        }
    }

    private static TeamsTranscriptMeta DocxMeta(string path)
    {
        var paras = DocxParagraphs(path);
        string? name = null;
        string? dateStr = null;

        if (paras.Count > 0)
        {
            // title = 1st paragraph, cut before the "-YYYYMMDD_HHMMSS-" suffix, then before
            // the "Transcription…" boilerplate; collapse whitespace and strip spaces/dashes.
            var title = TitleSuffixRe.Split(paras[0])[0];
            title = TranscriptionBoilerplateRe.Split(title)[0];
            title = MultiSpaceRe.Replace(title, " ").Trim(' ', '-');
            name = NullIfEmpty(title);
        }

        foreach (var p in paras.Take(6))
            dateStr ??= ParseDate(p);

        return new TeamsTranscriptMeta(name, dateStr);
    }

    private static string? ParseDate(string line)
    {
        var m = DateRe.Match(line);
        if (!m.Success) return null;
        string mon = m.Groups[2].Value.ToLowerInvariant();
        if (!Months.TryGetValue(mon, out int num)) return null;
        int day = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        return $"{day} {EnMonth[num]} {m.Groups[3].Value}";
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    // ─────────────────────────────────────────────────────────────────────────
    //  Internal raw representation
    // ─────────────────────────────────────────────────────────────────────────

    private sealed record RawTurn(double T, string Speaker, string Text);

    // ─────────────────────────────────────────────────────────────────────────
    //  .docx
    // ─────────────────────────────────────────────────────────────────────────

    private static List<string> DocxParagraphs(string path)
    {
        var paragraphs = new List<string>();
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return paragraphs;

        foreach (var p in body.Descendants<Paragraph>())
        {
            // InnerText concatenates all <w:t> run texts and decodes XML entities — equivalent
            // to the Python join-runs-then-unescape step.
            var line = p.InnerText;
            if (!string.IsNullOrWhiteSpace(line))
                paragraphs.Add(line.Trim());
        }
        return paragraphs;
    }

    private static List<RawTurn> ParseDocx(string path)
    {
        var paras = DocxParagraphs(path);
        return TurnsFromLines(paras);
    }

    private static List<RawTurn> TurnsFromLines(List<string> lines)
    {
        var turns = new List<RawTurn>();
        foreach (var line in lines)
        {
            var m = TurnRe.Match(line);
            if (!m.Success) continue;
            var spk = m.Groups["spk"].Value.Trim();
            var ts = m.Groups["ts"].Value;
            var text = m.Groups["text"].Value.Trim();
            if (!LooksLikeSpeaker(spk) || text.Length == 0) continue;
            turns.Add(new RawTurn(TsToSeconds(ts), spk, text));
        }
        return turns;
    }

    private static bool LooksLikeSpeaker(string? spk)
    {
        spk = (spk ?? "").Trim();
        if (spk.Length == 0 || spk.Length > 70) return false;
        if (FourDigitRe.IsMatch(spk)) return false; // a year ⇒ a date/header line
        if (!AlphaRe.IsMatch(spk)) return false;
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  .vtt
    // ─────────────────────────────────────────────────────────────────────────

    private static List<RawTurn> ParseVtt(string path)
    {
        // utf-8-sig equivalent: StreamReader with BOM detection strips a leading BOM.
        string content = File.ReadAllText(path, Encoding.UTF8);
        var turns = new List<RawTurn>();

        foreach (var rawBlock in BlankLineSplitRe.Split(content))
        {
            var block = rawBlock.Trim();
            if (block.Length == 0 || block.ToUpperInvariant().StartsWith("WEBVTT")) continue;

            var mt = VttStartRe.Match(block);
            if (!mt.Success) continue;
            double start = TsToSeconds(mt.Groups[1].Value);

            var textLines = block
                .Split('\n')
                .Where(l => !l.Contains("-->") && !BareNumberRe.IsMatch(l.Trim()))
                .ToArray();
            var text = string.Join(" ", textLines).Trim();

            string speaker = "";
            var mv = VttVoiceRe.Match(text);
            if (mv.Success) speaker = mv.Groups[1].Value.Trim();

            text = VttVoiceTagRe.Replace(text, "").Trim();
            text = AnyTagRe.Replace(text, "").Trim();

            if (text.Length > 0)
                turns.Add(new RawTurn(start, speaker, text));
        }
        return turns;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Shared normalization
    // ─────────────────────────────────────────────────────────────────────────

    private static double TsToSeconds(string ts)
    {
        var parts = ts.Split(':').Select(p => int.TryParse(p, out var v) ? v : 0).ToArray();
        return parts.Length switch
        {
            3 => parts[0] * 3600 + parts[1] * 60 + parts[2],
            2 => parts[0] * 60 + parts[1],
            _ => 0.0,
        };
    }

    /// <summary>Merges consecutive turns by the same speaker (Teams splits one utterance into many cues).</summary>
    private static List<RawTurn> MergeTurns(List<RawTurn> turns)
    {
        var outp = new List<RawTurn>();
        foreach (var t in turns)
        {
            if (outp.Count > 0 && string.Equals(outp[^1].Speaker, t.Speaker, StringComparison.Ordinal))
            {
                var prev = outp[^1];
                outp[^1] = prev with { Text = (prev.Text + " " + t.Text).Trim() };
            }
            else
            {
                outp.Add(t);
            }
        }
        return outp;
    }

    /// <summary>
    /// Projects merged raw turns onto <see cref="TranscriptTurn"/>. Teams transcripts give only a
    /// start time per turn, so EndS is set to the next turn's start (or its own start for the last).
    /// </summary>
    private static List<TranscriptTurn> ToTurns(List<RawTurn> merged)
    {
        var result = new List<TranscriptTurn>(merged.Count);
        for (int i = 0; i < merged.Count; i++)
        {
            double start = merged[i].T;
            double end = (i + 1 < merged.Count && merged[i + 1].T > start) ? merged[i + 1].T : start;
            result.Add(new TranscriptTurn(start, end, merged[i].Speaker, merged[i].Text));
        }
        return result;
    }
}

/// <summary>
/// Meeting name/date carried by a native Teams transcript export, as extracted by
/// <see cref="TeamsTranscriptImporter.TryReadMeta"/> (mirrors the <c>meta</c> dict of
/// <c>teams_transcript.py</c>). Either field may be null when absent/unparseable.
/// </summary>
public sealed record TeamsTranscriptMeta(string? Name, string? DateStr);
