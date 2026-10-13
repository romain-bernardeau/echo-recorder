namespace Echo.Ai;

/// <summary>
/// Faithful, self-contained port of the tiny slice of CPython's
/// <c>difflib.SequenceMatcher</c> that Echo relies on: the character-level
/// <c>ratio()</c>. Both the guarded LLM correction pass (<see cref="Postprocess"/>,
/// <c>apply_llm_corrections</c>) and the text-level echo dedup
/// (<c>transcribe.echo_analysis</c>) score similarity with
/// <c>difflib.SequenceMatcher(None, a, b).ratio()</c>; their thresholds only make
/// sense against the SAME metric, so it is ported here rather than approximated.
///
/// Reproduces exactly (for <c>isjunk=None</c>, <c>autojunk=True</c>):
///  - <c>__chain_b</c>: the b2j index over the SECOND sequence, with the autojunk
///    heuristic (when <c>len(b) &gt;= 200</c>, elements occurring more than
///    <c>len(b)//100 + 1</c> times are dropped from the index);
///  - <c>find_longest_match</c>: the j2len dynamic-programming longest run + the
///    non-junk end-extension (the junk-extension loops are no-ops with no junk);
///  - <c>get_matching_blocks</c>: the queue-driven recursive partition;
///  - <c>ratio</c>: <c>2.0 * M / (len(a) + len(b))</c>.
/// </summary>
public static class Difflib
{
    /// <summary>
    /// <c>difflib.SequenceMatcher(None, a, b).ratio()</c> over characters.
    /// Returns 1.0 for two empty strings (mirrors <c>_calculate_ratio(0, 0)</c>).
    /// </summary>
    public static double Ratio(string? a, string? b)
    {
        a ??= "";
        b ??= "";
        int total = a.Length + b.Length;
        if (total == 0) return 1.0;

        var b2j = ChainB(b);
        int matches = 0;

        // get_matching_blocks: queue used as a stack (order-independent for the sum).
        var queue = new Stack<(int alo, int ahi, int blo, int bhi)>();
        queue.Push((0, a.Length, 0, b.Length));
        while (queue.Count > 0)
        {
            var (alo, ahi, blo, bhi) = queue.Pop();
            var (i, j, k) = FindLongestMatch(a, b, b2j, alo, ahi, blo, bhi);
            if (k > 0)
            {
                matches += k;
                if (alo < i && blo < j) queue.Push((alo, i, blo, j));
                if (i + k < ahi && j + k < bhi) queue.Push((i + k, ahi, j + k, bhi));
            }
        }
        return 2.0 * matches / total;
    }

    /// <summary>Port of <c>__chain_b</c> for <c>isjunk=None</c>: b2j over <paramref name="b"/>
    /// with the autojunk purge (only when <c>len(b) &gt;= 200</c>).</summary>
    private static Dictionary<char, List<int>> ChainB(string b)
    {
        var b2j = new Dictionary<char, List<int>>();
        for (int i = 0; i < b.Length; i++)
        {
            if (!b2j.TryGetValue(b[i], out var lst)) b2j[b[i]] = lst = new List<int>();
            lst.Add(i);
        }
        // isjunk is None -> no junk purge. autojunk purge of popular elements:
        int n = b.Length;
        if (n >= 200)
        {
            int ntest = n / 100 + 1;
            var popular = new List<char>();
            foreach (var kv in b2j)
                if (kv.Value.Count > ntest) popular.Add(kv.Key);
            foreach (var elt in popular) b2j.Remove(elt);
        }
        return b2j;
    }

    /// <summary>Port of <c>find_longest_match</c> (isjunk=None: the junk-extension
    /// loops never fire because bjunk is empty, so only the plain extension runs).</summary>
    private static (int Besti, int Bestj, int Bestsize) FindLongestMatch(
        string a, string b, Dictionary<char, List<int>> b2j, int alo, int ahi, int blo, int bhi)
    {
        int besti = alo, bestj = blo, bestsize = 0;
        var j2len = new Dictionary<int, int>();
        for (int i = alo; i < ahi; i++)
        {
            var newj2len = new Dictionary<int, int>();
            if (b2j.TryGetValue(a[i], out var idxs))
            {
                foreach (var j in idxs)
                {
                    if (j < blo) continue;
                    if (j >= bhi) break;
                    int k = (j2len.TryGetValue(j - 1, out var pv) ? pv : 0) + 1;
                    newj2len[j] = k;
                    if (k > bestsize)
                    {
                        besti = i - k + 1;
                        bestj = j - k + 1;
                        bestsize = k;
                    }
                }
            }
            j2len = newj2len;
        }
        while (besti > alo && bestj > blo && a[besti - 1] == b[bestj - 1])
        {
            besti--; bestj--; bestsize++;
        }
        while (besti + bestsize < ahi && bestj + bestsize < bhi && a[besti + bestsize] == b[bestj + bestsize])
        {
            bestsize++;
        }
        return (besti, bestj, bestsize);
    }
}
