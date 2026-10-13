using System.Text.Json;
using Echo.Core.Models;
using Echo.Core.Storage;

namespace Echo.Tests;

/// <summary>
/// Guards that every persisted setting survives the full round-trip UI -> disk -> UI.
///
/// Regression (2026-07-15): "audio_retention_days" (and its sibling "keep_original_wav")
/// were written to settings.json correctly but read back as their default on every
/// restart. The cause was NOT the store: it was the get_meta projection, which used a
/// hand-maintained allow-list of keys and never listed the two v2 disk-housekeeping keys.
/// The fix projects the WHOLE model (Bridge.GetMeta: settings = AppSettings). These tests
/// pin both halves: the store persists across instances, and the model — serialised the
/// same way the WebView reply is (default options) — exposes every key the front-end reads.
/// </summary>
public sealed class SettingsPersistenceTests
{
    // Keys the settings UI reads from META.settings (echo-dotnet/ui/index.html, renderSettings).
    private static readonly string[] UiReadKeys =
    {
        "app_lang", "ui_theme", "cr_lang", "cr_type", "cr_out_lang", "cr_formats",
        "auto_detect", "auto_transcribe", "auto_minutes", "auto_record",
        "auto_screenshot", "postprocess_llm", "user_name", "update_url", "update_auto",
        "recordings_dir", "cr_template", "keep_original_wav", "audio_retention_days",
    };

    [Fact]
    public void GetMetaProjection_SurfacesEveryKeyTheUiReads()
    {
        var s = new AppSettings { AudioRetentionDays = 7, KeepOriginalWav = true, UiTheme = "dark" };

        // Mirror WebViewHost's JS<->native reply exactly: default options, no naming policy.
        var json = JsonSerializer.Serialize(new { settings = s });
        using var doc = JsonDocument.Parse(json);
        var settings = doc.RootElement.GetProperty("settings");

        foreach (var key in UiReadKeys)
            Assert.True(settings.TryGetProperty(key, out _), $"get_meta.settings is missing '{key}'");

        // The exact regressed keys must carry their real (non-default) values, not defaults.
        Assert.Equal(7, settings.GetProperty("audio_retention_days").GetInt32());
        Assert.True(settings.GetProperty("keep_original_wav").GetBoolean());
        Assert.Equal("dark", settings.GetProperty("ui_theme").GetString());
    }

    [Fact]
    public void Set_PersistsAcrossStoreInstances()
    {
        using var profile = new TempProfile();
        {
            // First instance: change a numeric and a boolean setting.
            var store1 = new SettingsStore(profile.Paths());
            store1.Set("audio_retention_days", 1);
            store1.Set("keep_original_wav", true);

            // A brand-new instance reads only from disk (simulates the app restart).
            var store2 = new SettingsStore(profile.Paths());
            Assert.Equal(1, store2.Current.AudioRetentionDays);
            Assert.True(store2.Current.KeepOriginalWav);
        }
    }

    [Fact]
    public void Set_NumericFromJsonElement_PersistsAsNumber()
    {
        // The bridge boxes the value as a JsonElement (JS sends a number). Set must persist it
        // as a real number so the typed int property reloads correctly — not silently dropped.
        using var profile = new TempProfile();
        {
            using var jd = JsonDocument.Parse("3");
            var store = new SettingsStore(profile.Paths());
            store.Set("audio_retention_days", jd.RootElement.Clone());

            var reloaded = new SettingsStore(profile.Paths());
            Assert.Equal(3, reloaded.Current.AudioRetentionDays);
        }
    }
}
