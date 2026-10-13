using Echo.App;
using Echo.App.Engine;
using Echo.Core.Storage;

namespace Echo.Tests;

/// <summary>Hotkey parsing, the shared .echo_status reading, the session-folder helpers and
/// the tail read of speakers.jsonl.</summary>
public sealed class EngineHotkeyAndStatusTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "echo-st-" + Guid.NewGuid().ToString("N"));

    public EngineHotkeyAndStatusTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    // ── HotkeyManager.TryParse ─────────────────────────────────────────────────

    [Theory]
    [InlineData("Ctrl+Alt+R", 0x2u | 0x1u, (uint)'R')]
    [InlineData("ctrl + shift + 5", 0x2u | 0x4u, (uint)'5')]
    [InlineData("Win+F9", 0x8u, 0x78u)]
    [InlineData("Alt+Espace", 0x1u, 0x20u)]
    [InlineData("Maj+Ctrl+F24", 0x4u | 0x2u, 0x87u)]
    public void TryParse_ValidCombos(string combo, uint mods, uint vk)
    {
        Assert.True(HotkeyManager.TryParse(combo, out var m, out var v));
        Assert.Equal(mods, m);
        Assert.Equal(vk, v);
    }

    [Theory]
    [InlineData("R")]               // no modifier
    [InlineData("Ctrl+Alt")]        // no main key
    [InlineData("Ctrl+A+B")]        // two main keys
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+Échap")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_Rejects(string? combo)
        => Assert.False(HotkeyManager.TryParse(combo, out _, out _));

    // ── SessionStatus ───────────────────────────────────────────────────────────

    private const int Me = 1000;
    private static readonly long Now = 1_800_000_000;

    [Theory]
    [InlineData("running\n1000\n1799999000\n", "running")]         // ours, recent
    [InlineData("running\n1000\n1700000000\n", null)]              // ours but > 3 h: dead
    [InlineData("running\n4242\n1799999000\n", null)]              // another process
    [InlineData("running\n", null)]                                // no pid
    [InlineData("waiting\n1000\n1700000000\n", "waiting")]         // queued by us (no age limit)
    [InlineData("waiting\n4242\n1799999000\n", "error")]           // queued by a dead run
    [InlineData("error\n4242\n0\n", "error")]
    [InlineData("weird", null)]
    public void Effective_IsTheSameReadingEverywhere(string raw, string? expected)
        => Assert.Equal(expected, SessionStatus.Effective(SessionStatus.Parse(raw), Me, Now));

    [Fact]
    public void IsInterrupted_OnlyForAGoneProcess()
    {
        var foreign = SessionStatus.Parse("running\n4242\n0\n");
        Assert.True(SessionStatus.IsInterrupted(foreign, Me, _ => false));
        Assert.False(SessionStatus.IsInterrupted(foreign, Me, _ => true));      // another live Echo
        Assert.False(SessionStatus.IsInterrupted(SessionStatus.Parse("running\n1000\n0\n"), Me, _ => false));
        Assert.False(SessionStatus.IsInterrupted(SessionStatus.Parse("error\n4242\n0\n"), Me, _ => false));
        Assert.True(SessionStatus.IsInterrupted(SessionStatus.Parse("waiting\n"), Me, _ => true));
    }

    // ── SessionFiles ────────────────────────────────────────────────────────────

    [Fact]
    public void UniqueDir_AppendsACounter_WhenTheMinuteFolderExists()
    {
        var d = Path.Combine(_root, "20261007 - 10h00 - Point");
        Assert.Equal(d, SessionFiles.UniqueDir(d));
        Directory.CreateDirectory(d);
        Assert.Equal(d + " (2)", SessionFiles.UniqueDir(d));
        Directory.CreateDirectory(d + " (2)");
        Assert.Equal(d + " (3)", SessionFiles.UniqueDir(d));
    }

    [Fact]
    public void HasAudio_TracksOrMixdown_NotHeadersOrNothing()
    {
        var d = Path.Combine(_root, "s");
        Directory.CreateDirectory(d);
        Assert.False(SessionFiles.HasAudio(d));
        File.WriteAllBytes(Path.Combine(d, "mic.wav"), new byte[44]);            // header only
        Assert.False(SessionFiles.HasAudio(d));
        File.WriteAllBytes(Path.Combine(d, "Point - Audio.mp3"), new byte[10]);
        Assert.True(SessionFiles.HasAudio(d));
        File.Delete(Path.Combine(d, "Point - Audio.mp3"));
        File.WriteAllBytes(Path.Combine(d, "system.mp3"), new byte[5000]);
        Assert.True(SessionFiles.HasAudio(d));
    }

    // ── speakers.jsonl tail ────────────────────────────────────────────────────

    [Fact]
    public void LastSpeakers_ReadsOnlyTheTail_AndSkipsAPartialLastLine()
    {
        using var ms = new MemoryStream();
        var w = new StreamWriter(ms);
        for (int i = 0; i < 2000; i++) w.Write($"{{\"t\":{i},\"speakers\":[\"Orateur {i}\"]}}\n");
        w.Write("{\"t\":2000,\"speakers\":[\"Coup");                          // being written
        w.Flush();
        Assert.True(ms.Length > SessionQuery.SpeakersTailBytes * 4);

        var last = SessionQuery.LastSpeakersFromTail(ms);

        Assert.Equal(new[] { "Orateur 1999" }, last);
    }

    [Fact]
    public void LastSpeakers_SmallFile()
    {
        using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            "{\"speakers\":[\"A\"]}\n{\"speakers\":[\"Hélène\",\"B\"]}\n"));
        Assert.Equal(new[] { "Hélène", "B" }, SessionQuery.LastSpeakersFromTail(ms));
    }
}
