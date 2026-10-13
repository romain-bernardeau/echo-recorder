using System.Reflection;
using System.Text.Json;

namespace Echo.App;

/// <summary>
/// i18n table ported verbatim from the Python engine (tray_app.STRINGS), embedded
/// as Assets/strings.json. Backs <c>Api.strings</c> and the tray/notification labels.
/// Lookup falls back to French, then to the raw key (mirrors <c>tray_app.tr</c>).
/// </summary>
public sealed class I18n
{
    private readonly Dictionary<string, Dictionary<string, string>> _table;
    private readonly Func<string> _lang;

    public I18n(Func<string> currentLang)
    {
        _lang = currentLang;
        _table = Load();
    }

    /// <summary>The full {lang: {key: text}} table, for <c>Api.strings</c>.</summary>
    public IReadOnlyDictionary<string, Dictionary<string, string>> Table => _table;

    public string Lang => _lang();

    /// <summary>Translate <paramref name="key"/> in the current UI language.</summary>
    public string T(string key)
    {
        var lang = _lang();
        if (_table.TryGetValue(lang, out var m) && m.TryGetValue(key, out var v)) return v;
        if (_table.TryGetValue("fr", out var fr) && fr.TryGetValue(key, out var vf)) return vf;
        return key;
    }

    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            // Resource name = "<default-namespace>.Assets.strings.json" → find by suffix.
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("strings.json", StringComparison.OrdinalIgnoreCase));
            if (name is not null)
            {
                using var s = asm.GetManifestResourceStream(name)!;
                var doc = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(s);
                if (doc is not null) return doc;
            }
        }
        catch (Exception ex) { Log.Error("i18n load failed", ex); }
        return new() { ["fr"] = new(), ["en"] = new() };
    }
}
