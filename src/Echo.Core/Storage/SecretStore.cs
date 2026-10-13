using System.Security.Cryptography;
using System.Text;
using Echo.Core.Abstractions;

namespace Echo.Core.Storage;

/// <summary>
/// Mistral API key storage (secret_store.py). Default backend is Windows DPAPI
/// (per-user) with entropy "Voxra" — byte-identical to the Python app so an
/// existing secret.bin keeps decrypting. Also supports env and plain-text backends.
/// The key is NEVER written to settings.json — only the backend name is.
/// </summary>
public sealed class SecretStore : ISecretStore
{
    private readonly StoragePaths _paths;
    private readonly ISettingsStore _settings;

    public SecretStore(StoragePaths paths, ISettingsStore settings)
    {
        _paths = paths;
        _settings = settings;
    }

    public string? ResolveKey()
    {
        var env = Environment.GetEnvironmentVariable(EchoConstants.EnvApiKey);
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

        var (key, _) = LoadAny(_settings.Current.KeyStore);
        if (!string.IsNullOrWhiteSpace(key))
            // repopulate process env so any child inherits it
            Environment.SetEnvironmentVariable(EchoConstants.EnvApiKey, key);
        return key;
    }

    public bool KeyPresent() => !string.IsNullOrWhiteSpace(ResolveKey());

    public void Save(string key)
    {
        key = key?.Trim() ?? "";
        if (key.Length == 0) return;

        StoreDpapi(key);
        ClearOthers("dpapi");

        // Mutate a clone under the store lock: writing into the shared Current instance raced
        // with concurrent bridge calls.
        _settings.Update(s => s.KeyStore = "dpapi");

        Environment.SetEnvironmentVariable(EchoConstants.EnvApiKey, key);
        Environment.SetEnvironmentVariable(EchoConstants.EnvApiKey, key, EnvironmentVariableTarget.Process);
    }

    public void Clear()
    {
        foreach (var b in new[] { "dpapi", "env", "plain" }) ClearBackend(b);
        Environment.SetEnvironmentVariable(EchoConstants.EnvApiKey, null);
    }

    // ── backends ─────────────────────────────────────────────────────────────

    private (string? key, string? backend) LoadAny(string preferred)
    {
        var order = new List<string> { preferred };
        foreach (var b in new[] { "dpapi", "env", "plain" })
            if (!order.Contains(b)) order.Add(b);

        foreach (var b in order)
        {
            var k = LoadBackend(b);
            if (!string.IsNullOrWhiteSpace(k)) return (k, b);
        }
        return (null, null);
    }

    private string? LoadBackend(string backend) => backend switch
    {
        "dpapi" => LoadDpapi(),
        "env" => Environment.GetEnvironmentVariable(EchoConstants.EnvApiKey, EnvironmentVariableTarget.User),
        "plain" => File.Exists(_paths.SecretTxt) ? File.ReadAllText(_paths.SecretTxt).Trim() : null,
        _ => null,
    };

    private void ClearOthers(string keep)
    {
        foreach (var b in new[] { "dpapi", "env", "plain" })
            if (b != keep) ClearBackend(b);
    }

    private void ClearBackend(string backend)
    {
        try
        {
            switch (backend)
            {
                case "dpapi": if (File.Exists(_paths.SecretBin)) File.Delete(_paths.SecretBin); break;
                case "plain": if (File.Exists(_paths.SecretTxt)) File.Delete(_paths.SecretTxt); break;
                case "env": Environment.SetEnvironmentVariable(EchoConstants.EnvApiKey, null, EnvironmentVariableTarget.User); break;
            }
        }
        catch { /* best effort */ }
    }

