using System.Text;
using System.Text.Json;
using Echo.Ai;
using Echo.Core.Abstractions;
using Echo.Core.Models;

namespace Echo.Tests;

/// <summary>
/// End-to-end runs of <see cref="Transcriber.TranscribeAsync"/> on a synthetic session folder
/// with a FAKE transcription client (no network, no key). Audio preparation and piece
/// splitting go through the transcriber's seams, so the timestamp remapping (trim mapping +
/// piece offset + track offset), the ordering, the attribution and the processed transcript
/// are checked deterministically.
/// </summary>
public class AiTranscriberPipelineTests
{
    // ── fakes & fixtures ───────────────────────────────────────────────────────

    /// <summary>Scripted transcription backend; optionally "chunked" like Whisper on Workers AI.</summary>
    private class FakeTranscribe : ITranscribeClient
    {
        public List<(string FileName, IReadOnlyList<string>? Bias)> Calls { get; } = new();
        public Func<string, int, IReadOnlyList<VoxSegment>> Script { get; init; } = (_, _) => Array.Empty<VoxSegment>();

        public Task<VoxtralResult> TranscribeAsync(string apiKey, string model, byte[] audioBytes, string fileName,
            IReadOnlyList<string>? contextBias, IProgress<string>? log, CancellationToken ct)
        {
            Calls.Add((fileName, contextBias));
            var segs = Script(fileName, Calls.Count);
            return Task.FromResult(new VoxtralResult(string.Join(" ", segs.Select(s => s.Text)), "fr", segs, "{\"segments\":[]}"));
        }
    }

    private sealed class FakeChunkedTranscribe : FakeTranscribe, IChunkedTranscribe
    {
        public int ChunkSeconds => 10;
    }

    private sealed class NoChat : IChatClient
    {
        public Task<string> ChatCompleteAsync(string apiKey, string model, IReadOnlyList<ChatMessage> messages,
            double temperature, bool jsonObject, CancellationToken ct) => throw new InvalidOperationException("no chat in this test");
    }

    private static string NewSession(string name, string[]? roster = null)
    {
        var sd = Path.Combine(Path.GetTempPath(), $"echo_aitr_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sd);
        var meeting = new Dictionary<string, object?> { ["name"] = name, ["date_str"] = "07/10/2026", ["roster"] = roster ?? Array.Empty<string>() };
        File.WriteAllText(Path.Combine(sd, "meeting.json"), JsonSerializer.Serialize(meeting));
        return sd;
    }

    private static void AddTrack(string sd, string tag, double t0, bool withAudio = true)
    {
        File.WriteAllText(Path.Combine(sd, $"{tag}.json"),
            JsonSerializer.Serialize(new { got_audio = true, t0_wall_epoch = t0 }));
        if (withAudio) WriteSilentWav(Path.Combine(sd, $"{tag}.wav"), 1.0);
    }

    private static void WriteSilentWav(string path, double seconds)
    {
        int samples = (int)(16000 * seconds), data = samples * 2;
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8.ToArray()); w.Write(36 + data); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(data); w.Write(new byte[data]);
    }

    private static Vocabulary TestVocab()
    {
        var lex = Path.Combine(Path.GetTempPath(), $"lex_{Guid.NewGuid():N}.txt");
        File.WriteAllText(lex, "SteerCo\n");
        return new Vocabulary(defaultLexiconPath: lex, missionBiasPath: Path.Combine(Path.GetTempPath(), $"nomission_{Guid.NewGuid():N}.txt"), env: _ => null);
    }

    private static AppSettings Settings() => new() { PostprocessLlm = false, KeepOriginalWav = true };

    /// <summary>Trim mapping used by every test: trimmed 0-10 s = real 5-15 s, trimmed 10-30 s = real 40-60 s.</summary>
    private static readonly IReadOnlyList<(double, double, double)> Mapping = new List<(double, double, double)> { (0, 10, 5), (10, 30, 40) };

    private static TrackPrep FakePrep(string wav, string dir, string tag)
    {
        var prepared = Path.Combine(dir, $"{tag}_16k.wav");
        WriteSilentWav(prepared, 1.0);
        return new TrackPrep(prepared, Mapping, 60, 30, 30, true);
    }

    private static List<(string Path, double StartSeconds)> FakeSplit(string wav)
    {
        var stem = Path.Combine(Path.GetDirectoryName(wav)!, Path.GetFileNameWithoutExtension(wav));
        WriteSilentWav(stem + "_c0.wav", 1.0);
        WriteSilentWav(stem + "_c1.wav", 1.0);
        return new() { (stem + "_c0.wav", 0.0), (stem + "_c1.wav", 12.0) };
    }

