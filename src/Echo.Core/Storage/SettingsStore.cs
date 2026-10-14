using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Echo.Core.Abstractions;
using Echo.Core.Models;

namespace Echo.Core.Storage;

/// <summary>
/// Loads/saves %APPDATA%\Echo\settings.json. Defaults are the base; the file
/// overrides key-by-key; missing keys back-filled; unknown keys preserved.
/// A read error NEVER silently reverts to defaults: the in-memory values are kept
/// (see Load). Writes are atomic and verified by re-reading the file.
/// </summary>
public sealed class SettingsStore : ISettingsStore
{
    private readonly StoragePaths _paths;
    private readonly object _lock = new();
    private AppSettings _current = new();

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep non-ASCII (ensure_ascii=False)
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    public SettingsStore(StoragePaths paths)
    {
        _paths = paths;
        _current = Load();
    }

    /// <summary>Diagnostics sink (wired to echo.log by the host). Every write and every
    /// failed read is traced: "the setting did not stick" must be answerable from the log
    /// alone instead of another round of static archaeology.</summary>
    public Action<string>? Log { get; set; }

    public AppSettings Current
    {
        get { lock (_lock) return _current; }
    }

    /// <summary>
    /// Re-read settings.json. A read that FAILS (unreadable, torn, locked by an indexer or
    /// antivirus for a moment) keeps the values already in memory: the previous behaviour —
    /// silently falling back to a fresh <see cref="AppSettings"/> — reset the whole in-memory
    /// state to defaults, and the next <see cref="Set"/> then persisted those defaults over
    /// the user's file. That is how a chosen provider could turn back into "mistral" between
    /// two meetings (2026-09-18: two recordings transcribed on voxtral although the file said
    /// cloudflare). Only a genuinely ABSENT file yields defaults.
    /// </summary>
    public AppSettings Load()
    {
        lock (_lock)
        {
            var loaded = LoadTolerant();
            if (loaded is null)
            {
                // Keep the in-memory values; never downgrade to defaults on a read error.
                Log?.Invoke("[settings] lecture impossible : les valeurs en mémoire sont conservées.");
                return _current;
            }
            _current = MigrateUpdateUrl(MigrateToFreeTier(HealBuiltinTemplateNames(loaded)));
            return _current;
        }
    }

    /// <summary>
    /// Free-plan model defaults: mistral-large-latest answers 403 on a free key. Applied on every
    /// load, in memory; persisted by the next Save. A gemini provider is left as chosen (5.6.1
    /// rewrote it to mistral; Gemini is offered again).
    /// </summary>
    /// <summary>
    /// 5.7.1: updates come from the latest GitHub release. Installs set up from a synced
    /// SharePoint folder point at a local "…\MAJ\version.json", which breaks as soon as the
    /// folder is no longer synced; an empty value disabled updates altogether. Both move to
    /// the GitHub URL.
    /// 5.8.1: the GitHub account was renamed (romain-bernardeau → romainio). GitHub redirects the
    /// old name only until someone else claims it, so the old repository URL moves too.
    /// 5.9.0: only the repository's releases are accepted (Updater.RefusedSource), so any other
    /// source, custom https included, moves to the default.
    /// </summary>
    private static AppSettings MigrateUpdateUrl(AppSettings s)
    {
        var url = s.UpdateUrl?.Trim() ?? "";
        if (!url.StartsWith(EchoConstants.TrustedUpdatePrefix, StringComparison.OrdinalIgnoreCase))
            s.UpdateUrl = EchoConstants.DefaultUpdateUrl;
        return s;
    }

