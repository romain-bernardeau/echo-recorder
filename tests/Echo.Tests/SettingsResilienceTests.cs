using Echo.Core.Models;
using Echo.Core.Storage;

namespace Echo.Tests;

/// <summary>
/// Guards the settings store against the failure mode behind "my provider choice does not
/// stick" (2026-09-18: two meetings transcribed on Mistral/voxtral although settings.json
/// held transcription_provider = cloudflare, with no fallback in the log).
///
/// The store used to answer a FAILED read with a fresh <see cref="AppSettings"/>. Everything
/// the user had chosen was then gone from memory, and the very next set_setting persisted
/// those defaults back over the file. These tests pin the two halves of the fix: a read error
/// keeps the in-memory values, and a write is atomic so a concurrent reader can never see a
/// half-written file.
/// </summary>
/// <remarks>Writes only inside a <see cref="TempProfile"/>, which refuses to resolve to a
/// real user profile.</remarks>
public sealed class SettingsResilienceTests : IDisposable
{
    private readonly TempProfile _profile = new();

    public void Dispose() => _profile.Dispose();

    private static string SettingsFile(StoragePaths p) => p.SettingsFile;

    [Fact]
    public void Load_OnUnreadableFile_KeepsInMemoryValues_AndDoesNotRevertToDefaults()
    {
        var paths = _profile.Paths();
        var store = _profile.Store();
        store.Set("transcription_provider", "cloudflare");
        Assert.Equal("cloudflare", store.Current.TranscriptionProvider);

        // The file is there but unparseable (a torn write, a truncated copy, a bad edit).
        File.WriteAllText(SettingsFile(paths), "{\"transcription_provider\": \"cloud");

        var reloaded = store.Load();

        Assert.Equal("cloudflare", reloaded.TranscriptionProvider);          // NOT "mistral"
        Assert.Equal("cloudflare", store.Current.TranscriptionProvider);
        Assert.True(File.Exists(Path.Combine(paths.SettingsDir, "settings.bad.json")),
                    "the unreadable file should be kept for diagnosis");
    }

    [Fact]
    public void Set_AfterAnUnreadableFile_WritesTheUserValues_NotDefaults()
    {
        var paths = _profile.Paths();
        var store = _profile.Store();
        store.Set("transcription_provider", "cloudflare");
        store.Set("audio_retention_days", 3);

        File.WriteAllText(SettingsFile(paths), "not json at all");
        store.Load();                                   // the failing read used to wipe memory
        store.Set("ui_theme", "dark");                  // ... and this used to persist defaults

        var fresh = new SettingsStore(_profile.Paths()).Current;
        Assert.Equal("cloudflare", fresh.TranscriptionProvider);
        Assert.Equal(3, fresh.AudioRetentionDays);
        Assert.Equal("dark", fresh.UiTheme);
    }

    [Fact]
    public void Load_OnMissingFile_StillYieldsDefaults()
    {
        var paths = _profile.Paths();
        var store = _profile.Store();
        store.Set("transcription_provider", "gemini");
        File.Delete(SettingsFile(paths));

        // A first run (no file at all) is a different case from a broken file: defaults apply.
        Assert.Equal("mistral", new SettingsStore(_profile.Paths()).Current.TranscriptionProvider);
    }

    [Fact]
    public void Save_IsAtomic_NoTemporaryFileIsLeftBehind()
    {
        var paths = _profile.Paths();
        var store = _profile.Store();
        store.Set("cr_provider", "cloudflare");

        Assert.False(File.Exists(SettingsFile(paths) + ".tmp"));
        var onDisk = File.ReadAllText(SettingsFile(paths));
        Assert.Contains("\"cr_provider\": \"cloudflare\"", onDisk);
    }

    [Fact]
    public void Set_TracesTheVerifiedValue()
    {
        var paths = _profile.Paths();
        var log = new List<string>();
        var store = new SettingsStore(paths) { Log = log.Add };

        store.Set("transcription_provider", "cloudflare");

        Assert.Contains(log, l => l.Contains("transcription_provider") && l.Contains("vérifié sur disque"));
    }

    [Fact]
    public void NewRetentionKey_RoundTripsAsANumber()
    {
        var paths = _profile.Paths();
        var store = _profile.Store();
        store.Set("failed_audio_retention_days", 45);

        Assert.Equal(45, new SettingsStore(_profile.Paths()).Current.FailedAudioRetentionDays);
    }
}