    private static Transcriber NewTranscriber(ITranscribeClient client, Vocabulary vocab) =>
        new(client, new NoChat(), vocab: vocab, env: _ => null)
        {
            PrepareTrackFn = (wav, dir, tag, _, _) => FakePrep(wav, dir, tag),
            SplitWavFn = (wav, _) => FakeSplit(wav),
        };

    private static string Processed(string sd) =>
        File.ReadAllText(Directory.GetFiles(sd, "* - Processed Transcript.txt").Single());

    // ── two tracks, several pieces each ────────────────────────────────────────

    [Fact]
    public async Task TwoTracks_SeveralPieces_TimestampsRemapped_OrderStable_ParticipantsInBias()
    {
        var sd = NewSession("Point budget", new[] { "Alice Martin", "Bob Durand" });
        try
        {
            AddTrack(sd, "mic", 1000.0);
            AddTrack(sd, "system", 1002.0);          // system track started 2 s after the mic
            var client = new FakeChunkedTranscribe
            {
                Script = (file, _) =>
                {
                    bool mic = file.StartsWith("mic", StringComparison.Ordinal);
                    bool second = file.Contains("_c1", StringComparison.Ordinal);
                    if (mic)
                        return new[] { second
                            ? new VoxSegment(1, 2, "je propose de valider le planning cette semaine", null)
                            : new VoxSegment(1, 2, "bonjour je presente le budget annuel", null) };
                    return second
                        ? new[]
                        {
                            new VoxSegment(null, null, "segment sans horodatage du second morceau", null),
                            new VoxSegment(2, 3, "nous reviendrons vers vous jeudi prochain", null),
                        }
                        : new[] { new VoxSegment(2, 3, "merci pour cette introduction tres claire", null) };
                },
            };
            var t = NewTranscriber(client, TestVocab());

            var res = await t.TranscribeAsync(new TranscriptionRequest(sd, "k", Settings(), new[] { "SteerCo" }), null, CancellationToken.None);

            Assert.Equal(4, client.Calls.Count);                      // 2 tracks x 2 pieces
            // Piece 1 starts at 12 s of the trimmed track. mic piece 0: local 1 s -> trimmed 1 ->
            // real 6 (offset 0); piece 1: 1+12=13 -> real 43. system piece 0: 2 -> real 7, +2 s
            // track offset = 9; piece 1: 14 -> 44 -> 46; the untimed segment of piece 1 is pinned
            // to its piece start: 12 -> 42 -> 44, i.e. BEFORE its timed sibling, not at the end.
            Assert.Equal(new[] { 6.0, 9.0, 43.0, 44.0 }, res.Turns.Select(x => x.StartS).ToArray());
            Assert.Equal(new[] { "Vous", "Unknown", "Vous", "Unknown" }, res.Turns.Select(x => x.Speaker).ToArray());
            Assert.Equal("segment sans horodatage du second morceau nous reviendrons vers vous jeudi prochain", res.Turns[3].Text);

            var text = Processed(sd);
            Assert.Contains("[00:06] Vous :", text);
            Assert.Contains("[00:43] Vous :", text);

            // The roster reaches the engine even though the caller passed its own (generic) bias.
            Assert.All(client.Calls, c => Assert.Contains("Alice_Martin", c.Bias!));
            Assert.All(client.Calls, c => Assert.Contains("SteerCo", c.Bias!));
        }
        finally { Directory.Delete(sd, true); }
    }

    // ── single track: diarisation without Teams names ──────────────────────────

    [Fact]
    public async Task SingleTrack_NoTeamsNames_UsesDiarisation_Participant1ToN()
    {
        var sd = NewSession("Import dictaphone");
        try
        {
            AddTrack(sd, "system", 500.0);
            var client = new FakeTranscribe
            {
                Script = (_, _) => new[]
                {
                    new VoxSegment(0, 2, "premier intervenant ouvre la reunion", "speaker_3"),
                    new VoxSegment(2, 4, "deuxieme intervenant repond", "speaker_1"),
                    new VoxSegment(4, 6, "premier intervenant reprend", "speaker_3"),
                    new VoxSegment(6, 8, "troisieme intervenant conclut", "speaker_0"),
                },
            };
            var res = await NewTranscriber(client, TestVocab())
                .TranscribeAsync(new TranscriptionRequest(sd, "k", Settings(), Array.Empty<string>()), null, CancellationToken.None);

            Assert.Equal(new[] { "Participant 1", "Participant 2", "Participant 1", "Participant 3" },
                res.Turns.Select(x => x.Speaker).ToArray());
            Assert.DoesNotContain("Unknown", Processed(sd));
        }
        finally { Directory.Delete(sd, true); }
    }