    private static AppSettings MigrateToFreeTier(AppSettings s)
    {
        // 5.6.2: a free key also gets 0 req/min on mistral-small/medium (2026-10-13), so the
        // free default is ministral-8b-latest. One-shot (models_migration), so a paid user can
        // pick mistral-small or mistral-large again afterwards.
        if (s.ModelsMigration < 2)
        {
            foreach (var paid in new[] { "mistral-large-latest", "mistral-medium-latest", "mistral-small-latest" })
            {
                if (string.Equals(s.MistralCrModel?.Trim(), paid, StringComparison.OrdinalIgnoreCase)) s.MistralCrModel = EchoConstants.DefaultPostprocessModel;
                if (string.Equals(s.PostprocessModel?.Trim(), paid, StringComparison.OrdinalIgnoreCase)) s.PostprocessModel = EchoConstants.DefaultPostprocessModel;
            }
            s.ModelsMigration = 2;
        }
        return s;
    }

    /// <summary>
    /// Built-in CR templates carry app-provided names ("Détaillé"/"Synthèse"). An early
    /// build persisted them with a mangled encoding (double UTF-8/CP1252 round-trip, e.g.
    /// "Détaillé" stored as "DÃƒÂ©taillÃƒÂ©"), which then stuck in settings.json. Re-assert
    /// the canonical name by Id on every load so existing installs self-heal; user
    /// templates are left untouched.
    /// </summary>
    private static AppSettings HealBuiltinTemplateNames(AppSettings s)
    {
        try
        {
            if (s.CrTemplates is { Count: > 0 })
            {
                var canon = CrTemplate.Defaults().ToDictionary(t => t.Id, t => t.Name);
                foreach (var t in s.CrTemplates)
                    if (t.Builtin && !string.IsNullOrEmpty(t.Id) && canon.TryGetValue(t.Id, out var name))
                        t.Name = name;
            }
        }
        catch { /* names are cosmetic; never block load */ }
        return s;
    }

    /// <summary>
    /// Tolerant load (Python parity: <c>{**DEFAULTS, **loaded}</c>). A single bad or
    /// hand-edited value must NOT reset every preference: on a whole-file deserialize
    /// failure we fall back to a per-key merge over defaults, skipping only the
    /// offending key(s). (Audit finding F3.)
    /// </summary>
    /// <returns>The loaded settings, defaults when the file does not exist yet, or
    /// <c>null</c> when the file exists but could not be read/parsed — the caller then
    /// keeps whatever is already in memory rather than reverting the user's choices.</returns>
    private AppSettings? LoadTolerant()
    {
        if (!File.Exists(_paths.SettingsFile)) return new AppSettings();

        JsonObject? fileObj = null;
        try
        {
            fileObj = JsonNode.Parse(ReadShared(_paths.SettingsFile)) as JsonObject;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[settings] échec de lecture de {_paths.SettingsFile} : {ex.GetType().Name} {ex.Message}");
            BackupUnreadable();
            return null;
        }
        if (fileObj is null)
        {
            Log?.Invoke("[settings] fichier présent mais illisible (JSON non conforme).");
            BackupUnreadable();
            return null;
        }

        // Fast path: the whole file deserialises (missing keys back-filled by defaults).
        try
        {
            var loaded = fileObj.Deserialize<AppSettings>(Opts);
            if (loaded is not null) return loaded;
        }
        catch { /* fall through to the per-key merge */ }

        var merged = JsonSerializer.SerializeToNode(new AppSettings(), Opts)!.AsObject();
        foreach (var kv in fileObj.ToList())
        {
            var backup = merged.ContainsKey(kv.Key) ? merged[kv.Key]?.DeepClone() : null;
            bool had = merged.ContainsKey(kv.Key);
            merged[kv.Key] = kv.Value?.DeepClone();
            try { _ = merged.Deserialize<AppSettings>(Opts); }
            catch
            {
                if (had) merged[kv.Key] = backup; else merged.Remove(kv.Key);
            }
        }
        try { return merged.Deserialize<AppSettings>(Opts) ?? new AppSettings(); }
        catch { return new AppSettings(); }
    }

