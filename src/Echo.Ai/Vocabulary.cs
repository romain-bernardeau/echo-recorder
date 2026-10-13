using System.Text.RegularExpressions;
using Echo.Core.Abstractions;
using Echo.Core.Models;

namespace Echo.Ai;

/// <summary>
/// Domain vocabulary for Voxtral biasing + deterministic canonicalisation.
/// Faithful port of the relevant bits of <c>transcribe.load_context_bias</c>,
/// <c>postprocess.apply_variant_corrections</c> and the <c>lexicon</c> module.
///
/// Two lexicon sources, merged MISSION-first (highest priority):
///  - <c>context_bias.txt</c> : LOCAL, per-installation, user-supplied. Its
///    CONTENTS are never embedded/redistributed; it is only ever read from a path.
///  - <c>lexicon_default.txt</c> : the shipped generic lexicon (no client data),
///    copied next to the assembly at build time.
/// </summary>
public sealed class Vocabulary : IVocabulary
{
    /// <summary>API off-switch env var (parity with Python VOXRA_CONTEXT_BIAS=0).</summary>
    public const string EnvOffSwitch = "VOXRA_CONTEXT_BIAS";
    /// <summary>Optional override for the local mission/client bias file path.</summary>
    public const string EnvMissionFile = "ECHO_CONTEXT_BIAS_FILE";
    /// <summary>Optional override for the per-mission vocab DIRECTORY (injectable for tests);
    /// defaults to <c>%APPDATA%\Echo\vocab</c>.</summary>
    public const string EnvVocabDir = "ECHO_VOCAB_DIR";
    private const int MaxBias = 100;

    private readonly string _defaultLexiconPath;
    private readonly string _baseDir;
    private readonly bool _missionBiasFixed;
    private string _missionBiasPath;
    private readonly Func<string, string?> _env;

    /// <summary>Per-mission (per-flag) file layered ON TOP of the global + system lexicons for
    /// a SESSION-SCOPED instance (see <see cref="ForSession"/>); null for the shared singleton.</summary>
    private readonly string? _sessionMissionPath;

    private List<LexEntry>? _entries;
    private List<(Regex Rx, string Canonical)>? _patterns;

    /// <param name="defaultLexiconPath">Shipped generic lexicon. Defaults to
    /// <c>lexicon_default.txt</c> next to the assembly.</param>
    /// <param name="missionBiasPath">Local mission/client bias file. Defaults to the
    /// <see cref="EnvMissionFile"/> env var, else <c>context_bias.txt</c> next to the assembly.
    /// Its contents are NEVER copied into source, tests or logs.</param>
    /// <param name="env">Environment-variable reader (injectable for tests).</param>
    public Vocabulary(string? defaultLexiconPath = null, string? missionBiasPath = null,
        Func<string, string?>? env = null)
    {
        _env = env ?? Environment.GetEnvironmentVariable;
        var baseDir = AppContext.BaseDirectory;
        _baseDir = baseDir;
        _defaultLexiconPath = defaultLexiconPath ?? Path.Combine(baseDir, "Resources", "lexicon_default.txt");
        if (!File.Exists(_defaultLexiconPath) && defaultLexiconPath is null)
        {
            // Fallback to a flat copy beside the exe if the Resources subfolder isn't used.
            var flat = Path.Combine(baseDir, "lexicon_default.txt");
            if (File.Exists(flat)) _defaultLexiconPath = flat;
        }
        // A caller-supplied path is honoured verbatim and never re-resolved (tests);
        // otherwise the mission file is re-resolved on Reload so a freshly created
        // %APPDATA%\Echo\context_bias.txt is picked up without an app restart.
        _missionBiasFixed = missionBiasPath is not null;
        _missionBiasPath = missionBiasPath ?? ResolveMissionBiasPath(baseDir);
    }