    [Fact]
    public async Task SingleTrack_NoDiarisation_TurnsCutOnPauses()
    {
        var sd = NewSession("Import whisper");
        try
        {
            AddTrack(sd, "system", 500.0);
            var client = new FakeTranscribe
            {
                Script = (_, _) => new[]
                {
                    new VoxSegment(0, 2, "premiere phrase", null),
                    new VoxSegment(2.5, 4, "suite immediate", null),    // 0.5 s gap: same turn
                    new VoxSegment(7, 9, "apres une vraie pause", null), // 3 s gap: new turn
                },
            };
            var res = await NewTranscriber(client, TestVocab())
                .TranscribeAsync(new TranscriptionRequest(sd, "k", Settings(), Array.Empty<string>()), null, CancellationToken.None);

            Assert.Equal(2, res.Turns.Count);
            Assert.Equal("premiere phrase suite immediate", res.Turns[0].Text);
        }
        finally { Directory.Delete(sd, true); }
    }

    // ── re-transcription after retention ───────────────────────────────────────

    [Fact]
    public async Task TracksPurged_MixdownPresent_TranscribesTheMixdownAsOneRemoteTrack()
    {
        var sd = NewSession("Revue");
        try
        {
            AddTrack(sd, "mic", 1000.0, withAudio: false);
            AddTrack(sd, "system", 1000.0, withAudio: false);
            File.WriteAllBytes(Path.Combine(sd, "Revue - Audio.mp3"), new byte[4096]);
            string? prepared = null;
            var client = new FakeTranscribe { Script = (_, _) => new[] { new VoxSegment(0, 2, "texte du mixage", "speaker_0") } };
            var t = new Transcriber(client, new NoChat(), vocab: TestVocab(), env: _ => null)
            {
                PrepareTrackFn = (wav, dir, tag, _, _) => { prepared = wav; return FakePrep(wav, dir, tag); },
            };
            var logs = new List<string>();

            var res = await t.TranscribeAsync(new TranscriptionRequest(sd, "k", Settings(), Array.Empty<string>()),
                new SyncProgress(logs), CancellationToken.None);

            Assert.EndsWith("Revue - Audio.mp3", prepared);
            Assert.Single(client.Calls);
            Assert.Equal("Participant 1", res.Turns.Single().Speaker);          // remote, diarised; never "Vous"
            Assert.Contains(logs, l => l.Contains("mixage", StringComparison.Ordinal));
            Assert.False(File.Exists(Path.Combine(sd, "system_16k.wav")));        // regenerable copy cleaned
        }
        finally { Directory.Delete(sd, true); }
    }

    [Fact]
    public async Task TracksPurged_NoMixdown_ThrowsAudioPurged_WithFrenchMessage()
    {
        var sd = NewSession("Revue");
        try
        {
            AddTrack(sd, "mic", 1000.0, withAudio: false);
            var t = NewTranscriber(new FakeTranscribe(), TestVocab());

            var ex = await Assert.ThrowsAsync<AudioPurgedException>(() =>
                t.TranscribeAsync(new TranscriptionRequest(sd, "k", Settings(), Array.Empty<string>()), null, CancellationToken.None));

            Assert.Equal("audio_purged", ex.Code);
            Assert.Contains("supprimé par la rétention", ex.UserMessage);
            Assert.IsAssignableFrom<InvalidOperationException>(ex);              // old catch sites still match
        }
        finally { Directory.Delete(sd, true); }
    }

    // ── timestamps beyond 99 minutes ──────────────────────────────────────────

    [Theory]
    [InlineData(6000.0, "1:40:00")]
    [InlineData(3599.0, "59:59")]
    [InlineData(3600.0, "1:00:00")]
    [InlineData(3930.0, "1:05:30")]
    public void FmtTs_HoursFromOneHour_AndParsesBack(double sec, string expected)
    {
        Assert.Equal(expected, Transcriber.FmtTs(sec));
        Assert.Equal(sec, Transcriber.ParseTs(expected));
    }

    [Fact]
    public void ParseTs_AcceptsLegacyThreeDigitMinutes()
    {
        Assert.Equal(125 * 60 + 30, Transcriber.ParseTs("125:30"));
        Assert.Null(Transcriber.ParseTs("??:??"));
    }

    /// <summary>IProgress that reports synchronously (Progress&lt;T&gt; posts to the thread pool).</summary>
    private sealed class SyncProgress : IProgress<string>
    {
        private readonly List<string> _sink;
        public SyncProgress(List<string> sink) => _sink = sink;
        public void Report(string value) { lock (_sink) _sink.Add(value); }
    }
}
