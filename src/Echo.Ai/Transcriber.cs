using System.Globalization;
using System.Text;
using System.Text.Json;
using Echo.Core;
using Echo.Core.Abstractions;
using Echo.Core.Models;
using Echo.Core.Storage;

namespace Echo.Ai;

/// <summary>
/// "Relancer la transcription" on a session whose audio (mic/system tracks AND the shareable
/// mixdown) has been deleted by the retention policy. Not a pipeline failure: the transcript,
/// CR and summary already on disk stay valid, so the orchestrator should report it without
/// flagging the session in error. Derives from <see cref="InvalidOperationException"/> so the
/// existing catch sites keep working.
/// </summary>
public sealed class AudioPurgedException : InvalidOperationException
{
    /// <summary>Stable code for the orchestrator / UI (status file, telemetry).</summary>
    public const string ErrorCode = "audio_purged";

    public AudioPurgedException()
        : base("L'audio de cette réunion a été supprimé par la rétention, impossible de la retranscrire.") { }

    public string Code => ErrorCode;

    /// <summary>French, user-facing (same text as <see cref="Exception.Message"/>).</summary>
    public string UserMessage => Message;
}

/// <summary>
/// Transcribes a session's tracks with Voxtral and merges Teams diarisation
/// (port of <c>transcribe.py</c>). Each track (mic = local, system = remote) is
/// transcribed SEPARATELY; segments are placed on a shared timeline, attributed
/// per-segment via the Teams UIA timeline (speakers.jsonl) with track-origin and
/// Voxtral speaker-id fallbacks, then written raw and processed.
/// </summary>
public sealed class Transcriber : ITranscriber
{
    public const string DefaultVoxtralModel = "voxtral-mini-2602";
    private const string EnvVoxtralModel = "VOXTRAL_MODEL";

    /// <summary>Safety-net threshold: share of mic-track duration duplicating the
    /// system track above which we note that echo was detected without AEC
    /// (parity with transcribe.ECHO_TEXT_THR).</summary>
    private const double EchoTextThr = 0.40;

    /// <summary>Unattributed speech (Whisper: no diarisation, no Teams names) is cut into a new
    /// turn after a pause this long, instead of one "Unknown" block for the whole meeting.</summary>
    internal const double PauseTurnSeconds = 1.5;

    /// <summary>Per-track audio preparation (ffmpeg resample + silence trim). A seam for the
    /// tests, which have no ffmpeg and no real speech.</summary>
    internal Func<string, string, string, bool, CancellationToken, TrackPrep> PrepareTrackFn { get; init; }
        = (wav, dir, tag, trim, ct) => AudioPrep.PrepareTrack(wav, dir, tag, ct, trim);

    /// <summary>Piece splitter for chunked backends (Whisper). Seam for the tests.</summary>
    internal Func<string, int, List<(string Path, double StartSeconds)>> SplitWavFn { get; init; }
        = (wav, seconds) => AudioPrep.SplitWav(wav, seconds);

    private readonly ITranscribeClient _transcribe;
    private readonly ITranscribeClient? _geminiTranscribe;
    private readonly ITranscribeClient? _cloudflareTranscribe;
    private readonly IChatClient _chat;
    private readonly IChatClient? _geminiChat;
    private readonly IChatClient? _cloudflareChat;
    private readonly IChatClient? _openRouterChat;
    private readonly Vocabulary _vocab;
    private readonly Func<string, string?> _env;

    /// <summary><paramref name="client"/> = default (Mistral/Voxtral) transcription backend,
    /// <paramref name="chat"/> = default (Mistral) chat backend for the LLM postprocess.
    /// The optional Gemini / Cloudflare pairs are used when the session's settings select
    /// that provider for transcription / CR.</summary>
    public Transcriber(ITranscribeClient? client = null, IChatClient? chat = null,
        ITranscribeClient? geminiTranscribe = null, IChatClient? geminiChat = null,
        Vocabulary? vocab = null, Func<string, string?>? env = null,
        ITranscribeClient? cloudflareTranscribe = null, IChatClient? cloudflareChat = null,
        IChatClient? openRouterChat = null)
    {
        _transcribe = client ?? new MistralClient();
        _chat = chat ?? (_transcribe as IChatClient) ?? new MistralClient();
        _geminiTranscribe = geminiTranscribe;
        _geminiChat = geminiChat;
        _cloudflareTranscribe = cloudflareTranscribe;
        _cloudflareChat = cloudflareChat;
        _openRouterChat = openRouterChat;
        _vocab = vocab ?? new Vocabulary();
        _env = env ?? Environment.GetEnvironmentVariable;
    }

