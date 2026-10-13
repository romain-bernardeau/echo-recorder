using Echo.Core.Storage;

namespace Echo.Tests;

/// <summary>
/// The disposition of a meeting folder (<see cref="SessionLayout"/>): visible products named
/// after the meeting at the root, everything technical in the hidden .echo\, reads that fall
/// back on the legacy root, and the detection of a CR / transcript / summary in both languages
/// and under the pre-layout names.
/// </summary>
public sealed class SessionLayoutTests : IDisposable
{
    private readonly string _root;

    public SessionLayoutTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "echo-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string Dir(string name = "20261001 - 10h00 - Point")
    {
        var d = Path.Combine(_root, name);
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Touch(string path, string text = "x", DateTime? at = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        if (at is { } t) File.SetLastWriteTimeUtc(path, t);
    }

    private static void Meeting(string dir, string name, bool legacy = false)
        => Touch(legacy ? Path.Combine(dir, "meeting.json") : SessionLayout.ForWrite(dir, "meeting.json"),
                 $$"""{"name":"{{name}}"}""");

    // ── paths ──────────────────────────────────────────────────────────────

    [Fact]
    public void ForWrite_CreatesHiddenEchoFolder_AndMarksAFreshSession()
    {
        var d = Dir();
        var p = SessionLayout.ForWrite(d, "meeting.json");

        Assert.Equal(Path.Combine(d, ".echo", "meeting.json"), p);
        var h = new DirectoryInfo(Path.Combine(d, ".echo"));
        Assert.True(h.Exists);
        Assert.True(h.Attributes.HasFlag(FileAttributes.Hidden));
        Assert.True(SessionLayout.IsMigrated(d));   // nothing at the root: a new session
    }

    [Fact]
    public void ForWrite_InALegacyFolder_DoesNotMarkItMigrated()
    {
        var d = Dir();
        Touch(Path.Combine(d, "mic.wav"));
        SessionLayout.ForWrite(d, SessionStatus.FileName);
        Assert.False(SessionLayout.IsMigrated(d));
    }

    [Fact]
    public void VisiblePaths_UseTheFullTitle_AndTheLanguageLabels()
    {
        var d = Dir();
        Meeting(d, "Comite de pilotage: lot 2 / achats indirects");

        Assert.Equal("Comite de pilotage lot 2 achats indirects", SessionLayout.Title(d));
        Assert.Equal(Path.Combine(d, "Comite de pilotage lot 2 achats indirects - Compte rendu"), SessionLayout.CrBase(d, "fr"));
        Assert.EndsWith(" - Minutes", SessionLayout.CrBase(d, "en"));
        Assert.EndsWith(" - Minutes", SessionLayout.CrBase(d, "es"));
        Assert.EndsWith(" - Transcript.txt", SessionLayout.TranscriptPath(d));
        Assert.EndsWith(" - Résumé.md", SessionLayout.SummaryPath(d, "fr"));
        Assert.EndsWith(" - Summary.md", SessionLayout.SummaryPath(d, "en"));
        Assert.EndsWith(" - Audio.mp3", SessionLayout.AudioPath(d));
        Assert.Equal(Path.Combine(d, "notes.md"), SessionLayout.NotesPath(d));
        Assert.Equal(Path.Combine(d, "Captures"), SessionLayout.CapturesDir(d));
    }

    [Fact]
    public void SafeTitle_StripsForbiddenCharacters_CapsAt80_AndTrimsTheTail()
    {
        Assert.Equal("Weekly Sync", SessionLayout.SafeTitle("Weekly:/ Sync"));
        Assert.Equal(80, SessionLayout.SafeTitle(new string('a', 120)).Length);
        Assert.Equal("abc", SessionLayout.SafeTitle("abc - ."));
        Assert.Equal("", SessionLayout.SafeTitle(null));
    }

    [Fact]
    public void Title_FallsBackOnTheFolderName_ThenOnReunion()
    {
        Assert.Equal("Point", SessionLayout.Title(Dir()));
        Assert.Equal("Reunion", SessionLayout.Title(Dir("20261001 - 10h00 - -")));
        var d = Dir("20261001 - 10h00 - Revue");
        Meeting(d, "   ");
        Assert.Equal("Revue", SessionLayout.Title(d));
    }

    // ── reads with legacy fallback ─────────────────────────────────────────

    [Fact]
    public void Find_PrefersEcho_FallsBackOnRoot_ElseEchoPath()
    {
        var d = Dir();
        Assert.Equal(Path.Combine(d, ".echo", "flag.txt"), SessionLayout.Find(d, "flag.txt"));   // nowhere

        Touch(Path.Combine(d, "flag.txt"), "legacy");
        Assert.Equal(Path.Combine(d, "flag.txt"), SessionLayout.Find(d, "flag.txt"));

        Touch(SessionLayout.ForWrite(d, "flag.txt"), "new");
        Assert.Equal(Path.Combine(d, ".echo", "flag.txt"), SessionLayout.Find(d, "flag.txt"));

        SessionLayout.Delete(d, "flag.txt");
        Assert.False(SessionLayout.Exists(d, "flag.txt"));
    }

    [Fact]
    public void Status_IsWrittenInEcho_AndReadFromALegacyRoot()
    {
        var d = Dir();
        Touch(Path.Combine(d, ".echo_status"), "error\n1\n0\n");
        Assert.Equal("error", SessionStatus.Read(d)!.State);

        SessionStatus.Write(d, "running");
        Assert.True(File.Exists(Path.Combine(d, ".echo", ".echo_status")));
        Assert.Equal("running", SessionStatus.Read(d)!.State);

        SessionStatus.Clear(d);
        Assert.Null(SessionStatus.Read(d));
        Assert.False(File.Exists(Path.Combine(d, ".echo_status")));
    }

    [Fact]
    public void IsSession_SeesMeetingJsonOrATrack_AtEitherPlace()
    {
        var a = Dir("a"); Meeting(a, "A");
        var b = Dir("b"); Touch(Path.Combine(b, "system.mp3"));
        var c = Dir("c"); Touch(Path.Combine(c, "Capture.png"));
        Assert.True(SessionLayout.IsSession(a));
        Assert.True(SessionLayout.IsSession(b));
        Assert.False(SessionLayout.IsSession(c));
    }

    // ── detection: both languages and legacy names ─────────────────────────

    [Theory]
    [InlineData("T - Compte rendu.eml", false, true)]
    [InlineData("T - Compte rendu.docx", true, true)]
    [InlineData("T - Minutes.md", true, true)]
    [InlineData("T - Summary.eml", true, true)]      // legacy "general" CR
    [InlineData("T - Summary.md", false, true)]      // legacy folder: a CR
    [InlineData("T - Summary.md", true, false)]      // migrated folder: the English short summary
    [InlineData("T - Résumé.md", true, false)]
    [InlineData("T - Transcript.txt", true, false)]
    [InlineData("notes.md", true, false)]
    public void IsCr_KnowsBothLanguagesAndTheLegacyNames(string name, bool migrated, bool expected)
        => Assert.Equal(expected, SessionLayout.IsCr(name, migrated));

    [Fact]
    public void FindTranscript_Legacy_PrefersProcessed()
    {
        var d = Dir();
        var t0 = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);
        Touch(Path.Combine(d, "Point - Transcript.txt"), "raw", t0);
        Touch(Path.Combine(d, "Point - Processed Transcript.txt"), "corrected", t0.AddMinutes(1));
        Assert.Equal("Point - Processed Transcript.txt", Path.GetFileName(SessionLayout.FindTranscript(d)));
        Assert.True(SessionLayout.HasTranscript(d));
    }

