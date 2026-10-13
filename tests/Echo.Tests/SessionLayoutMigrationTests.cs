using Echo.Core.Storage;

namespace Echo.Tests;

/// <summary>
/// Startup tidy-up of legacy meeting folders (<see cref="SessionLayoutMigration"/>): exact
/// target layout, moves only (no copy, no overwrite, no delete), idempotence, collisions,
/// live and locked sessions left for the next start.
/// </summary>
public sealed class SessionLayoutMigrationTests : IDisposable
{
    private readonly string _root;
    private readonly List<string> _log = new();

    public SessionLayoutMigrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "echo-layoutmig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static readonly DateTime T0 = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    private static void Touch(string path, string? text = null, int minute = 0)
    {
        File.WriteAllText(path, text ?? Path.GetFileName(path));   // content = original name: proves a move
        File.SetLastWriteTimeUtc(path, T0.AddMinutes(minute));
    }

    /// <summary>A pre-layout folder as 5.6 left it: everything at the root, 40-character prefix.</summary>
    private string Legacy(string name, string meetingName, bool processed = true, string crLabel = "Minutes", bool resume = true)
    {
        var d = Path.Combine(_root, $"20261001 - 09h00 - {name}");
        Directory.CreateDirectory(d);
        Directory.CreateDirectory(Path.Combine(d, "Captures"));
        Touch(Path.Combine(d, "Captures", "Capture 1.png"));
        File.WriteAllText(Path.Combine(d, "meeting.json"), $$"""{"name":"{{meetingName}}"}""");
        foreach (var f in new[] { "session.json", "mic.json", "system.json", "speakers.jsonl", "roster.jsonl",
                                  "meeting_title.txt", "transcript.json", "postprocess_log.json", ".echo_status",
                                  "Infos réunion.txt", "emails.json", "flag.txt", "mic.mp3", "system.mp3",
                                  "system_16k.wav", "mic_16k_trim_c1.mp3", "notes.md" })
            Touch(Path.Combine(d, f));
        File.WriteAllText(Path.Combine(d, ".echo_status"), "error\n1\n0\n");
        var p40 = meetingName.Length > 40 ? meetingName[..40] : meetingName;
        Touch(Path.Combine(d, $"{p40} - Transcript.txt"), minute: 1);
        if (processed) Touch(Path.Combine(d, $"{p40} - Processed Transcript.txt"), minute: 2);
        Touch(Path.Combine(d, $"{p40} - {crLabel}.eml"), minute: 3);
        Touch(Path.Combine(d, $"{p40} - {crLabel}.docx"), minute: 3);
        Touch(Path.Combine(d, $"{p40} - Audio.mp3"));
        if (resume) Touch(Path.Combine(d, "resume.md"), minute: 4);
        return d;
    }

    private static string[] Root(string d)
        => Directory.GetFiles(d).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToArray()!;

    private static string[] Hidden(string d)
        => Directory.GetFiles(Path.Combine(d, ".echo")).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToArray()!;

