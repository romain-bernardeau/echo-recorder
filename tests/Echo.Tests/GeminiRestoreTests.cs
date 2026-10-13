using System.Text;
using Echo.Ai;
using Echo.App;
using Echo.App.Engine;
using Echo.Core;
using Echo.Core.Abstractions;
using Echo.Core.Models;
using Echo.Core.Storage;

namespace Echo.Tests;

/// <summary>
/// Gemini is offered again (5.6.1 had hidden it and rewritten every "gemini" setting to
/// "mistral"), and OpenRouter joins as a chat-only provider. Pins the provider ids, the
/// fallback order, the "never transcribe on OpenRouter" rule, the settings that must survive a
/// reload, and the DPAPI round-trip of the OpenRouter key. Settings and secrets live in a
/// TempProfile, never in the real profile.
/// </summary>
public class GeminiRestoreTests
{
    // ── provider ids ────────────────────────────────────────────────────────────

    [Fact]
    public void Normalize_KeepsGeminiAndOpenRouter_UnknownStillMapsToMistral()
    {
        Assert.Equal("gemini", LlmProvider.Normalize("gemini"));
        Assert.Equal("gemini", LlmProvider.Normalize(" Gemini "));
        Assert.Equal("openrouter", LlmProvider.Normalize("OpenRouter"));
        Assert.Equal("cloudflare", LlmProvider.Normalize("cloudflare"));
        Assert.Equal("mistral", LlmProvider.Normalize("something-else"));
        Assert.Equal("mistral", LlmProvider.Normalize(null));
        Assert.Equal("Gemini", LlmProvider.DisplayName("gemini"));
        Assert.Equal("OpenRouter", LlmProvider.DisplayName("openrouter"));
    }

    [Fact]
    public void FallbackOrder_IsMistralGeminiCloudflareOpenRouter()
        => Assert.Equal(new[] { "mistral", "gemini", "cloudflare", "openrouter" }, LlmProvider.All.ToArray());

    [Fact]
    public void OpenRouter_CannotTranscribe_OthersCan()
    {
        Assert.False(LlmProvider.CanTranscribe("openrouter"));
        Assert.True(LlmProvider.CanTranscribe("mistral"));
        Assert.True(LlmProvider.CanTranscribe("gemini"));
        Assert.True(LlmProvider.CanTranscribe("cloudflare"));
        // A hand-edited transcription_provider = openrouter runs on Voxtral, like the Transcriber does.
        Assert.Equal("mistral", SessionManager.TranscriptionProviderOf(new AppSettings { TranscriptionProvider = "openrouter" }));
        Assert.Equal("gemini", SessionManager.TranscriptionProviderOf(new AppSettings { TranscriptionProvider = "gemini" }));
    }

    [Fact]
    public void AltProvider_TranscriptionNeverFallsBackToOpenRouter_ChatDoes()
    {
        using var p = new TempProfile();
        var paths = p.Paths();
        var store = new SettingsStore(paths);

        // Only an OpenRouter key: the CR may switch to it, the transcription has nowhere to go.
        var onlyOr = Manager(paths, store, "openrouter");
        Assert.Null(onlyOr.AltProvider("mistral", transcription: true));
        Assert.Equal("openrouter", onlyOr.AltProvider("mistral", transcription: false));

        // Gemini + OpenRouter keys: the transcription goes to Gemini, first in order.
        var both = Manager(paths, store, "gemini", "openrouter");
        Assert.Equal("gemini", both.AltProvider("mistral", transcription: true));
        Assert.Equal("gemini", both.AltProvider("mistral", transcription: false));
        Assert.Equal("openrouter", both.AltProvider("gemini", transcription: false));
        Assert.Null(both.AltProvider("gemini", transcription: true));
    }

    private static SessionManager Manager(StoragePaths paths, SettingsStore store, params string[] keyed)
        => new(() => throw new InvalidOperationException("no capture in tests"), null!, null!, null!, null!, null!,
               store, new KeysFor(keyed), paths, new NullNotifier(), new I18n(() => "fr"));

    // ── settings ────────────────────────────────────────────────────────────────

    [Fact]
    public void GeminiProviders_SurviveLoad_NoLongerRewrittenToMistral()
    {
        using var p = new TempProfile();
        Directory.CreateDirectory(Path.GetDirectoryName(p.SettingsFile)!);
        File.WriteAllText(p.SettingsFile,
            "{\"transcription_provider\":\"gemini\",\"cr_provider\":\"gemini\",\"gemini_cr_model\":\"gemini-3.6-flash\"}");

        var s = p.Store().Load();
        Assert.Equal("gemini", s.TranscriptionProvider);
        Assert.Equal("gemini", s.CrProvider);

        // And a save/reload keeps it on disk too.
        p.Store().Set("auto_minutes", true);
        Assert.Equal("gemini", p.Store().Load().CrProvider);
    }

