using System.Globalization;
using System.Text.RegularExpressions;

namespace Echo.Ai;

/// <summary>
/// Text-level microphone-echo detection, faithful port of
/// <c>transcribe.echo_analysis</c>. Kept as a pure, index-based helper (independent
/// of the transcriber's internal segment type) so the thresholds are unit-testable.
///
/// The acoustic AEC (livekit) is a stub in this port, so — exactly like the Python
/// app when livekit is absent — echo is removed at the text level: a mic-track
/// segment that temporally overlaps a system-track segment AND says (almost) the
/// same thing is the loudspeaker echo captured by the microphone (no-headset case).
///
/// Two criteria run side by side:
///  - the original one (difflib ratio between the mic segment and ONE overlapping remote
///    segment). Its <c>2M/(la+lb)</c> cannot exceed ~0.55 once the mic text is under ~38 %
///    of the remote text, even when it is entirely contained in it: a short echo of a long
///    remote turn, or an echo straddling two remote segments, was never caught;
///  - CONTAINMENT: the share of the mic words found, in order and compactly, in the
///    concatenation of the remote segments overlapping [start-2 s, end+2 s]. Measured on the
///    MIC words, so a long mic segment that only partly repeats the remote (the user talking
///    over an echo) is never removed.
/// </summary>
public static class EchoDedup
{
    /// <summary>Lightweight view of a merged segment for echo analysis.</summary>
    public readonly record struct EchoSeg(string? Origin, double? Start, double? End, string Text);

    /// <summary>Best scores seen for one mic segment (the diagnostic triplet).</summary>
    public readonly record struct EchoScore(int Index, double Overlap, double Ratio, double Containment, bool Removed, string? Rule);

    /// <summary>Detailed outcome: the classic (ratio, indices) pair plus per-mic-segment scores
    /// and counters for a one-line diagnostic.</summary>
    public sealed record EchoReport(double Ratio, HashSet<int> EchoIndices, IReadOnlyList<EchoScore> Scores)
    {
        public int MicSegments => Scores.Count;
        public int RemovedByRatio => Scores.Count(s => s.Removed && s.Rule == "ratio");
        public int RemovedByContainment => Scores.Count(s => s.Removed && s.Rule == "contenance");
        public int RemovedByBoth => Scores.Count(s => s.Removed && s.Rule == "les deux");
        /// <summary>Kept, but close to a threshold (containment 0.4-0.6 or ratio 0.40-0.55):
        /// the segments to look at when tuning.</summary>
        public int KeptBorderline => Scores.Count(s => !s.Removed && (s.Containment >= 0.4 || s.Ratio >= 0.40));

        /// <summary>Compact diagnostic line: counters and the median triplet of the removed segments.</summary>
        public string Summary()
        {
            var removed = Scores.Where(s => s.Removed).ToList();
            string Med(Func<EchoScore, double> f)
            {
                if (removed.Count == 0) return "-";
                var v = removed.Select(f).OrderBy(x => x).ToList();
                return v[v.Count / 2].ToString("0.00", CultureInfo.InvariantCulture);
            }
            return $"{MicSegments} segment(s) micro analysé(s), {EchoIndices.Count} retiré(s) "
                   + $"(ratio {RemovedByRatio}, contenance {RemovedByContainment}, les deux {RemovedByBoth}), "
                   + $"{KeptBorderline} conservé(s) limite(s) ; médianes des retirés : chevauchement {Med(s => s.Overlap)}, "
                   + $"ratio {Med(s => s.Ratio)}, contenance {Med(s => s.Containment)}";
        }
    }

    /// <summary>Containment threshold (share of the mic words found in the remote text).</summary>
    public const double ContainmentThr = 0.6;
    /// <summary>Below this many words a mic segment is never judged by containment ("oui",
    /// "d'accord" are found in any remote turn).</summary>
    public const int ContainmentMinWords = 3;
    /// <summary>Seconds of slack around the mic segment when gathering remote segments.</summary>
    public const double ContainmentSlack = 2.0;

    private static readonly Regex NormWordsRx = new("[^0-9a-zàâäçéèêëîïôöùûüœ ]", RegexOptions.Compiled);

    /// <summary>Lowercase, then keep only digits / basic-latin lowercase / a fixed set
    /// of accented letters / spaces (everything else -&gt; space). Port of
    /// <c>transcribe._norm_words</c>.</summary>
    public static string NormWords(string? t) => NormWordsRx.Replace((t ?? "").ToLowerInvariant(), " ");

    /// <summary>
    /// Return the share of mic-track duration that is echo and the SET OF INDICES
    /// (into <paramref name="merged"/>) of the mic segments to drop. See <see cref="AnalyzeDetailed"/>.
    /// </summary>
    public static (double Ratio, HashSet<int> EchoIndices) Analyze(
        IReadOnlyList<EchoSeg> merged, double simThr = 0.55, double ovFrac = 0.3)
    {
        var r = AnalyzeDetailed(merged, simThr, ovFrac);
        return (r.Ratio, r.EchoIndices);
    }

