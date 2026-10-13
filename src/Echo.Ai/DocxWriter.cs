using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Echo.Ai;

/// <summary>
/// Minimal .docx writer for the minutes, built on the OpenXML SDK. OpenXML has no
/// implicit Heading/List/Table styles, so we inject a styles part (Heading1/2), a
/// numbering part (disc bullet) and explicit table borders. Mirrors the python-docx
/// layout in <c>minutes.py</c> (title, Date/Attendees, section headings, bullets,
/// 3-column next-steps table with a bold header row and TBC defaults).
/// </summary>
public static class DocxWriter
{
    private const string Navy = "0D2F4B";

    public static void Write(string path, MinutesData data, bool general, string? dateStr, IReadOnlyList<string>? attendees)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body());
        var body = main.Document.Body!;

        AddStyles(main);
        AddNumbering(main);

        var L = data.Labels;

        // python-docx applies minutes._plain everywhere (strip **/__, em/en dash -> comma,
        // HTML-unescape). The .eml (Esc) and .md (Plain) writers already do; the docx writer
        // did NOT, so French labels such as "3. Points d&rsquo;arbitrage" and any model **bold**
        // leaked through verbatim. Normalise every string, exactly like write_docx.
        string P(string? s) => MinutesGenerator.Plain(s);

        body.Append(Heading(P(data.Subject.Length > 0 ? data.Subject : "Minutes"), "Heading1"));

        if (!string.IsNullOrWhiteSpace(dateStr))
            body.Append(LabelValue($"{P(L["date"])}: ", P(dateStr!)));
        var names = string.Join(", ", (attendees ?? Array.Empty<string>()).Where(a => !string.IsNullOrWhiteSpace(a)).Select(P));
        if (names.Length > 0)
            body.Append(LabelValue($"{P(L["participants"])}: ", names));

        body.Append(Heading(P(L["context"]), "Heading2"));
        body.Append(Para(P(data.Context)));

        body.Append(Heading(P(L["key"]), "Heading2"));
        foreach (var it in data.KeyElements) body.Append(Bullet(it));

        if (!general)
        {
            if (data.Arbitration.Count > 0)
            {
                body.Append(Heading(P(L["arb"]), "Heading2"));
                foreach (var it in data.Arbitration) body.Append(Bullet(it));
            }
            body.Append(Heading(P(L["next"]), "Heading2"));
            body.Append(NextStepsTable(data.NextSteps, L));
        }

        main.Document.Save();
    }

    // ── content builders ───────────────────────────────────────────────────────

    private static Paragraph Heading(string text, string styleId)
    {
        var p = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = styleId }));
        p.Append(new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
        return p;
    }

    private static Paragraph Para(string text)
        => new(new Run(new Text(text ?? "") { Space = SpaceProcessingModeValues.Preserve }));

    private static Paragraph LabelValue(string label, string value)
    {
        var p = new Paragraph();
        p.Append(new Run(new RunProperties(new Bold()), new Text(label) { Space = SpaceProcessingModeValues.Preserve }));
        p.Append(new Run(new Text(value) { Space = SpaceProcessingModeValues.Preserve }));
        return p;
    }

    private static Paragraph Bullet(MinItem it)
    {
        var props = new ParagraphProperties(
            new NumberingProperties(new NumberingLevelReference { Val = 0 }, new NumberingId { Val = 1 }));
        var p = new Paragraph(props);
        var title = MinutesGenerator.Plain(it.Title);
        var desc = MinutesGenerator.Plain(it.Description);
        if (title.Length > 0)
            p.Append(new Run(new RunProperties(new Bold()), new Text($"{title}: ") { Space = SpaceProcessingModeValues.Preserve }));
        p.Append(new Run(new Text(desc) { Space = SpaceProcessingModeValues.Preserve }));
        if (!string.IsNullOrEmpty(it.Ts))
            p.Append(new Run(new Text($" [{it.Ts}]") { Space = SpaceProcessingModeValues.Preserve }));
        return p;
    }

    private static Table NextStepsTable(IReadOnlyList<NextStep> steps, IReadOnlyDictionary<string, string> L)
    {
        var table = new Table();
        var borders = new TableBorders(
            new TopBorder { Val = BorderValues.Single, Size = 4 },
            new BottomBorder { Val = BorderValues.Single, Size = 4 },
            new LeftBorder { Val = BorderValues.Single, Size = 4 },
            new RightBorder { Val = BorderValues.Single, Size = 4 },
            new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
            new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 });
        table.AppendChild(new TableProperties(
            new TableStyle { Val = "TableGrid" },
            new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct },
            borders));

        table.Append(Row(bold: true, MinutesGenerator.Plain(L["action"]), MinutesGenerator.Plain(L["owner"]), MinutesGenerator.Plain(L["deadline"])));
        foreach (var s in steps)
        {
            var action = MinutesGenerator.Plain(s.Action);
            if (!string.IsNullOrEmpty(s.Ts)) action = $"{action} [{s.Ts}]";
            var owner = MinutesGenerator.Plain(s.Owner); if (owner.Length == 0) owner = "TBC";
            var deadline = MinutesGenerator.Plain(s.Deadline); if (deadline.Length == 0) deadline = "TBC";
            table.Append(Row(bold: false, action, owner, deadline));
        }
        return table;
    }

    private static TableRow Row(bool bold, params string[] cells)
    {
        var row = new TableRow();
        foreach (var c in cells)
        {
            var runProps = bold ? new RunProperties(new Bold()) : new RunProperties();
            var para = new Paragraph(new Run(runProps, new Text(c ?? "") { Space = SpaceProcessingModeValues.Preserve }));
            row.Append(new TableCell(para));
        }
        return row;
    }

    // ── parts: styles + numbering ────────────────────────────────────────────

    private static void AddStyles(MainDocumentPart main)
    {
        var part = main.AddNewPart<StyleDefinitionsPart>();
        var styles = new Styles();
        styles.Append(HeadingStyle("Heading1", "heading 1", 32, Navy));  // 16pt (half-points)
        styles.Append(HeadingStyle("Heading2", "heading 2", 26, Navy));  // 13pt
        // A named TableGrid style referenced by the table (kept minimal).
        styles.Append(new Style(new StyleName { Val = "Table Grid" }) { Type = StyleValues.Table, StyleId = "TableGrid" });
        part.Styles = styles;
        part.Styles.Save();
    }

    private static Style HeadingStyle(string id, string name, int halfPts, string colorHex)
    {
        return new Style(
            new StyleName { Val = name },
            new BasedOn { Val = "Normal" },
            new StyleRunProperties(
                new Bold(),
                new Color { Val = colorHex },
                new FontSize { Val = halfPts.ToString() }))
        {
            Type = StyleValues.Paragraph,
            StyleId = id,
        };
    }

    private static void AddNumbering(MainDocumentPart main)
    {
        var part = main.AddNewPart<NumberingDefinitionsPart>();
        var abstractNum = new AbstractNum(
            new Level(
                new NumberingFormat { Val = NumberFormatValues.Bullet },
                new LevelText { Val = "•" },
                new ParagraphProperties(new Indentation { Left = "720", Hanging = "360" }))
            { LevelIndex = 0 })
        { AbstractNumberId = 0 };
        var num = new NumberingInstance(new AbstractNumId { Val = 0 }) { NumberID = 1 };
        part.Numbering = new Numbering(abstractNum, num);
        part.Numbering.Save();
    }
}