    /// <summary>Open the file with the widest possible sharing: an indexer or antivirus
    /// holding a transient read/write handle must not turn into "settings lost".</summary>
    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                      FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    /// <summary>Keep one copy of a file we could not parse, so a later "my settings reset
    /// themselves" can be investigated on the actual bytes. Best-effort, never throws.</summary>
    private void BackupUnreadable()
    {
        try
        {
            var bad = Path.Combine(_paths.SettingsDir, "settings.bad.json");
            if (File.Exists(bad)) return;                 // keep the FIRST corruption only
            File.Copy(_paths.SettingsFile, bad, overwrite: false);
            Log?.Invoke($"[settings] copie du fichier illisible conservée : {bad}");
        }
        catch { /* forensics only */ }
    }

    /// <summary>
    /// Persist the whole model. The write is ATOMIC (temp file + <see cref="File.Move(string,string,bool)"/>):
    /// the previous truncate-then-write could be read half-written by the processing thread,
    /// which then loaded a torn file. A reader now always sees either the old or the new file.
    /// </summary>
    public void Save(AppSettings settings)
    {
        lock (_lock)
        {
            _current = settings;
            Directory.CreateDirectory(_paths.SettingsDir);
            var json = JsonSerializer.Serialize(_current, Opts);
            var tmp = _paths.SettingsFile + ".tmp";
            try
            {
                File.WriteAllText(tmp, json);
                File.Move(tmp, _paths.SettingsFile, overwrite: true);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[settings] ÉCHEC d'écriture de {_paths.SettingsFile} : {ex.GetType().Name} {ex.Message}");
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }
    }

    /// <summary>Set one key by JSON name and persist (Api.set_setting). The value is read
    /// back from disk right after the write and traced: a preference that does not stick is
    /// then visible in echo.log at the moment of the click, not three meetings later.</summary>
    public void Set(string jsonKey, object? value)
    {
        lock (_lock)
        {
            var node = JsonSerializer.SerializeToNode(_current, Opts)!.AsObject();
            node[jsonKey] = value is null ? null : JsonSerializer.SerializeToNode(value, Opts);
            _current = node.Deserialize<AppSettings>(Opts) ?? _current;
            Save(_current);
            VerifyOnDisk(jsonKey);
        }
    }

    /// <summary>Mutate a clone of the current settings under the lock, then persist it
    /// atomically. Readers holding the previous <see cref="Current"/> instance never see a
    /// half-applied change, and two concurrent bridge calls can no longer lose an update.</summary>
    public void Update(Action<AppSettings> mutate)
    {
        lock (_lock)
        {
            var clone = JsonSerializer.SerializeToNode(_current, Opts)!.Deserialize<AppSettings>(Opts) ?? new AppSettings();
            mutate(clone);
            Save(clone);
        }
    }

    /// <summary>Re-read the file and confirm the key holds what we just wrote.</summary>
    private void VerifyOnDisk(string jsonKey)
    {
        if (Log is null) return;
        try
        {
            var wanted = JsonSerializer.SerializeToNode(_current, Opts)!.AsObject()[jsonKey]?.ToJsonString() ?? "null";
            var onDisk = (JsonNode.Parse(ReadShared(_paths.SettingsFile)) as JsonObject)?[jsonKey]?.ToJsonString() ?? "null";
            Log(wanted == onDisk
                ? $"[settings] {jsonKey} = {(PersonalKeys.Contains(jsonKey) ? "(masqué)" : Short(onDisk))} (vérifié sur disque)"
                : $"[settings] ANOMALIE {jsonKey} : la valeur relue diffère de celle écrite");
        }
        catch (Exception ex) { Log($"[settings] {jsonKey} : relecture de contrôle impossible ({ex.GetType().Name})"); }
    }

    /// <summary>Settings that hold personal or client data: logged by name only (logs end up in bug reports).</summary>
    private static readonly HashSet<string> PersonalKeys = new(StringComparer.Ordinal)
    {
        "user_name", "user_email", "flags", "cr_templates", "recordings_dir", "cloudflare_account_id",
    };

    private static string Short(string v) => v.Length <= 120 ? v : v[..120] + "…";
}