    /// <summary>Transcription backend + model for the session's provider. Falls back to the
    /// Mistral client when the selected provider has no client wired (tests, partial setups).</summary>
    private (ITranscribeClient Client, string Model, string Provider) ResolveTranscribe(AppSettings s)
    {
        var p = LlmProvider.Normalize(s.TranscriptionProvider);
        if (p == LlmProvider.Gemini && _geminiTranscribe is not null)
            return (_geminiTranscribe, LlmProvider.NonEmpty(s.GeminiTranscribeModel, EchoConstants.DefaultGeminiTranscribeModel), p);
        if (p == LlmProvider.Cloudflare && _cloudflareTranscribe is not null)
            return (_cloudflareTranscribe, LlmProvider.NonEmpty(s.CloudflareTranscribeModel, EchoConstants.DefaultCloudflareTranscribeModel), p);
        var model = _env(EnvVoxtralModel) is { Length: > 0 } m ? m : LlmProvider.NonEmpty(s.MistralTranscribeModel, DefaultVoxtralModel);
        return (_transcribe, model, LlmProvider.Mistral);
    }

    /// <summary>Chat backend + model for the LLM postprocess (follows the CR provider).</summary>
    private (IChatClient Client, string Model) ResolvePostprocessChat(AppSettings s)
    {
        var p = LlmProvider.Normalize(s.CrProvider);
        if (p == LlmProvider.Gemini && _geminiChat is not null)
            return (_geminiChat, LlmProvider.NonEmpty(s.GeminiCrModel, EchoConstants.DefaultGeminiCrModel));
        if (p == LlmProvider.Cloudflare && _cloudflareChat is not null)
            return (_cloudflareChat, LlmProvider.NonEmpty(s.CloudflareCrModel, EchoConstants.DefaultCloudflareCrModel));
        if (p == LlmProvider.OpenRouter && _openRouterChat is not null)
            return (_openRouterChat, LlmProvider.NonEmpty(s.OpenRouterCrModel, EchoConstants.DefaultOpenRouterCrModel));
        return (_chat, string.IsNullOrWhiteSpace(s.PostprocessModel) ? Postprocess.DefaultModel : s.PostprocessModel);
    }

    /// <summary>A working segment on the shared timeline (mutable during the pipeline).</summary>
    private sealed class Seg
    {
        public double? Start;
        public double? End;
        public string Text = "";
        public string? VoxSpeaker;
        public string? Origin;   // "local" | "remote"
        public string? Speaker;  // attributed name
        public Seg Clone() => new()
        {
            Start = Start, End = End, Text = Text, VoxSpeaker = VoxSpeaker, Origin = Origin, Speaker = Speaker,
        };
    }

    private sealed record Track(string Tag, string Wav, string Label, double T0Epoch);
    private sealed record Interval(string Name, double Start, double End);

