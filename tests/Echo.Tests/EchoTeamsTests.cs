using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Echo.Teams;

namespace Echo.Tests;

/// <summary>
/// Regression tests for the Teams transcript meta extraction added for audit F4
/// (<see cref="TeamsTranscriptImporter.TryReadMeta"/>), mirroring teams_transcript.py
/// parse_docx/parse_vtt meta: .docx title minus the "-YYYYMMDD_HHMMSS-" suffix and the
/// "Transcription…" boilerplate + FR/EN date normalised to "D MonthName YYYY"; .vtt name
/// from the file basename.
/// </summary>
public class EchoTeamsTests
{
    private static string MakeDocx(params string[] paragraphs)
    {
        var path = Path.Combine(Path.GetTempPath(), $"echo_teams_{Guid.NewGuid():N}.docx");
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        var body = new Body();
        foreach (var text in paragraphs)
            body.Append(new Paragraph(new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve })));
        main.Document = new Document(body);
        main.Document.Save();
        return path;
    }

    [Fact]
    public void TryReadMeta_Vtt_NameFromBasename_NoDate()
    {
        var path = Path.Combine(Path.GetTempPath(), $"Weekly Sync {Guid.NewGuid():N}.vtt");
        File.WriteAllText(path, "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\n<v Alice>Bonjour</v>\n");
        try
        {
            var meta = new TeamsTranscriptImporter().TryReadMeta(path);
            Assert.Equal(Path.GetFileNameWithoutExtension(path), meta.Name);
            Assert.Null(meta.DateStr);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TryReadMeta_Docx_StripsSuffix_AndNormalisesFrenchDate()
    {
        // Title carries the Teams "-YYYYMMDD_HHMMSS-Transcription de la réunion" suffix.
        var path = MakeDocx(
            "Réunion stratégie-20260115_101500-Transcription de la réunion",
            "15 janvier 2026, 10:15");
        try
        {
            var meta = new TeamsTranscriptImporter().TryReadMeta(path);
            Assert.Equal("Réunion stratégie", meta.Name);
            Assert.Equal("15 January 2026", meta.DateStr);   // FR month → EN, leading zero dropped
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TryReadMeta_Docx_StripsBoilerplate_AndEnglishDate()
    {
        var path = MakeDocx(
            "Weekly Sync Transcription",
            "3 January 2026 - 11:00AM");
        try
        {
            var meta = new TeamsTranscriptImporter().TryReadMeta(path);
            Assert.Equal("Weekly Sync", meta.Name);          // "Transcription" boilerplate removed
            Assert.Equal("3 January 2026", meta.DateStr);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TryReadMeta_UnknownExtension_ReturnsNulls()
    {
        var meta = new TeamsTranscriptImporter().TryReadMeta("does-not-exist.txt");
        Assert.Null(meta.Name);
        Assert.Null(meta.DateStr);
    }
}