    /// <summary>
    /// A mic ('local') segment is echo when EITHER it overlaps a non-local segment by more than
    /// <paramref name="ovFrac"/> of the shorter span AND their normalised texts are at least
    /// <paramref name="simThr"/> similar (difflib ratio; port of <c>echo_analysis(0.55, 0.3)</c>),
    /// OR it has at least <see cref="ContainmentMinWords"/> words of which at least
    /// <see cref="ContainmentThr"/> are found, in order and compactly, in the concatenated
    /// remote text around it.
    /// </summary>
    public static EchoReport AnalyzeDetailed(
        IReadOnlyList<EchoSeg> merged, double simThr = 0.55, double ovFrac = 0.3)
    {
        var echoIndices = new HashSet<int>();
        var scores = new List<EchoScore>();
        double echoDur = 0.0, total = 0.0;

        // Pre-collect the remote segments (with valid spans) once, by start time.
        var rems = new List<EchoSeg>();
        foreach (var r in merged)
            if (r.Origin != "local" && r.Start is not null && r.End is not null) rems.Add(r);
        rems.Sort((a, b) => a.Start!.Value.CompareTo(b.Start!.Value));

        for (int i = 0; i < merged.Count; i++)
        {
            var l = merged[i];
            if (l.Origin != "local" || l.Start is null || l.End is null) continue;
            double ls = l.Start.Value, le = l.End.Value;
            double dur = le - ls;
            if (dur <= 0) continue;
            total += dur;

            var micNorm = NormWords(l.Text);
            bool byRatio = false;
            double bestOv = 0.0, bestRatio = 0.0;
            var around = new List<string>();
            foreach (var r in rems)
            {
                double rs = r.Start!.Value, re = r.End!.Value;
                if (re >= ls - ContainmentSlack && rs <= le + ContainmentSlack) around.Add(r.Text);
                double ov = Overlap(ls, le, rs, re);
                if (ov <= 0) continue;
                double rd = re - rs;
                double denom = Math.Min(dur, rd != 0.0 ? rd : dur);   // Python: min(dur, (re-rs) or dur)
                bestOv = Math.Max(bestOv, denom > 0 ? ov / denom : 0.0);
                if (ov > ovFrac * denom)
                {
                    double sim = Difflib.Ratio(micNorm, NormWords(r.Text));
                    bestRatio = Math.Max(bestRatio, sim);
                    if (sim >= simThr) byRatio = true;
                }
            }

            double containment = around.Count == 0 ? 0.0 : Containment(micNorm, NormWords(string.Join(" ", around)));
            bool byContainment = containment >= ContainmentThr;
            bool removed = byRatio || byContainment;
            if (removed)
            {
                echoDur += dur;
                echoIndices.Add(i);
            }
            scores.Add(new EchoScore(i, bestOv, bestRatio, containment, removed,
                byRatio && byContainment ? "les deux" : byRatio ? "ratio" : byContainment ? "contenance" : null));
        }
        return new EchoReport(total > 0.0 ? echoDur / total : 0.0, echoIndices, scores);
    }

    /// <summary>
    /// Share of the mic words matched IN ORDER and COMPACTLY in the remote words: best local
    /// alignment (each match +1, each remote word skipped inside the match −0.34, mic words
    /// may be skipped for free but count as unmatched). A mic interjection whose words are
    /// merely scattered across a long remote turn ("oui … c'est … ça") scores low; a true echo,
    /// possibly with a few words missed by either recogniser, scores high. 0 below
    /// <see cref="ContainmentMinWords"/> mic words.
    /// </summary>
    public static double Containment(string micNorm, string remoteNorm)
    {
        var a = micNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var b = remoteNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (a.Length < ContainmentMinWords || b.Length == 0) return 0.0;
        const double gapB = 0.34;

        // score[j] / matches[j] for the previous and current mic word (rolling rows).
        var prevS = new double[b.Length + 1];
        var prevM = new int[b.Length + 1];
        var curS = new double[b.Length + 1];
        var curM = new int[b.Length + 1];
        double best = 0.0;
        int bestMatches = 0;
        for (int i = 1; i <= a.Length; i++)
        {
            curS[0] = prevS[0];
            curM[0] = prevM[0];
            for (int j = 1; j <= b.Length; j++)
            {
                double s = 0.0; int m = 0;                                   // start fresh (local alignment)
                if (a[i - 1] == b[j - 1] && prevS[j - 1] + 1 > s) { s = prevS[j - 1] + 1; m = prevM[j - 1] + 1; }
                if (prevS[j] > s) { s = prevS[j]; m = prevM[j]; }             // mic word unmatched
                if (curS[j - 1] - gapB > s) { s = curS[j - 1] - gapB; m = curM[j - 1]; } // remote word skipped
                curS[j] = s; curM[j] = m;
                if (s > best || (s == best && m > bestMatches)) { best = s; bestMatches = m; }
            }
            (prevS, curS) = (curS, prevS);
            (prevM, curM) = (curM, prevM);
        }
        return (double)bestMatches / a.Length;
    }

    private static double Overlap(double s, double e, double a, double b) => Math.Max(0.0, Math.Min(e, b) - Math.Max(s, a));
}