    [Fact]
    public void Migration_DoesNotSwitchAnyoneToGeminiOrOpenRouter()
    {
        using var p = new TempProfile();
        Directory.CreateDirectory(Path.GetDirectoryName(p.SettingsFile)!);
        File.WriteAllText(p.SettingsFile, "{\"transcription_provider\":\"cloudflare\",\"cr_provider\":\"mistral\"}");
        var s = p.Store().Load();
        Assert.Equal("cloudflare", s.TranscriptionProvider);
        Assert.Equal("mistral", s.CrProvider);

        var fresh = new AppSettings();
        Assert.Equal("mistral", fresh.TranscriptionProvider);
        Assert.Equal("mistral", fresh.CrProvider);
    }

    [Fact]
    public void OpenRouterCrModel_DefaultsToAFreeModel_AndRoundTrips()
    {
        using var p = new TempProfile();
        var s = p.Store().Load();
        Assert.Equal(EchoConstants.DefaultOpenRouterCrModel, s.OpenRouterCrModel);
        Assert.EndsWith(":free", s.OpenRouterCrModel);

        p.Store().Set("openrouter_cr_model", "vendor/other:free");
        Assert.Equal("vendor/other:free", p.Store().Load().OpenRouterCrModel);
        Assert.Contains("\"openrouter_cr_model\"", File.ReadAllText(p.SettingsFile));
    }

    // ── secret store ────────────────────────────────────────────────────────────

    [Fact]
    public void OpenRouterKey_DpapiRoundTrip_InItsOwnFile()
    {
        var prevEnv = Environment.GetEnvironmentVariable(EchoConstants.EnvOpenRouterApiKey);
        using var p = new TempProfile();
        try
        {
            Environment.SetEnvironmentVariable(EchoConstants.EnvOpenRouterApiKey, null);
            var paths = p.Paths();
            var secret = new SecretStore(paths, new SettingsStore(paths));
            Assert.False(secret.OpenRouterKeyPresent());

            secret.SaveOpenRouter("  sk-or-v1-test-key  ");
            Assert.EndsWith("secret_openrouter.bin", paths.SecretOpenRouterBin);
            Assert.True(File.Exists(paths.SecretOpenRouterBin));
            Assert.False(File.Exists(paths.SecretBin));                       // the Mistral secret is untouched
            Assert.DoesNotContain("sk-or-v1-test-key", Encoding.UTF8.GetString(File.ReadAllBytes(paths.SecretOpenRouterBin)));

            Environment.SetEnvironmentVariable(EchoConstants.EnvOpenRouterApiKey, null);   // force the DPAPI read
            Assert.Equal("sk-or-v1-test-key", secret.ResolveOpenRouterKey());
            Assert.Equal("sk-or-v1-test-key", secret.ResolveKeyFor("openrouter"));
            Assert.True(secret.KeyPresentFor("openrouter"));

            secret.ClearOpenRouter();
            Assert.False(File.Exists(paths.SecretOpenRouterBin));
            Assert.Null(secret.ResolveOpenRouterKey());
        }
        finally
        {
            Environment.SetEnvironmentVariable(EchoConstants.EnvOpenRouterApiKey, prevEnv);
        }
    }

    // ── fakes ───────────────────────────────────────────────────────────────────

    private sealed class NullNotifier : INotifier
    {
        public void Notify(string message) { }
    }

    /// <summary>A key exists for the listed providers only.</summary>
    private sealed class KeysFor : ISecretStore
    {
        private readonly HashSet<string> _keyed;
        public KeysFor(params string[] keyed) => _keyed = new(keyed);
        public string? ResolveKey() => ResolveKeyFor("mistral");
        public bool KeyPresent() => ResolveKey() is not null;
        public void Save(string key) { }
        public void Clear() { }
        public string? ResolveGoogleKey() => ResolveKeyFor("gemini");
        public bool GoogleKeyPresent() => ResolveGoogleKey() is not null;
        public void SaveGoogle(string key) { }
        public void ClearGoogle() { }
        public string? ResolveCloudflareKey() => ResolveKeyFor("cloudflare");
        public bool CloudflareKeyPresent() => ResolveCloudflareKey() is not null;
        public void SaveCloudflare(string key) { }
        public void ClearCloudflare() { }
        public string? ResolveOpenRouterKey() => ResolveKeyFor("openrouter");
        public bool OpenRouterKeyPresent() => ResolveOpenRouterKey() is not null;
        public void SaveOpenRouter(string key) { }
        public void ClearOpenRouter() { }
        public string? ResolveKeyFor(string? provider) => _keyed.Contains(provider ?? "") ? "k-" + provider : null;
        public bool KeyPresentFor(string? provider) => ResolveKeyFor(provider) is not null;
    }
}
