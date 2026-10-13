using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Echo.Core.Storage;

/// <summary>
/// Persistent deferred-retry queue for post-meeting processing. A transient API failure
/// (429 rate limit, 5xx, network) used to be a terminal error after ~2 minutes of retries,
/// leaving the meeting without a transcript until the user clicked "Relancer". Now the
/// session is parked here and re-run later in the background: 10 min, then 30, then 60.
/// The queue survives restarts (pending.json) so an app relaunch does not lose the retry.
/// The session shows the "waiting" badge while parked (.echo_status "waiting").
/// </summary>
public sealed class ProcessingQueue
{
    public sealed class Item
    {
        [JsonPropertyName("dir")] public string Dir { get; set; } = "";
        [JsonPropertyName("minutes")] public bool Minutes { get; set; }
        [JsonPropertyName("summarize")] public bool Summarize { get; set; }
        [JsonPropertyName("attempts")] public int Attempts { get; set; }
        [JsonPropertyName("next_at_epoch")] public long NextAtEpoch { get; set; }
        [JsonPropertyName("reason")] public string? Reason { get; set; }
    }

    /// <summary>Wait before attempt 1, 2 and 3 (minutes). After that the session is dropped
    /// from the queue with an explicit notification (audio is kept, manual retry possible).</summary>
    public static readonly int[] DelaysMinutes = { 10, 30, 60 };

    private readonly string _file;
    private readonly object _lock = new();
    private readonly List<Item> _items = new();
    private readonly Action<string> _log;

    public ProcessingQueue(string file, Action<string> log)
    {
        _file = file;
        _log = log;
        Load();
    }

    public int Count { get { lock (_lock) return _items.Count; } }

    public IReadOnlyList<Item> Snapshot() { lock (_lock) return _items.Select(Clone).ToList(); }

    public bool Contains(string dir)
    {
        lock (_lock) return _items.Any(i => SamePath(i.Dir, dir));
    }

    /// <summary>
    /// Park <paramref name="dir"/> for a later retry. Returns the scheduled delay in minutes,
    /// or null when the attempt budget is exhausted (item removed; caller notifies "abandon").
    /// </summary>
    public int? Enqueue(string dir, bool minutes, bool summarize, string? reason, DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.Now;
        lock (_lock)
        {
            var it = _items.FirstOrDefault(i => SamePath(i.Dir, dir));
            int attempts = (it?.Attempts ?? 0) + 1;
            if (attempts > DelaysMinutes.Length)
            {
                if (it is not null) _items.Remove(it);
                SaveLocked();
                _log($"[queue] {Path.GetFileName(dir)}: attempt budget exhausted ({attempts - 1}), dropped.");
                return null;
            }
            int delay = DelaysMinutes[attempts - 1];
            if (it is null) { it = new Item { Dir = dir }; _items.Add(it); }
            it.Minutes = minutes || it.Minutes;
            it.Summarize = summarize || it.Summarize;
            it.Attempts = attempts;
            it.NextAtEpoch = t.AddMinutes(delay).ToUnixTimeSeconds();
            it.Reason = reason;
            SaveLocked();
            _log($"[queue] {Path.GetFileName(dir)}: retry {attempts}/{DelaysMinutes.Length} in {delay} min ({reason}).");
            return delay;
        }
    }

    /// <summary>
    /// Queue <paramref name="dir"/> for the very next pump tick WITHOUT spending a retry: a run
    /// that never got its chance (capture stopped by a quit/logoff, pipeline killed with the
    /// app). An existing item keeps its attempt count.
    /// </summary>
    public void EnqueueNow(string dir, bool minutes, bool summarize, string? reason, DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.Now;
        lock (_lock)
        {
            var it = _items.FirstOrDefault(i => SamePath(i.Dir, dir));
            if (it is null) { it = new Item { Dir = dir }; _items.Add(it); }
            it.Minutes = minutes || it.Minutes;
            it.Summarize = summarize || it.Summarize;
            it.NextAtEpoch = t.ToUnixTimeSeconds();
            it.Reason = reason;
            SaveLocked();
            _log($"[queue] {Path.GetFileName(dir)}: queued for the next pass ({reason}).");
        }
    }

    /// <summary>The queued item for <paramref name="dir"/> (a copy), or null.</summary>
    public Item? Get(string dir)
    {
        lock (_lock)
        {
            var it = _items.FirstOrDefault(i => SamePath(i.Dir, dir));
            return it is null ? null : Clone(it);
        }
    }

    /// <summary>Remove a session from the queue (processing succeeded, or the user deleted it).</summary>
    public void Clear(string dir)
    {
        lock (_lock)
        {
            int n = _items.RemoveAll(i => SamePath(i.Dir, dir));
            if (n > 0) SaveLocked();
        }
    }

    /// <summary>Items whose retry time has come. They stay queued (with their attempt count)
    /// until the run either clears them or re-enqueues them with the next delay.</summary>
    public List<Item> Due(DateTimeOffset? now = null)
    {
        long t = (now ?? DateTimeOffset.Now).ToUnixTimeSeconds();
        lock (_lock) return _items.Where(i => i.NextAtEpoch <= t).Select(Clone).ToList();
    }

    /// <summary>Background pump: every <paramref name="period"/>, run every due item through
    /// <paramref name="run"/> (sequentially: the API is what we are waiting for).</summary>
    public async Task RunLoopAsync(Func<Item, Task> run, TimeSpan period, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(period, ct); } catch (TaskCanceledException) { break; }
            foreach (var it in Due())
            {
                if (ct.IsCancellationRequested) break;
                if (!Directory.Exists(it.Dir)) { Clear(it.Dir); continue; }
                try { await run(it); }
                catch (Exception ex) { _log($"[queue] run failed for {Path.GetFileName(it.Dir)}: {ex.Message}"); }
            }
        }
    }

    // ── persistence ─────────────────────────────────────────────────────────────

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var items = JsonSerializer.Deserialize<List<Item>>(File.ReadAllText(_file));
            if (items is null) return;
            lock (_lock)
            {
                _items.Clear();
                _items.AddRange(items.Where(i => !string.IsNullOrWhiteSpace(i.Dir) && Directory.Exists(i.Dir)));
            }
            if (_items.Count > 0) _log($"[queue] {_items.Count} deferred session(s) reloaded.");
        }
        catch (Exception ex) { _log($"[queue] load failed: {ex.Message}"); }
    }

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception ex) { _log($"[queue] save failed: {ex.Message}"); }
    }

    private static Item Clone(Item i) => new()
    {
        Dir = i.Dir, Minutes = i.Minutes, Summarize = i.Summarize,
        Attempts = i.Attempts, NextAtEpoch = i.NextAtEpoch, Reason = i.Reason,
    };

    private static bool SamePath(string a, string b) => SessionFiles.SamePath(a, b);
}
