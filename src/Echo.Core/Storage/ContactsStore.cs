using System.Text.Encodings.Web;
using System.Text.Json;
using Echo.Core.Abstractions;

namespace Echo.Core.Storage;

/// <summary>
/// Cross-meeting learned address book (Teams display name → e-mail), stored at
/// %LOCALAPPDATA%\Echo\contacts.json. Teams UI Automation only exposes display
/// names, so each e-mail entered once is remembered and re-applied everywhere.
/// </summary>
public sealed class ContactsStore : IContactsStore
{
    private readonly StoragePaths _paths;
    private readonly object _lock = new();
    private Dictionary<string, string> _map;

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public ContactsStore(StoragePaths paths)
    {
        _paths = paths;
        _map = LoadMap();
    }

    public IReadOnlyDictionary<string, string> All()
    {
        lock (_lock) return new Dictionary<string, string>(_map);
    }

    public string? Get(string displayName)
    {
        lock (_lock) return _map.TryGetValue(displayName, out var e) ? e : null;
    }

    public void Set(string displayName, string? email)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return;
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(email)) _map.Remove(displayName);
            else _map[displayName] = email.Trim();
            SaveMap();
        }
    }

    private Dictionary<string, string> LoadMap()
    {
        try
        {
            if (File.Exists(_paths.ContactsFile))
            {
                var json = File.ReadAllText(_paths.ContactsFile);
                return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                       ?? new Dictionary<string, string>();
            }
        }
        catch { /* ignore corrupt file */ }
        return new Dictionary<string, string>();
    }

    private void SaveMap()
    {
        Directory.CreateDirectory(_paths.DataDir);
        File.WriteAllText(_paths.ContactsFile, JsonSerializer.Serialize(_map, Opts));
    }
}