    public async Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request, IProgress<string>? log, CancellationToken ct)
    {
        var sd = request.SessionDir;
        if (!Directory.Exists(sd)) throw new DirectoryNotFoundException($"Session folder not found: {sd}");

        // Session-scoped vocabulary: layers this session's per-mission file (resolved from
        // <sessionDir>\flag.txt) ON TOP of the shared global + system lexicons. Falls back to
        // the shared instance when the session is not linked to a mission with its own vocab.
        var vocab = _vocab.ForSession(sd);

        var meeting = LoadJson(SessionLayout.Find(sd, SessionLayout.Meeting));
        var sessionBase = new DirectoryInfo(sd.TrimEnd('\\', '/')).Name;
        var meetingName = GetString(meeting, "name") ?? sessionBase;
        var dateStr = GetString(meeting, "date_str") ?? "";
        bool physical = GetBool(meeting, "physical");

        var roster = GetStringList(meeting, "roster");
        var attendees = roster.Concat(SpeakersSeen(sd)).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();

        // A caller-supplied bias (SessionManager pre-builds the generic global+system one) is
        // honoured, but the per-mission terms AND the meeting's participants are always layered
        // on top (the pre-built bias carries no name, so the roster never reached the engine);
        // with no caller bias we build the full list, names included.
        var contextBias = request.ContextBias is { Count: > 0 }
            ? vocab.LayerSessionBias(request.ContextBias, attendees)
            : vocab.BuildContextBias(request.Settings, attendees, meetingName);

        var startEpoch = AudioStartEpoch(sd);
        var intervals = BuildIntervals(sd, startEpoch);

        var tracks = PresentTracks(sd);
        if (physical)
        {
            var micOnly = tracks.Where(t => t.Tag == "mic").ToList();
            if (micOnly.Count > 0 && micOnly.Count != tracks.Count)
            {
                log?.Report("  Physical meeting: 'system' (loopback) track ignored.");
                tracks = micOnly;
            }
        }
        bool fromMixdown = false;
        if (tracks.Count == 0)
        {
            // The retention policy deletes the per-track audio but may keep the shareable
            // mixdown: re-transcribe from it as ONE remote track (no "Vous" split, speakers come
            // from the Teams timeline or the engine's diarisation).
            var mix = SessionLayout.FindAudio(sd);
            if (mix is null)
            {
                if (AudioWasRecorded(sd)) throw new AudioPurgedException();
                throw new InvalidOperationException("No usable audio track.");
            }
            log?.Report($"  Pistes micro/système absentes (rétention) : transcription depuis le mixage « {Path.GetFileName(mix)} » "
                        + "comme piste unique distante (diarisation).");
            tracks = new List<Track> { new("system", mix, "remote", startEpoch ?? 0.0) };
            fromMixdown = true;
        }

        // Provider selection for transcription: Voxtral (default), Gemini flash or Cloudflare Whisper.
        var (transcribeClient, model, providerUsed) = ResolveTranscribe(request.Settings);
        if (providerUsed != LlmProvider.Mistral)
            log?.Report($"  Moteur de transcription : {LlmProvider.DisplayName(providerUsed)} ({model}).");
        if (contextBias.Count > 0)
            log?.Report($"  Vocabulaire : {contextBias.Count} terme(s) de biasing envoyés à {LlmProvider.DisplayName(providerUsed)}"
                + (vocab.SessionMissionPath is not null ? " (vocabulaire mission inclus)" : "")
                + (attendees.Count > 0 ? $" (dont {Math.Min(attendees.Count, 40)} participant(s))" : "") + ".");
        bool useSplit = tracks.Count == 2;

        // Human-shareable MP3 mixdown of the present tracks (parity with transcribe.py
        // make_share_mp3). No effect on the per-track transcription; ffmpeg-absent is a
        // logged skip, never a crash. Deliberate v2 divergence: NOT regenerated when it
        // already exists — the tracks never change after the meeting, and on a
        // re-transcription from archived MP3s a re-mix would only add generation loss.
        var shareMp3 = SessionLayout.AudioPath(sd);
        if (!fromMixdown && !File.Exists(shareMp3))
            AudioPrep.MakeShareMp3(tracks.Select(t => t.Wav).ToList(), shareMp3, log, ct);

        List<Seg> merged;
        var raw = new Dictionary<string, string>();
        var infos = new Dictionary<string, object>();
        List<Seg> attributed;

        if (useSplit)
        {
            merged = await TranscribeTracks(transcribeClient, sd, request.ApiKey, tracks, model, contextBias, trim: true, startEpoch, raw, infos, log, ct);
            log?.Report($"  {merged.Count} merged segments (across {tracks.Count} track(s)).");

            // Text-level echo dedup: drop mic-track segments that duplicate system-track
            // content (loudspeaker echo picked up by the microphone, no-headset case).
            // The acoustic AEC (livekit) is a stub, so — exactly like Python when livekit
            // is absent — we always fall back to text dedup, at the merge stage.
            var echo = EchoAnalysis(merged);
            var echoIdx = echo.EchoIndices;
            if (echoIdx.Count > 0)
            {
                merged = merged.Where((s, i) => !echoIdx.Contains(i)).ToList();
                log?.Report($"  Echo (text): {echoIdx.Count} mic segment(s) removed ({echo.Ratio:P0}).");
            }
            // Compact diagnostic (counters + median triplet), never one line per segment.
            if (echo.MicSegments > 0) log?.Report("  Echo (diagnostic) : " + echo.Summary() + ".");
            infos["echo_dedup"] = new
            {
                mic_segments = echo.MicSegments, removed = echoIdx.Count,
                by_ratio = echo.RemovedByRatio, by_containment = echo.RemovedByContainment, by_both = echo.RemovedByBoth,
                kept_borderline = echo.KeptBorderline,
                // Best (overlap, ratio, containment) triplet of every removed or borderline mic
                // segment, as compact arrays: the material to tune the thresholds on real meetings.
                scores = echo.Scores
                    .Where(x => x.Removed || x.Containment >= 0.4 || x.Ratio >= 0.40)
                    .Select(x => new object[] { x.Index, Math.Round(x.Overlap, 2), Math.Round(x.Ratio, 2), Math.Round(x.Containment, 2), x.Removed }),
            };
            if (echo.Ratio >= EchoTextThr)
                log?.Report("  Note: echo detected without AEC; install 'livekit' for clean suppression.");

            var localName = DetectLocalName(merged.Where(s => s.Origin == "local").ToList(), intervals);
            if (localName is not null) log?.Report($"  Local speaker identified: {localName}");
            attributed = AttributeSplit(merged, intervals, localName);
        }
        else
        {
            // Single-track (imported file, physical / mic-only, mixdown): one transcription.
            // Teams names when the UIA timeline has them, else the engine's diarisation
            // ("Participant 1..N"); before, an import without speakers.jsonl came out as ONE
            // "Unknown" block although Voxtral had told 4 speakers apart.
            merged = await TranscribeTracks(transcribeClient, sd, request.ApiKey, tracks, model, contextBias, trim: true, startEpoch, raw, infos, log, ct);
            foreach (var s in merged) s.Origin ??= "remote";
            attributed = AttributeSingle(merged, intervals);
            int diarised = attributed.Where(s => s.Speaker is not null).Select(s => s.Speaker).Distinct().Count();
            if (intervals.Count == 0)
                log?.Report(diarised > 0
                    ? $"  Pas de noms Teams : {diarised} locuteur(s) distingué(s) par la diarisation (Participant 1..{diarised})."
                    : "  Pas de noms Teams ni de diarisation : tours de parole découpés sur les pauses.");
        }

        int named = attributed.Count(s => !string.IsNullOrEmpty(s.Speaker));
        log?.Report($"  Attribution: {named}/{attributed.Count} segments named ({intervals.Count} intervals).");

        // Raw outputs are written BEFORE any correction (faithful reflection of Voxtral).
        var rawPath = WriteRaw(sd, merged, raw, infos);

        // Post-transcription correction (only affects the Processed Transcript):
        // deterministic lexicon pass (always) then optional guarded LLM pass
        // (postprocess.py). Uses the SESSION-SCOPED vocab so per-mission terms correct too.
        // Robust: never throws to break the pipeline.
        await ApplyPostprocess(sd, request, vocab, attributed, meetingName, attendees, log, ct);

        var header = $"{meetingName}\n{dateStr}\nParticipants: {(attendees.Count > 0 ? string.Join(", ", attendees) : "n/a")}";
        var (processedPath, turns) = WriteProcessed(sd, attributed, header);

        // Disk housekeeping, only on FULL SUCCESS (transcript written): drop the
        // regenerable _16k intermediates, and unless the user opted to keep the raw
        // WAVs, archive each track to a verified 16 kHz mono MP3 (~-95 % disk).
        // Never throws — a cleanup hiccup must not fail a finished transcription.
        try
        {
            void Report(string m) => log?.Report(m);
            AudioArchive.CleanupIntermediates(sd, Report);
            // CleanupIntermediates keeps the system_16k* files when no system.wav/mp3 exists
            // (they could be the last audio); from the mixdown they are pure regenerable copies.
            if (fromMixdown)
                foreach (var td in SessionLayout.TrackDirs(sd))
                    foreach (var suffix in new[] { "_16k.wav", "_16k_trim.wav", "_16k.mp3", "_16k_trim.mp3" })
                        AudioArchive.TryDelete(Path.Combine(td, "system" + suffix));
            if (!request.Settings.KeepOriginalWav)
                foreach (var tk in tracks.Where(t => t.Wav.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)))
                    AudioArchive.ArchiveTrackToMp3(tk.Wav, Report, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            log?.Report($"  (archivage audio ignoré : {e.Message})");
        }

        return new TranscriptionResult(rawPath, processedPath, turns);
    }

    // ── track discovery ────────────────────────────────────────────────────────

    private List<Track> PresentTracks(string sd)
    {
        var outp = new List<Track>();
        foreach (var (tag, label) in new[] { ("mic", "local"), ("system", "remote") })
        {
            // {tag}.wav, or the {tag}.mp3 archive once the WAV has been compressed —
            // ffmpeg (which does all the prep work) decodes MP3 natively.
            var src = AudioArchive.FindTrackSource(sd, tag);
            var meta = LoadJson(SessionLayout.Find(sd, $"{tag}.json"));
            if (src is not null
                && GetBool(meta, "got_audio") && GetDouble(meta, "t0_wall_epoch") is { } t0)
            {
                outp.Add(new Track(tag, src, label, t0));
            }
        }
        return outp;
    }

    /// <summary>True when a track sidecar says audio was captured: no source file then means
    /// it was deleted afterwards (retention), not that the meeting was silent.</summary>
    private static bool AudioWasRecorded(string sd)
        => new[] { "mic.json", "system.json" }.Any(n => GetBool(LoadJson(SessionLayout.Find(sd, n)), "got_audio"));

    private double? AudioStartEpoch(string sd)
    {
        var t0s = new List<double>();
        foreach (var n in new[] { "mic.json", "system.json" })
        {
            var m = LoadJson(SessionLayout.Find(sd, n));
            if (GetBool(m, "got_audio") && GetDouble(m, "t0_wall_epoch") is { } t0) t0s.Add(t0);
        }
        if (t0s.Count > 0) return t0s.Min();
        var s = LoadJson(SessionLayout.Find(sd, SessionLayout.Session));
        return GetDouble(s, "session_wall0_epoch");
    }

    // ── per-track transcription ─────────────────────────────────────────────────

    private async Task<List<Seg>> TranscribeTracks(
        ITranscribeClient client, string sd, string apiKey, IReadOnlyList<Track> tracks, string model, IReadOnlyList<string> contextBias,
        bool trim, double? startEpoch, Dictionary<string, string> raw, Dictionary<string, object> infos,
        IProgress<string>? log, CancellationToken ct)
    {
        var all = new List<Seg>();
        foreach (var tk in tracks)
        {
            double offset = startEpoch is { } se ? tk.T0Epoch - se : 0.0;
            var prep = PrepareTrackFn(tk.Wav, SessionLayout.EnsureHidden(sd), tk.Tag, trim, ct);
            // Per-track prep info (transcript.json {infos} field), collected for every
            // track including silent ones, mirroring audio_prep.prepare_track's `info`.
            infos[tk.Tag] = new { duration = prep.Duration, trimmed = prep.Trimmed, kept = prep.Kept, removed = prep.Removed };
            if (prep.WavPath is null)
            {
                log?.Report($"  [{tk.Tag}] silent track, skipped.");
                continue;
            }
            log?.Report($"  [{tk.Tag}] {prep.Kept:0}s kept"
                + (prep.Trimmed ? $" (-{prep.Removed:0}s silence)" : "")
                + $", offset {offset:+0.00;-0.00}s");

            // Backends with a request-size ceiling (Whisper on Workers AI) get the prepared
            // track in consecutive pieces; each piece's timestamps are shifted by its start
            // before the trim remap, so the rest of the pipeline sees one continuous track.
            var pieces = client is IChunkedTranscribe chunked
                ? SplitWavFn(prep.WavPath, chunked.ChunkSeconds)
                : new List<(string Path, double StartSeconds)> { (prep.WavPath, 0.0) };
            if (pieces.Count > 1)
                log?.Report($"  [{tk.Tag}] envoi en {pieces.Count} morceaux de {(client as IChunkedTranscribe)!.ChunkSeconds / 60} min.");

            var rawParts = new List<string>();
            try
            {
                for (int pi = 0; pi < pieces.Count; pi++)
                {
                    var (piecePath, pieceStart) = pieces[pi];
                    var pieceTag = pieces.Count > 1 ? $"{tk.Tag} {pi + 1}/{pieces.Count}" : tk.Tag;
                    var result = await UploadAndTranscribe(client, apiKey, model, piecePath, pieceTag, contextBias, log, ct);
                    rawParts.Add(result.RawJson);
                    foreach (var seg in result.Segments)
                    {
                        // An untimed segment of a multi-piece track (text-only answer) is pinned
                        // to its piece's start: otherwise all of them sort after every timed
                        // segment and piece 3's text could land before piece 1's.
                        double? segStart = seg.Start ?? (pieces.Count > 1 ? 0.0 : null);
                        var s = AudioPrep.RemapTime(segStart is null ? null : segStart + pieceStart, prep.Mapping);
                        var e = AudioPrep.RemapTime(seg.End is null ? null : seg.End + pieceStart, prep.Mapping);
                        all.Add(new Seg
                        {
                            Start = s is null ? null : s + offset,
                            End = e is null ? null : e + offset,
                            Text = seg.Text,
                            VoxSpeaker = seg.VoxSpeaker,
                            Origin = tk.Label,
                        });
                    }
                }
            }
            finally
            {
                if (pieces.Count > 1)
                    foreach (var (piecePath, _) in pieces) AudioArchive.TryDelete(piecePath);
            }
            raw[tk.Tag] = rawParts.Count == 1 ? rawParts[0] : "[" + string.Join(",", rawParts) + "]";
        }
        return SortByStart(all);
    }

    /// <summary>
    /// Upload one prepared WAV (or piece of it) and return the provider's segments in
    /// FILE-LOCAL seconds. Compact MP3 upload (~4× smaller than the prepared PCM): fewer
    /// network timeouts on 2-3 h meetings and much cheaper retries. The WAV is kept
    /// untouched as the fallback: a 400/422 escaping the whole ladder means the server
    /// rejected the FILE (not a parameter), so we resend the WAV once.
    /// </summary>
    private static async Task<VoxtralResult> UploadAndTranscribe(
        ITranscribeClient client, string apiKey, string model, string wavPath, string tag,
        IReadOnlyList<string> contextBias, IProgress<string>? log, CancellationToken ct)
    {
        var uploadMp3 = AudioArchive.EncodeForUpload(wavPath, m => log?.Report(m), ct);
        try
        {
            var uploadPath = uploadMp3 ?? wavPath;
            if (uploadMp3 is not null)
                log?.Report($"  [{tag}] envoi en mp3 : {new FileInfo(uploadMp3).Length / (1024 * 1024)} Mo"
                    + $" (wav : {new FileInfo(wavPath).Length / (1024 * 1024)} Mo).");
            var bytes = await File.ReadAllBytesAsync(uploadPath, ct);
            try
            {
                return await client.TranscribeAsync(
                    apiKey, model, bytes, Path.GetFileName(uploadPath), contextBias, log, ct);
            }
            catch (LlmApiException e) when (uploadMp3 is not null && e.StatusCode is 400 or 422)
            {
                log?.Report($"  [{tag}] mp3 refusé par l'API (HTTP {e.StatusCode}), nouvel envoi en WAV.");
                bytes = await File.ReadAllBytesAsync(wavPath, ct);
                return await client.TranscribeAsync(
                    apiKey, model, bytes, Path.GetFileName(wavPath), contextBias, log, ct);
            }
        }
        finally
        {
            if (uploadMp3 is not null) AudioArchive.TryDelete(uploadMp3);
        }
    }

    // ── Teams UIA intervals ─────────────────────────────────────────────────────

    private List<Interval> BuildIntervals(string sd, double? startEpoch,
        double uiaLag = 1.0, double gap = 1.8, double pad = 0.5, double minSeg = 0.7, int minPolls = 2)
    {
        var path = SessionLayout.Find(sd, SessionLayout.Speakers);
        if (!File.Exists(path) || startEpoch is null) return new List<Interval>();
        var byName = new Dictionary<string, List<double>>();
        foreach (var line in File.ReadLines(path))
        {
            var s = line.Trim();
            if (s.Length == 0) continue;
            JsonElement rec;
            try { rec = JsonDocument.Parse(s).RootElement; } catch { continue; }
            if (GetDouble(rec, "t") is not { } t) continue;
            double rel = t - startEpoch.Value - uiaLag;
            foreach (var nm in GetStringList(rec, "speakers"))
                (byName.TryGetValue(nm, out var lst) ? lst : byName[nm] = new List<double>()).Add(rel);
        }
        var intervals = new List<Interval>();
        foreach (var (nm, timesRaw) in byName)
        {
            var times = timesRaw;
            times.Sort();
            var group = new List<double> { times[0] };
            for (int i = 1; i < times.Count; i++)
            {
                if (times[i] - group[^1] <= gap) group.Add(times[i]);
                else { Flush(intervals, nm, group, pad, minSeg, minPolls); group = new List<double> { times[i] }; }
            }
            Flush(intervals, nm, group, pad, minSeg, minPolls);
        }
        return intervals;
    }

    private static void Flush(List<Interval> intervals, string nm, List<double> group, double pad, double minSeg, int minPolls)
    {
        if (group.Count < minPolls && (group[^1] - group[0]) < minSeg) return; // debounce isolated blip
        intervals.Add(new Interval(nm, Math.Max(0.0, group[0] - pad), group[^1] + pad));
    }

    private static double Overlap(double s, double e, double a, double b) => Math.Max(0.0, Math.Min(e, b) - Math.Max(s, a));

    // ── attribution ─────────────────────────────────────────────────────────────

    private List<Seg> Attribute(List<Seg> segments, List<Interval> intervals,
        double coverageMin = 0.30, double marginMin = 0.15, double nearestMax = 0.75)
    {
        var names = intervals.Select(iv => iv.Name).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
        var outp = new List<Seg>();
        foreach (var seg in segments)
        {
            var c = seg.Clone();
            double? s = seg.Start, e = seg.End;
            if (s is null || e is null || e <= s || intervals.Count == 0)
            {
                c.Speaker = null;
                outp.Add(c);
                continue;
            }
            var scores = names.ToDictionary(n => n, _ => 0.0);
            foreach (var iv in intervals) scores[iv.Name] += Overlap(s.Value, e.Value, iv.Start, iv.End);
            var ranked = scores.OrderByDescending(kv => kv.Value).ToList();
            var (best, bestv) = (ranked[0].Key, ranked[0].Value);
            double secondv = ranked.Count > 1 ? ranked[1].Value : 0.0;
            double dur = e.Value - s.Value;
            if (bestv / dur >= coverageMin && (bestv - secondv) / dur >= marginMin)
            {
                c.Speaker = best;
            }
            else
            {
                string? spk = null;
                double gapmin = 1e9;
                foreach (var iv in intervals)
                {
                    double g = Overlap(s.Value, e.Value, iv.Start, iv.End) > 0
                        ? 0
                        : Math.Min(Math.Abs(s.Value - iv.End), Math.Abs(iv.Start - e.Value));
                    if (g < gapmin) { gapmin = g; spk = iv.Name; }
                }
                c.Speaker = gapmin > nearestMax ? null : spk;
            }
            outp.Add(c);
        }
        return outp;
    }

    /// <summary>Split-mode attribution: trust the full UIA timeline per-segment, then
    /// fall back by track origin (local -> local name/"Vous", remote -> "Participant N").</summary>
    private List<Seg> AttributeSplit(List<Seg> merged, List<Interval> intervals, string? localName)
    {
        var attributed = Attribute(merged, intervals);
        var participants = ParticipantNumbers(attributed.Where(s => s.Origin != "local"));
        var outp = new List<Seg>();
        foreach (var s in attributed)
        {
            if (!string.IsNullOrEmpty(s.Speaker)) { outp.Add(s); continue; }
            if (s.Origin == "local")
            {
                s.Speaker = localName ?? "Vous";
            }
            else
            {
                s.Speaker = ParticipantLabel(s, participants);
            }
            outp.Add(s);
        }
        return SortByStart(outp);
    }

    /// <summary>Single-track attribution (imports, physical meetings, mixdown): Teams timeline
    /// first, then the engine's diarisation ids as "Participant 1..N"; segments without either
    /// stay unattributed and <see cref="WriteProcessed"/> cuts them on pauses.</summary>
    private List<Seg> AttributeSingle(List<Seg> merged, List<Interval> intervals)
    {
        var attributed = Attribute(merged, intervals);
        var participants = ParticipantNumbers(attributed);
        foreach (var s in attributed)
            if (string.IsNullOrEmpty(s.Speaker)) s.Speaker = ParticipantLabel(s, participants);
        return attributed;
    }

    /// <summary>Engine speaker ids ("speaker_2", "0", …) renumbered 1..N in order of first
    /// appearance, so the transcript reads "Participant 1" rather than "Participant speaker_0".</summary>
    private static Dictionary<string, int> ParticipantNumbers(IEnumerable<Seg> segs)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in segs)
            if (!string.IsNullOrEmpty(s.VoxSpeaker) && !map.ContainsKey(s.VoxSpeaker!))
                map[s.VoxSpeaker!] = map.Count + 1;
        return map;
    }

    private static string? ParticipantLabel(Seg s, Dictionary<string, int> numbers)
        => !string.IsNullOrEmpty(s.VoxSpeaker) && numbers.TryGetValue(s.VoxSpeaker!, out var n) ? $"Participant {n}" : null;

    private string? DetectLocalName(List<Seg> localSegments, List<Interval> intervals, double minOverlap = 1.0, double dominance = 0.5)
    {
        if (localSegments.Count == 0 || intervals.Count == 0) return null;
        var scores = new Dictionary<string, double>();
        foreach (var seg in localSegments)
        {
            if (seg.Start is not { } s || seg.End is not { } e) continue;
            foreach (var iv in intervals)
            {
                double ov = Overlap(s, e, iv.Start, iv.End);
                if (ov > 0) scores[iv.Name] = scores.GetValueOrDefault(iv.Name) + ov;
            }
        }
        if (scores.Count == 0) return null;
        var top = scores.OrderByDescending(kv => kv.Value).First();
        double total = scores.Values.Sum();
        return (top.Value >= minOverlap && top.Value >= dominance * total) ? top.Key : null;
    }

    /// <summary>STABLE sort by start (untimed last). List.Sort is unstable: segments with equal
    /// or missing starts came out in an arbitrary order across pieces and runs. Ties keep the
    /// production order (track, then piece, then segment).</summary>
    private static List<Seg> SortByStart(List<Seg> segs)
        => segs.Select((s, i) => (s, i))
               .OrderBy(x => x.s.Start is null ? 1 : 0)
               .ThenBy(x => x.s.Start ?? 0.0)
               .ThenBy(x => x.i)
               .Select(x => x.s)
               .ToList();

    // ── post-transcription correction (deterministic + guarded LLM) ─────────────

    /// <summary>Run the postprocess pass over the attributed segments in place:
    /// deterministic lexicon correction (always) then optional guarded LLM correction
    /// (postprocess.py). Never throws; on any failure it falls back to at least the
    /// deterministic pass so the Processed Transcript is still corrected.</summary>
    private async Task ApplyPostprocess(
        string sd, TranscriptionRequest request, Vocabulary vocab, List<Seg> attributed, string meetingName,
        IReadOnlyList<string> attendees, IProgress<string>? log, CancellationToken ct)
    {
        try
        {
            var rawTexts = attributed.Select(s => s.Text).ToList();
            // The LLM postprocess follows the CR provider (it is a chat cleanup), which may
            // differ from the transcription provider, hence its own key (ChatApiKey) and model.
            var (chatClient, ppModel) = ResolvePostprocessChat(request.Settings);
            var chatKey = string.IsNullOrEmpty(request.ChatApiKey) ? request.ApiKey : request.ChatApiKey!;
            ChatFunc? chat = string.IsNullOrEmpty(chatKey)
                ? null
                : (msgs, c) => chatClient.ChatCompleteAsync(chatKey, ppModel, msgs, temperature: 0.0, jsonObject: true, c);

            var pp = new Postprocess(chat);
            var (corrected, stats) = await pp.RunAsync(
                rawTexts, vocab, sd, attendees, meetingName,
                enableLlm: request.Settings.PostprocessLlm, log, ct);

            for (int i = 0; i < attributed.Count && i < corrected.Count; i++) attributed[i].Text = corrected[i];
            log?.Report($"  Post-processing: {stats.Deterministic} deterministic correction(s), "
                + $"{stats.LlmApplied} LLM applied, {stats.LlmRejected} rejected.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            // Defence in depth (RunAsync already swallows): keep the deterministic-only text.
            log?.Report($"  (post-processing skipped: {e.Message})");
            foreach (var s in attributed) s.Text = vocab.CanonicalizeDeterministic(s.Text);
        }
    }

    // ── text-level echo dedup (transcribe.echo_analysis) ────────────────────────

    /// <summary>Analyse <paramref name="merged"/> for microphone echo and return the
    /// SET OF MERGED-LIST INDICES to drop (plus the echo duration share).</summary>
    private static EchoDedup.EchoReport EchoAnalysis(List<Seg> merged)
    {
        var view = merged.Select(s => new EchoDedup.EchoSeg(s.Origin, s.Start, s.End, s.Text)).ToList();
        return EchoDedup.AnalyzeDetailed(view);
    }

    // ── outputs ─────────────────────────────────────────────────────────────────

    /// <summary>"MM:SS" below one hour, "H:MM:SS" from 3600 s on. The old minutes-only form
    /// wrote "125:30" for a 2 h meeting, which the CR (ts validation) and the UI rejected.</summary>
    internal static string FmtTs(double? sec)
    {
        if (sec is null) return "??:??";
        int s = Math.Max(0, (int)sec.Value);
        if (s >= 3600) return $"{s / 3600}:{s % 3600 / 60:00}:{s % 60:00}";
        return $"{s / 60:00}:{s % 60:00}";
    }

    /// <summary>Inverse of <see cref="FmtTs"/>; also reads the legacy 3-digit minutes form
    /// ("125:30"). Null for "??:??" or anything else.</summary>
    internal static double? ParseTs(string? ts)
    {
        var m = System.Text.RegularExpressions.Regex.Match((ts ?? "").Trim(), @"^(\d{1,3}):(\d{2})(?::(\d{2}))?$");
        if (!m.Success) return null;
        int a = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        int b = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return m.Groups[3].Success
            ? a * 3600 + b * 60 + int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)
            : a * 60 + b;
    }

    private string WriteRaw(string sd, List<Seg> merged, Dictionary<string, string> raw, Dictionary<string, object> infos)
    {
        // transcript.json {infos, raw, merged}: debug/audit (per-track prep info + raw
        // per-track Voxtral dumps + merged segments), same key order as Python write_raw.
        var rawObjects = new Dictionary<string, JsonElement>();
        foreach (var (tag, json) in raw)
        {
            try { rawObjects[tag] = JsonDocument.Parse(json).RootElement.Clone(); } catch { /* skip */ }
        }
        var payload = new
        {
            infos,
            raw = rawObjects,
            merged = merged.Select(s => new { start = s.Start, end = s.End, text = s.Text, vox_speaker = s.VoxSpeaker, origin = s.Origin }),
        };
        var opts = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.WriteAllText(SessionLayout.ForWrite(sd, SessionLayout.TranscriptJson), JsonSerializer.Serialize(payload, opts), new UTF8Encoding(false));

        var text = string.Join(" ", merged.Where(s => !string.IsNullOrEmpty(s.Text)).Select(s => s.Text)).Trim();
        // Raw text out of sight: the visible "<Titre> - Transcript.txt" is the corrected one.
        var rawTxt = SessionLayout.ForWrite(sd, SessionLayout.RawTranscript);
        File.WriteAllText(rawTxt, text + "\n", new UTF8Encoding(false));
        return rawTxt;
    }

    private (string Path, List<TranscriptTurn> Turns) WriteProcessed(string sd, List<Seg> attributed, string header)
    {
        var path = SessionLayout.TranscriptPath(sd);
        var lines = new List<string> { header, "" };
        var turns = new List<TranscriptTurn>();

        string? curSpk = null;
        var buf = new List<string>();
        double? curStart = null;
        double? curEnd = null;

        void FlushTurn()
        {
            if (buf.Count == 0) return;
            var spk = curSpk ?? "Unknown";
            lines.Add($"[{FmtTs(curStart)}] {spk} :");
            var joined = string.Join(" ", buf).Trim();
            lines.Add(joined);
            lines.Add("");
            turns.Add(new TranscriptTurn(curStart ?? 0.0, curEnd ?? curStart ?? 0.0, spk, joined));
        }

        foreach (var s in attributed)
        {
            var spk = string.IsNullOrEmpty(s.Speaker) ? "Unknown" : s.Speaker!;
            // Unattributed speech (Whisper, no Teams names): a pause starts a new turn, so the
            // transcript is readable paragraphs instead of one block for the whole meeting.
            bool pause = spk == curSpk && spk == "Unknown" && curEnd is { } pe && s.Start is { } ss
                         && ss - pe >= PauseTurnSeconds;
            if (spk != curSpk || pause)
            {
                FlushTurn();
                curSpk = spk;
                buf = new List<string>();
                curStart = s.Start;
                curEnd = null;   // the end of THIS turn, not the previous speaker's
            }
            buf.Add(s.Text);
            curEnd = s.End ?? curEnd;
        }
        FlushTurn();

        var textOut = string.Join("\n", lines).Trim() + "\n";
        File.WriteAllText(path, textOut, new UTF8Encoding(false));
        return (path, turns);
    }

    // ── session helpers ─────────────────────────────────────────────────────────

    private IReadOnlyList<string> SpeakersSeen(string sd)
    {
        var path = SessionLayout.Find(sd, SessionLayout.Speakers);
        var names = new HashSet<string>();
        if (!File.Exists(path)) return Array.Empty<string>();
        // FileShare.ReadWrite: a still-open writer (live capture of this very session) must
        // not turn the read into an IOException (seen 2026-09-16 13:27).
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var rd = new StreamReader(fs);
        for (string? line; (line = rd.ReadLine()) is not null;)
        {
            JsonElement rec;
            try { rec = JsonDocument.Parse(line).RootElement; } catch { continue; }
            foreach (var n in GetStringList(rec, "speakers")) names.Add(n);
        }
        return names.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    // ── JSON reading ──────────────────────────────────────────────────────────

    internal static JsonElement? LoadJson(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone(); }
        catch { return null; }
    }

    internal static string? GetString(JsonElement? e, string name)
        => e is { } el && el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static bool GetBool(JsonElement? e, string name)
        => e is { } el && el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.True;

    internal static double? GetDouble(JsonElement? e, string name)
    {
        if (e is not { } el || el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
    }

    internal static double? GetDouble(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    internal static List<string> GetStringList(JsonElement? e, string name)
    {
        var outp = new List<string>();
        if (e is { } el && el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
            foreach (var item in v.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } str) outp.Add(str);
        return outp;
    }

    internal static List<string> GetStringList(JsonElement e, string name)
        => GetStringList((JsonElement?)e, name);
}