    /// <summary>
    /// Resolve the LOCAL mission/client bias file (audit finding #7). Priority:
    /// <list type="number">
    ///   <item><see cref="EnvMissionFile"/> env var (explicit override);</item>
    ///   <item><c>%APPDATA%\Echo\context_bias.txt</c> when it exists — the natural
    ///   home for a Python migrant's per-mission jargon, alongside settings.json;</item>
    ///   <item><c>context_bias.txt</c> next to the assembly (backward-compatible fallback,
    ///   also the path used when nothing is present so behaviour is unchanged).</item>
    /// </list>
    /// Its CONTENTS are never embedded/redistributed; only the path is ever read.
    /// </summary>
    private string ResolveMissionBiasPath(string baseDir)
    {
        if (_env(EnvMissionFile) is { Length: > 0 } p) return p;
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
            {
                var appDataBias = Path.Combine(appData, "Echo", "context_bias.txt");
                if (File.Exists(appDataBias)) return appDataBias;
            }
        }
        catch { /* fall back to the assembly-relative path */ }
        return Path.Combine(baseDir, "context_bias.txt");
    }

    /// <summary>Absolute path of the LOCAL mission/client bias file this instance reads
    /// (<c>%APPDATA%\Echo\context_bias.txt</c> unless overridden). Lets the bridge expose
    /// exactly the file that feeds this running Vocabulary.</summary>
    public string MissionBiasPath => _missionBiasPath;

    /// <summary>
    /// Drop the lazily-cached lexicon so the next <see cref="Entries"/>/<see cref="Patterns"/>
    /// access re-reads the files from disk. Call after the mission bias file is edited or
    /// imported so deterministic corrections apply without an app restart.
    /// <see cref="BuildContextBias"/> already reads the files fresh each call.
    /// </summary>
    public void Reload()
    {
        _entries = null;
        _patterns = null;
        if (!_missionBiasFixed) _missionBiasPath = ResolveMissionBiasPath(_baseDir);
    }

    // ── per-mission (per-flag) layering ────────────────────────────────────────

    /// <summary>Absolute path of the PER-MISSION vocab file layered on top for this
    /// session-scoped instance (null for the shared singleton). Diagnostic only.</summary>
    public string? SessionMissionPath => _sessionMissionPath;

    /// <summary>Private ctor for a SESSION-SCOPED clone: reuses <paramref name="baseVocab"/>'s
    /// default + global paths and env, and adds <paramref name="sessionMissionPath"/> as the
    /// HIGHEST-priority lexicon source (the client's per-mission jargon, IN ADDITION to the
    /// global + system layers).</summary>
    private Vocabulary(Vocabulary baseVocab, string sessionMissionPath)
    {
        _env = baseVocab._env;
        _baseDir = baseVocab._baseDir;
        _defaultLexiconPath = baseVocab._defaultLexiconPath;
        _missionBiasFixed = baseVocab._missionBiasFixed;
        _missionBiasPath = baseVocab._missionBiasPath;
        _sessionMissionPath = sessionMissionPath;
    }

    /// <summary>
    /// Build a SESSION-SCOPED vocabulary for <paramref name="sessionDir"/>. When the session
    /// is linked to a mission (<c>&lt;sessionDir&gt;\flag.txt</c>) whose per-mission file
    /// (<c>%APPDATA%\Echo\vocab\&lt;id&gt;.txt</c>) exists, that file becomes the
    /// HIGHEST-priority lexicon source, layered IN ADDITION to this instance's global +
    /// system layers (mission-specific &gt; global context_bias &gt; system lexicon_default).
    /// Otherwise this shared instance is returned unchanged. Never throws.
    /// </summary>
    public Vocabulary ForSession(string? sessionDir)
    {
        try
        {
            var missionPath = ResolveMissionVocabPath(sessionDir, _env);
            return missionPath is null ? this : new Vocabulary(this, missionPath);
        }
        catch { return this; }
    }

    /// <summary>Directory holding per-mission vocab files: <see cref="EnvVocabDir"/> override,
    /// else <c>%APPDATA%\Echo\vocab</c> (alongside settings.json + context_bias.txt).</summary>
    internal static string ResolveVocabDir(Func<string, string?> env)
    {
        if (env(EnvVocabDir) is { Length: > 0 } d) return d;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "Echo", "vocab");
    }

    /// <summary>Resolve a session's per-mission vocab file: read the mission id from
    /// <c>&lt;sessionDir&gt;\flag.txt</c> and return <c>&lt;vocabDir&gt;\&lt;id&gt;.txt</c> IF it
    /// exists. Null when there is no session dir, no flag file, an empty/unsafe id, or no
    /// matching file. The id must be a bare filename fragment (path-traversal guarded).</summary>
    internal static string? ResolveMissionVocabPath(string? sessionDir, Func<string, string?> env)
    {
        if (string.IsNullOrWhiteSpace(sessionDir)) return null;
        string id;
        try
        {
            var flagFile = Path.Combine(sessionDir!, "flag.txt");
            if (!File.Exists(flagFile)) return null;
            id = File.ReadAllText(flagFile).Trim();
        }
        catch { return null; }
        if (id.Length == 0 || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        var path = Path.Combine(ResolveVocabDir(env), id + ".txt");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Lexicon file paths in PRIORITY order (highest first): the session's
    /// per-mission file (session-scoped instances only), then the global client bias file,
    /// then the shipped system lexicon.</summary>
    private string[] LexiconPaths()
        => _sessionMissionPath is null
            ? new[] { _missionBiasPath, _defaultLexiconPath }
            : new[] { _sessionMissionPath, _missionBiasPath, _defaultLexiconPath };

    // ── IVocabulary ──────────────────────────────────────────────────────────

    public IReadOnlyList<string> BuildContextBias(AppSettings settings, string? missionHint = null)
        => BuildContextBias(settings, Array.Empty<string>(), missionHint);

    public string CanonicalizeDeterministic(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var result = text;
        // Literal replacement (MatchEvaluator) mirrors Python's rx.sub(lambda: canonical):
        // a canonical containing '$' must NOT be treated as a substitution reference.
        foreach (var (rx, canonical) in Patterns())
            result = rx.Replace(result, _ => canonical);
        return result;
    }

    /// <summary>A single deterministic correction, for the postprocess journal.</summary>
    public sealed record DetCorrection(int SegmentId, string Before, string After, string Term);

    /// <summary>
    /// Deterministic pass over many segments: replace each known variant by its
    /// canonical form, returning the corrected texts AND a per-term journal (one
    /// entry per term actually replaced in a segment). Faithful port of
    /// <c>postprocess.apply_variant_corrections</c>.
    /// </summary>
    public (List<string> Corrected, List<DetCorrection> Journal) ApplyVariantCorrections(IReadOnlyList<string> segments)
    {
        var patterns = Patterns();
        var outp = new List<string>(segments.Count);
        var journal = new List<DetCorrection>();
        for (int idx = 0; idx < segments.Count; idx++)
        {
            var text = segments[idx] ?? "";
            var @new = text;
            foreach (var (rx, canonical) in patterns)
            {
                var replaced = rx.Replace(@new, _ => canonical);
                if (!string.Equals(replaced, @new, StringComparison.Ordinal))
                {
                    journal.Add(new DetCorrection(idx, @new, replaced, canonical));
                    @new = replaced;
                }
            }
            outp.Add(@new);
        }
        return (outp, journal);
    }

    // ── Richer, attendee-aware overload used by the transcriber ────────────────

    /// <summary>
    /// Build the Voxtral <c>context_bias</c> list in priority order
    /// (missionHint, mission file, attendees, generic lexicon), normalised to
    /// single tokens matching <c>^[^,\s]+$</c>, dropped below 3 chars, deduped
    /// case-insensitively (first-seen wins) and capped at 100 entries.
    /// </summary>
    public IReadOnlyList<string> BuildContextBias(AppSettings settings, IReadOnlyList<string> attendees, string? missionHint)
    {
        if (_env(EnvOffSwitch) == "0") return Array.Empty<string>();

        var sessionMission = _sessionMissionPath is null
            ? (IReadOnlyList<string>)Array.Empty<string>()
            : Lexicon.BiasTerms(Lexicon.LoadEntries(new[] { _sessionMissionPath }));
        var mission = Lexicon.BiasTerms(Lexicon.LoadEntries(new[] { _missionBiasPath }));
        var generic = Lexicon.BiasTerms(Lexicon.LoadEntries(new[] { _defaultLexiconPath }));

        var raw = new List<string>();
        if (!string.IsNullOrWhiteSpace(missionHint)) raw.Add(missionHint!);
        raw.AddRange(sessionMission);                       // per-mission client layer (highest client priority)
        raw.AddRange(mission);                              // global client layer (all missions)
        raw.AddRange(attendees ?? Array.Empty<string>());
        raw.AddRange(generic);                              // shipped system layer

        return Normalize(raw);
    }

    /// <summary>
    /// Prepend this SESSION-SCOPED instance's PER-MISSION bias terms (highest priority) to a
    /// caller-supplied base bias, re-normalising (dedup keeps the mission spelling, cap 100).
    /// Returns <paramref name="baseBias"/> unchanged when this instance has no per-mission
    /// file (the shared singleton), so non-mission sessions are untouched. Lets the caller
    /// (e.g. the SessionManager) pre-build the generic bias while the mission terms are still
    /// layered on top inside <c>Echo.Ai</c>.
    /// </summary>
    public IReadOnlyList<string> LayerMissionBias(IReadOnlyList<string> baseBias)
    {
        if (_sessionMissionPath is null) return baseBias;
        if (_env(EnvOffSwitch) == "0") return Array.Empty<string>();
        var missionTerms = Lexicon.BiasTerms(Lexicon.LoadEntries(new[] { _sessionMissionPath }));
        if (missionTerms.Count == 0) return baseBias;
        return Normalize(missionTerms.Concat(baseBias ?? (IReadOnlyList<string>)Array.Empty<string>()));
    }

    /// <summary>
    /// Bias for one SESSION: the per-mission terms (session-scoped instance), then the meeting's
    /// participants, then the caller-supplied base list. The SessionManager pre-builds a base
    /// bias without any name, so the roster used to never reach the engine; names are now
    /// layered here whatever the caller passed. Same normalisation (dedup, cap 100); at most
    /// <paramref name="maxAttendees"/> names so a 150-person town hall does not evict the jargon.
    /// </summary>
    public IReadOnlyList<string> LayerSessionBias(IReadOnlyList<string> baseBias, IReadOnlyList<string>? attendees, int maxAttendees = 40)
    {
        if (_env(EnvOffSwitch) == "0") return Array.Empty<string>();
        var missionTerms = _sessionMissionPath is null
            ? (IReadOnlyList<string>)Array.Empty<string>()
            : Lexicon.BiasTerms(Lexicon.LoadEntries(new[] { _sessionMissionPath }));
        var names = (attendees ?? Array.Empty<string>())
            .Where(a => !string.IsNullOrWhiteSpace(a)).Take(maxAttendees);
        return Normalize(missionTerms.Concat(names).Concat(baseBias ?? (IReadOnlyList<string>)Array.Empty<string>()));
    }

    /// <summary>Back from the Voxtral single-token form to natural text for the engines that
    /// read the bias as a PROMPT (Whisper initial_prompt, Gemini): "Claire_MOREAU" -&gt; "Claire MOREAU".</summary>
    public static string ToPromptTerm(string? term)
        => Regex.Replace((term ?? "").Replace('_', ' '), @"\s+", " ").Trim();

    /// <summary>Normalise raw terms into API-safe single tokens (see class summary).</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string> rawTerms)
    {
        var seen = new HashSet<string>();
        var outp = new List<string>();
        foreach (var t in rawTerms)
        {
            var term = (t ?? "").Trim().Trim(',').Trim();
            if (term.Length == 0) continue;
            // internal whitespace/commas -> '_' so the whole concept stays ONE token
            var entry = Regex.Replace(term, @"[,\s]+", "_");
            if (entry.Length < 3) continue;
            var k = entry.ToLowerInvariant();
            if (seen.Add(k)) outp.Add(entry);
        }
        return outp.Count > MaxBias ? outp.GetRange(0, MaxBias) : outp;
    }

    // ── Shared lexicon access (used by minutes glossary + postprocess) ─────────

    /// <summary>Merged lexicon entries in PRIORITY order (highest first): per-mission file
    /// (session-scoped only), then global client bias, then shipped system lexicon.</summary>
    public List<LexEntry> Entries()
        => _entries ??= Lexicon.LoadEntries(LexiconPaths());

    /// <summary>Canonical glossary spellings (deduped) for the minutes prompt.</summary>
    public List<string> GlossaryTerms(int limit = 150)
    {
        var seen = new HashSet<string>();
        var outp = new List<string>();
        foreach (var e in Entries())
        {
            var t = e.Canonical.Trim();
            if (t.Length == 0) continue;
            if (seen.Add(t.ToLowerInvariant())) outp.Add(t);
            if (outp.Count >= limit) break;
        }
        return outp;
    }

    private List<(Regex, string)> Patterns()
    {
        if (_patterns is not null) return _patterns;
        var vmap = Lexicon.VariantMap(Entries());
        var patterns = new List<(Regex, string)>();
        // longer variants first so a multi-word variant is replaced before a fragment of it
        foreach (var variant in vmap.Keys.OrderByDescending(v => v.Length))
        {
            var rx = VariantRegex(variant);
            if (rx is not null) patterns.Add((rx, vmap[variant]));
        }
        return _patterns = patterns;
    }

    /// <summary>Case-insensitive, word-boundary-bounded regex; multi-word variants
    /// tolerate variable internal whitespace ("steer  co" == "steer co").</summary>
    private static Regex? VariantRegex(string variant)
    {
        var tokens = (variant ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return null;
        var body = string.Join(@"\s+", tokens.Select(Regex.Escape));
        return new Regex(@"\b" + body + @"\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