    [Fact]
    public void FindTranscript_NewLayout_TheVisibleOne_ElseTheRawInEcho()
    {
        var d = Dir();
        Meeting(d, "Point");
        Assert.Null(SessionLayout.FindTranscript(d));

        Touch(SessionLayout.ForWrite(d, SessionLayout.RawTranscript), "raw");
        Assert.Equal(SessionLayout.Tech(d, SessionLayout.RawTranscript), SessionLayout.FindTranscript(d));

        Touch(SessionLayout.TranscriptPath(d), "corrected");
        Assert.Equal(SessionLayout.TranscriptPath(d), SessionLayout.FindTranscript(d));
    }

    [Fact]
    public void FindTranscript_AnImportedTeamsTranscriptCounts()
    {
        var d = Dir();
        Touch(Path.Combine(d, "Point - Teams Transcript.txt"));
        Assert.True(SessionLayout.HasTranscript(d));
    }

    [Fact]
    public void HasCr_AndFindSummary_InBothLanguages()
    {
        var fr = Dir("fr"); Meeting(fr, "Point");
        Touch(Path.Combine(fr, "Point - Compte rendu.eml"));
        Touch(Path.Combine(fr, "Point - Résumé.md"));
        Assert.True(SessionLayout.HasCr(fr));
        Assert.EndsWith(" - Résumé.md", SessionLayout.FindSummary(fr));

        var en = Dir("en"); Meeting(en, "Point");
        Touch(Path.Combine(en, "Point - Minutes.docx"));
        Touch(Path.Combine(en, "Point - Summary.md"));
        Assert.Equal(new[] { "Point - Minutes.docx" }, SessionLayout.CrFiles(en).Select(Path.GetFileName));
        Assert.EndsWith(" - Summary.md", SessionLayout.FindSummary(en));
    }