    private void StoreDpapi(string key)
    {
        var data = Encoding.UTF8.GetBytes(key);
        var enc = ProtectedData.Protect(data, EchoConstants.DpapiEntropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(_paths.SettingsDir);
        File.WriteAllBytes(_paths.SecretBin, enc);
    }

    private string? LoadDpapi()
    {
        if (!File.Exists(_paths.SecretBin)) return null;
        try
        {
            var enc = File.ReadAllBytes(_paths.SecretBin);
            var data = ProtectedData.Unprotect(enc, EchoConstants.DpapiEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch { return null; }
    }

    // ── Google AI Studio (Gemini) key ────────────────────────────────────────────
    // Stored in a SEPARATE DPAPI file (secret_google.bin), same entropy/scope, so the
    // existing Mistral secret.bin is untouched. No plain/env-User backends: this key is
    // optional and only used when a Gemini provider is selected.

    public string? ResolveGoogleKey()
    {
        var env = Environment.GetEnvironmentVariable(EchoConstants.EnvGoogleApiKey);
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        var key = LoadGoogleDpapi();
        if (!string.IsNullOrWhiteSpace(key))
            Environment.SetEnvironmentVariable(EchoConstants.EnvGoogleApiKey, key);
        return key;
    }

    public bool GoogleKeyPresent() => !string.IsNullOrWhiteSpace(ResolveGoogleKey());

    public void SaveGoogle(string key)
    {
        key = key?.Trim() ?? "";
        if (key.Length == 0) return;
        var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), EchoConstants.DpapiEntropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(_paths.SettingsDir);
        File.WriteAllBytes(_paths.SecretGoogleBin, enc);
        Environment.SetEnvironmentVariable(EchoConstants.EnvGoogleApiKey, key);
        Environment.SetEnvironmentVariable(EchoConstants.EnvGoogleApiKey, key, EnvironmentVariableTarget.Process);
    }

    public void ClearGoogle()
    {
        try { if (File.Exists(_paths.SecretGoogleBin)) File.Delete(_paths.SecretGoogleBin); } catch { /* best effort */ }
        Environment.SetEnvironmentVariable(EchoConstants.EnvGoogleApiKey, null);
    }

    private string? LoadGoogleDpapi() => LoadDpapiFile(_paths.SecretGoogleBin);

    // ── Cloudflare Workers AI token ──────────────────────────────────────────────
    // Same scheme as the Google key: its own DPAPI file (secret_cloudflare.bin), env
    // override CLOUDFLARE_API_TOKEN. The account id is NOT a secret and lives in settings.

    public string? ResolveCloudflareKey()
    {
        var env = Environment.GetEnvironmentVariable(EchoConstants.EnvCloudflareApiToken);
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        var key = LoadDpapiFile(_paths.SecretCloudflareBin);
        if (!string.IsNullOrWhiteSpace(key))
            Environment.SetEnvironmentVariable(EchoConstants.EnvCloudflareApiToken, key);
        return key;
    }

    public bool CloudflareKeyPresent() => !string.IsNullOrWhiteSpace(ResolveCloudflareKey());

    public void SaveCloudflare(string key)
    {
        key = key?.Trim() ?? "";
        if (key.Length == 0) return;
        var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), EchoConstants.DpapiEntropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(_paths.SettingsDir);
        File.WriteAllBytes(_paths.SecretCloudflareBin, enc);
        Environment.SetEnvironmentVariable(EchoConstants.EnvCloudflareApiToken, key);
    }

    public void ClearCloudflare()
    {
        try { if (File.Exists(_paths.SecretCloudflareBin)) File.Delete(_paths.SecretCloudflareBin); } catch { /* best effort */ }
        Environment.SetEnvironmentVariable(EchoConstants.EnvCloudflareApiToken, null);
    }

    // ── OpenRouter API key ───────────────────────────────────────────────────────
    // Same scheme as the Cloudflare token: its own DPAPI file (secret_openrouter.bin), env
    // override OPENROUTER_API_KEY.

    public string? ResolveOpenRouterKey()
    {
        var env = Environment.GetEnvironmentVariable(EchoConstants.EnvOpenRouterApiKey);
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        var key = LoadDpapiFile(_paths.SecretOpenRouterBin);
        if (!string.IsNullOrWhiteSpace(key))
            Environment.SetEnvironmentVariable(EchoConstants.EnvOpenRouterApiKey, key);
        return key;
    }

    public bool OpenRouterKeyPresent() => !string.IsNullOrWhiteSpace(ResolveOpenRouterKey());

    public void SaveOpenRouter(string key)
    {
        key = key?.Trim() ?? "";
        if (key.Length == 0) return;
        var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), EchoConstants.DpapiEntropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(_paths.SettingsDir);
        File.WriteAllBytes(_paths.SecretOpenRouterBin, enc);
        Environment.SetEnvironmentVariable(EchoConstants.EnvOpenRouterApiKey, key);
    }

    public void ClearOpenRouter()
    {
        try { if (File.Exists(_paths.SecretOpenRouterBin)) File.Delete(_paths.SecretOpenRouterBin); } catch { /* best effort */ }
        Environment.SetEnvironmentVariable(EchoConstants.EnvOpenRouterApiKey, null);
    }

    // ── by provider id ───────────────────────────────────────────────────────────

    public string? ResolveKeyFor(string? provider) => (provider ?? "").Trim().ToLowerInvariant() switch
    {
        EchoConstants.ProviderGemini => ResolveGoogleKey(),
        EchoConstants.ProviderCloudflare => ResolveCloudflareKey(),
        EchoConstants.ProviderOpenRouter => ResolveOpenRouterKey(),
        EchoConstants.ProviderMistral or "" => ResolveKey(),
        _ => null,
    };

    public bool KeyPresentFor(string? provider) => !string.IsNullOrWhiteSpace(ResolveKeyFor(provider));

    private static string? LoadDpapiFile(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var enc = File.ReadAllBytes(path);
            var data = ProtectedData.Unprotect(enc, EchoConstants.DpapiEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch { return null; }
    }
}
