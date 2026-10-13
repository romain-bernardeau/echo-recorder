namespace Echo.Ai;

/// <summary>One lexicon entry: a canonical spelling plus its known deformed variants.</summary>
public sealed class LexEntry
{
    public string Canonical { get; init; } = "";
    public List<string> Variants { get; } = new();
}

/// <summary>
/// Faithful C# port of the Python <c>lexicon.py</c> parser shared by the two
/// lexicon files (<c>context_bias.txt</c> and <c>lexicon_default.txt</c>).
///
/// Extended format (backward-compatible with "one term per line"):
///   - blank / <c>#</c> line          -> ignored
///   - bare term        "SteerCo"     -> canonical only
///   - mapping          "stirco|steer co=>SteerCo" -> canonical + variants
///
/// The canonical side feeds Voxtral biasing (<see cref="BiasTerms"/>); the
/// variant map (<see cref="VariantMap"/>) drives the deterministic correction pass.
/// </summary>
public static class Lexicon
{
    /// <summary>Read the useful lines of a lexicon file (# = comment, blanks skipped).
    /// A missing/unreadable file yields an empty list (never throws to the caller).</summary>
    public static List<string> ReadLines(string? path)
    {
        var outp = new List<string>();
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return outp;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var s = line.Trim();
                if (s.Length == 0 || s.StartsWith('#')) continue;
                outp.Add(s);
            }
        }
        catch
        {
            return new List<string>();
        }
        return outp;
    }

    /// <summary>Parse one already-trimmed line. Returns null when not usable.</summary>
    public static (string Canonical, List<string> Variants)? ParseLine(string? line)
    {
        var s = (line ?? "").Trim();
        if (s.Length == 0 || s.StartsWith('#')) return null;
        var arrow = s.IndexOf("=>", StringComparison.Ordinal);
        if (arrow >= 0)
        {
            var left = s[..arrow];
            var canonical = s[(arrow + 2)..].Trim();
            if (canonical.Length == 0) return null;
            var variants = left.Split('|')
                .Select(v => v.Trim())
                .Where(v => v.Length > 0)
                .ToList();
            return (canonical, variants);
        }
        return (s, new List<string>());
    }

    /// <summary>
    /// Merge entries from several files (order = priority). Case-insensitive dedup
    /// on the canonical; variants of the same canonical accumulate without dupes;
    /// a variant equal to its canonical is dropped. Output order follows first
    /// appearance of each canonical.
    /// </summary>
    public static List<LexEntry> LoadEntries(IEnumerable<string> paths)
    {
        var outp = new List<LexEntry>();
        var index = new Dictionary<string, int>();
        foreach (var path in paths)
        {
            foreach (var raw in ReadLines(path))
            {
                var parsed = ParseLine(raw);
                if (parsed is null) continue;
                var (canonical, variants) = parsed.Value;
                var key = canonical.ToLowerInvariant();
                LexEntry entry;
                if (index.TryGetValue(key, out var pos))
                {
                    entry = outp[pos];
                }
                else
                {
                    entry = new LexEntry { Canonical = canonical };
                    index[key] = outp.Count;
                    outp.Add(entry);
                }
                var seen = new HashSet<string>(entry.Variants.Select(v => v.ToLowerInvariant()));
                foreach (var v in variants)
                {
                    var vl = v.ToLowerInvariant();
                    if (vl.Length > 0 && vl != entry.Canonical.ToLowerInvariant() && seen.Add(vl))
                        entry.Variants.Add(v);
                }
            }
        }
        return outp;
    }

    /// <summary>Canonical spellings only, in order (Voxtral context_bias source).</summary>
    public static List<string> BiasTerms(IEnumerable<LexEntry> entries) =>
        entries.Select(e => e.Canonical).ToList();

    /// <summary>variant_lower -> canonical (deterministic correction). Entries arrive in
    /// PRIORITY order (highest first, see <see cref="LoadEntries"/>), so the FIRST mapping
    /// for a given variant wins: a higher-priority layer (mission-specific &gt; global client
    /// &gt; shipped system) keeps its canonical even when a lower layer reuses the same
    /// variant string for a different canonical.</summary>
    public static Dictionary<string, string> VariantMap(IEnumerable<LexEntry> entries)
    {
        var m = new Dictionary<string, string>();
        foreach (var e in entries)
            foreach (var v in e.Variants)
            {
                var vl = v.ToLowerInvariant();
                if (vl.Length > 0 && !m.ContainsKey(vl)) m[vl] = e.Canonical;
            }
        return m;
    }
}