    [Fact]
    public void HasCr_AndFindSummary_LegacyFolder()
    {
        var d = Dir();
        Touch(Path.Combine(d, "mic.wav"));
        Touch(Path.Combine(d, "Point - Summary.md"));    // a "general" CR in the old naming
        Touch(Path.Combine(d, "resume.md"), "short");
        Assert.True(SessionLayout.HasCr(d));
        Assert.Equal(Path.Combine(d, "resume.md"), SessionLayout.FindSummary(d));
    }

    // ── rename of the meeting ──────────────────────────────────────────────

    [Fact]
    public void RenameVisible_GivesEveryProductTheNewTitle_KeepsLabels_NeverOverwrites()
    {
        var d = Dir();
        Meeting(d, "Ancien nom");
        var old40 = "Un tres long titre de reunion qui depass";   // the pre-layout 40-character cut
        Touch(Path.Combine(d, "Ancien nom - Compte rendu.docx"));
        Touch(Path.Combine(d, "Ancien nom - Transcript.txt"));
        Touch(Path.Combine(d, $"{old40} - Audio.mp3"));
        Touch(Path.Combine(d, "Transcript - Résumé.md"));
        Touch(Path.Combine(d, "Ancien nom - Compte rendu.eml"), "old");
        Touch(Path.Combine(d, "Nouveau nom - Compte rendu.eml"), "already there");
        Touch(Path.Combine(d, "notes.md"));

        Meeting(d, "Nouveau nom");
        int n = SessionLayout.RenameVisible(d);

        Assert.Equal(4, n);
        var names = Directory.GetFiles(d).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[]
        {
            "Ancien nom - Compte rendu.eml",          // target taken: left in place
            "Nouveau nom - Audio.mp3",
            "Nouveau nom - Compte rendu.docx",
            "Nouveau nom - Compte rendu.eml",
            "Nouveau nom - Résumé.md",
            "Nouveau nom - Transcript.txt",
            "notes.md",
        }, names);
        Assert.Equal("already there", File.ReadAllText(Path.Combine(d, "Nouveau nom - Compte rendu.eml")));
    }
}