    private static int CountAll(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length;

    [Fact]
    public void Migrate_LegacyWithProcessedTranscript_French_ExactLayout()
    {
        var name = "Revue mensuelle du portefeuille fournisseurs et des achats";   // > 40 characters
        var d = Legacy("Revue", name);
        int before = CountAll(_root);

        var r = SessionLayoutMigration.Run(_root, "fr", _log.Add);

        Assert.Equal(1, r.Sessions);
        Assert.Equal(0, r.Skipped);
        Assert.Equal(new[]
        {
            $"{name} - Audio.mp3",
            $"{name} - Compte rendu.docx",
            $"{name} - Compte rendu.eml",
            $"{name} - Résumé.md",
            $"{name} - Transcript.txt",
            "notes.md",
        }, Root(d));
        Assert.Equal(new[]
        {
            ".echo_status", "Infos réunion.txt", "emails.json", "flag.txt", "layout", "meeting.json", "meeting_title.txt",
            "mic.json", "mic.mp3", "mic_16k_trim_c1.mp3", "postprocess_log.json", "roster.jsonl", "session.json",
            "speakers.jsonl", "system.json", "system.mp3", "system_16k.wav", "transcript-raw.txt", "transcript.json",
        }, Hidden(d));
        // The corrected transcript is the visible one, the raw one is kept aside.
        Assert.Equal($"{name[..40]} - Processed Transcript.txt", File.ReadAllText(Path.Combine(d, $"{name} - Transcript.txt")));
        Assert.Equal($"{name[..40]} - Transcript.txt", File.ReadAllText(Path.Combine(d, ".echo", "transcript-raw.txt")));
        Assert.Equal("resume.md", File.ReadAllText(Path.Combine(d, $"{name} - Résumé.md")));
        Assert.True(new DirectoryInfo(Path.Combine(d, ".echo")).Attributes.HasFlag(FileAttributes.Hidden));
        Assert.True(Directory.Exists(Path.Combine(d, "Captures")));
        Assert.Equal(before + 1, CountAll(_root));   // nothing lost: + the .echo\layout marker only
        Assert.Contains(_log, l => l.StartsWith("[layout] 1 session(s) rangée(s)"));

        // Everything still reads through the layout.
        Assert.True(SessionLayout.HasCr(d));
        Assert.Equal(Path.Combine(d, $"{name} - Transcript.txt"), SessionLayout.FindTranscript(d));
        Assert.Equal(Path.Combine(d, $"{name} - Résumé.md"), SessionLayout.FindSummary(d));
        Assert.Equal("error", SessionStatus.Read(d)!.State);
    }

    [Fact]
    public void Migrate_NoProcessedTranscript_SummaryCr_English()
    {
        var d = Legacy("Sync", "Weekly sync", processed: false, crLabel: "Summary");

        SessionLayoutMigration.Run(_root, "en", _log.Add);

        Assert.Equal(new[]
        {
            "Weekly sync - Audio.mp3",
            "Weekly sync - Minutes.docx",       // a "Summary" CR is a CR: one name whatever the template
            "Weekly sync - Minutes.eml",
            "Weekly sync - Summary.md",         // the short summary (ex resume.md)
            "Weekly sync - Transcript.txt",     // the raw one: no corrected transcript exists
            "notes.md",
        }, Root(d));
        Assert.DoesNotContain("transcript-raw.txt", Hidden(d));
        Assert.Equal(new[] { "Weekly sync - Minutes.docx", "Weekly sync - Minutes.eml" },
                     SessionLayout.CrFiles(d).Select(Path.GetFileName));
        Assert.Equal("resume.md", File.ReadAllText(SessionLayout.FindSummary(d)!));
    }

    [Fact]
    public void Migrate_IsIdempotent_AndKeepsTheFolderDate()
    {
        var d = Legacy("Revue", "Revue");
        var stamp = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(d, stamp);

        var first = SessionLayoutMigration.Run(_root, "fr");
        var layout = Root(d).Concat(Hidden(d)).ToArray();
        var second = SessionLayoutMigration.Run(_root, "fr", _log.Add);

        Assert.True(first.Moved > 0);
        Assert.Equal(new SessionLayoutMigration.Result(0, 0, 0), second);
        Assert.Equal(layout, Root(d).Concat(Hidden(d)).ToArray());
        Assert.Equal("1", File.ReadAllText(Path.Combine(d, ".echo", "layout")).Trim());
        Assert.Equal(stamp, Directory.GetLastWriteTimeUtc(d));   // the meeting list keeps its order
    }

    [Fact]
    public void Migrate_Collision_LeavesTheSourceInPlace_AndLogs()
    {
        var d = Legacy("Revue", "Revue", resume: false);
        Directory.CreateDirectory(Path.Combine(d, ".echo"));
        File.WriteAllText(Path.Combine(d, ".echo", "flag.txt"), "already in .echo");

        var r = SessionLayoutMigration.Run(_root, "fr", _log.Add);

        Assert.Equal(1, r.Skipped);
        Assert.Equal("flag.txt", File.ReadAllText(Path.Combine(d, "flag.txt")));
        Assert.Equal("already in .echo", File.ReadAllText(Path.Combine(d, ".echo", "flag.txt")));
        Assert.Contains(_log, l => l.Contains("flag.txt") && l.Contains("existe déjà"));
        Assert.Contains(_log, l => l.Contains("1 ignoré(s)"));
    }

    [Fact]
    public void Migrate_SkipsASessionThisEchoIsProcessing()
    {
        var d = Legacy("Live", "Live");
        File.WriteAllText(Path.Combine(d, ".echo_status"), $"running\n{Environment.ProcessId}\n{DateTimeOffset.Now.ToUnixTimeSeconds()}\n");
        var before = Root(d);

        var r = SessionLayoutMigration.Run(_root, "fr", _log.Add);

        Assert.Equal(0, r.Moved);
        Assert.Equal(before, Root(d));
        Assert.False(Directory.Exists(Path.Combine(d, ".echo")));
        Assert.Contains(_log, l => l.Contains("traitement en cours"));
    }

    [Fact]
    public void Migrate_LockedFile_IsRetriedAtTheNextStart()
    {
        var d = Legacy("Rec", "Rec");
        var wav = Path.Combine(d, "mic.wav");
        Touch(wav);
        using (new FileStream(wav, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var r = SessionLayoutMigration.Run(_root, "en", _log.Add);
            Assert.Equal(1, r.Skipped);
            Assert.True(File.Exists(wav));
            Assert.Equal("partial", File.ReadAllText(Path.Combine(d, ".echo", "layout")).Trim());
        }

        var again = SessionLayoutMigration.Run(_root, "en", _log.Add);
        Assert.Equal(1, again.Moved);
        Assert.False(File.Exists(wav));
        Assert.True(File.Exists(Path.Combine(d, ".echo", "mic.wav")));
        Assert.Equal("1", File.ReadAllText(Path.Combine(d, ".echo", "layout")).Trim());
        Assert.Contains("Rec - Summary.md", Root(d));   // the English summary made in the first pass is not taken for a CR
    }

    [Fact]
    public void Migrate_IgnoresFoldersThatAreNotSessions_AndUnknownFiles()
    {
        var manual = Path.Combine(_root, "Captures manuelles");
        Directory.CreateDirectory(manual);
        Touch(Path.Combine(manual, "Capture.png"));
        var d = Legacy("Revue", "Revue");
        Touch(Path.Combine(d, "Mon document.xlsx"));

        SessionLayoutMigration.Run(_root, "fr");

        Assert.False(Directory.Exists(Path.Combine(manual, ".echo")));
        Assert.True(File.Exists(Path.Combine(d, "Mon document.xlsx")));
    }
}
