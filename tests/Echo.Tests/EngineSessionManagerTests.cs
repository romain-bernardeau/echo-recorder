using Echo.Ai;
using Echo.App;
using Echo.App.Engine;
using Echo.Core.Abstractions;
using Echo.Core.Models;
using Echo.Core.Storage;

namespace Echo.Tests;

/// <summary>
/// SessionManager pipeline outcomes, on a TempProfile (never a real profile) with fake
/// engines. Pins the retry-queue loop (a terminal failure left the item due and re-ran it
/// every minute, re-sending the audio and toasting twice a minute), the "re-transcription
/// never degrades a transcribed session" rule, the "no usable audio" message, and the startup
/// recovery of pipelines killed with the app.
/// </summary>
public sealed class EngineSessionManagerTests : IDisposable
{
    private readonly TempProfile _profile = new();
    private readonly StoragePaths _paths;
    private readonly SettingsStore _settings;
    private readonly FakeNotifier _notifier = new();
    private readonly FakeTranscriber _transcriber = new();
    private readonly SessionManager _sm;

    public EngineSessionManagerTests()
    {
        _paths = _profile.Paths();
        _settings = new SettingsStore(_paths);
        _settings.Update(s => { s.AutoFallbackProvider = false; s.AutoMinutes = true; s.AutoTranscribe = true; });
        Directory.CreateDirectory(_paths.RecordingsDir);
        _sm = new SessionManager(
            () => throw new InvalidOperationException("no capture in tests"),
            new FakeTeams(), _transcriber, new FakeMinutes(), new FakeSummarizer(), new FakeVocab(),
            _settings, new FakeSecret(), _paths, _notifier, new I18n(() => "fr"));
    }

    public void Dispose() => _profile.Dispose();

    private string Session(string name, bool transcript = false, bool audio = true, string? status = null)
    {
        var dir = Path.Combine(_paths.RecordingsDir, $"20261007 - 10h00 - {name}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "meeting.json"), $"{{\"name\":\"{name}\",\"physical\":false}}");
        if (audio) File.WriteAllBytes(Path.Combine(dir, "mic.wav"), new byte[4096]);
        if (transcript) File.WriteAllText(Path.Combine(dir, $"{name} - Transcript.txt"), "[00:00] A : bonjour");
        if (status is not null) File.WriteAllText(Path.Combine(dir, SessionStatus.FileName), status);
        return dir;
    }

    private static string? State(string dir) => SessionStatus.Read(dir)?.State;

    private ProcessingQueue.Item Queue(string dir, int attempts = 1)
    {
        // Park it like a transient failure did, then make it due now.
        for (int i = 0; i < attempts; i++) _sm.Queue.Enqueue(dir, minutes: true, summarize: true, "test");
        return _sm.Queue.Snapshot().Single(i => SessionFiles.SamePath(i.Dir, dir));
    }

    // ── retry queue ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(400)]
    public async Task QueuedRun_TerminalApiError_LeavesTheQueue(int status)
    {
        var dir = Session("Terminal");
        var it = Queue(dir);
        _transcriber.Throw = new MistralApiException(status, "refused", 1);

        await _sm.RunQueuedAsync(it);

        Assert.Null(_sm.Queue.Get(dir));
        Assert.Equal("error", State(dir));
        Assert.Equal(1, _transcriber.Calls);
    }

    [Fact]
    public async Task QueuedRun_TransientError_IsReparkedWithTheNextDelay()
    {
        var dir = Session("Sature");
        var it = Queue(dir);
        _transcriber.Throw = new MistralApiException(429, "rate limit", 3);

        await _sm.RunQueuedAsync(it);

        var after = _sm.Queue.Get(dir);
        Assert.NotNull(after);
        Assert.Equal(it.Attempts + 1, after!.Attempts);
        Assert.True(after.NextAtEpoch > DateTimeOffset.Now.ToUnixTimeSeconds());
        Assert.Equal("waiting", State(dir));
    }

    [Fact]
    public async Task QueuedRun_UnexpectedException_LeavesTheQueue()
    {
        var dir = Session("Crash");
        var it = Queue(dir);
        _transcriber.Throw = new IOException("disk full");

        await _sm.RunQueuedAsync(it);

        Assert.Null(_sm.Queue.Get(dir));
        Assert.Equal("error", State(dir));
    }

    [Fact]
    public async Task QueuedRun_Success_ClearsTheItemAndTheBadge()
    {
        var dir = Session("Ok");
        var it = Queue(dir);

        await _sm.RunQueuedAsync(it);

        Assert.Null(_sm.Queue.Get(dir));
        Assert.Null(State(dir));
        Assert.True(File.Exists(Path.Combine(dir, "CR - Minutes.md")));
    }

    // ── re-transcription / no audio ──────────────────────────────────────────────

    [Fact]
    public async Task FailedRetranscription_NeverDegradesATranscribedSession()
    {
        var dir = Session("DejaTranscrit", transcript: true);
        _transcriber.Throw = new MistralApiException(400, "bad request", 1);

        await _sm.ProcessAsync(dir, forceTranscribe: true);

        Assert.Null(State(dir));                         // no red "error" badge
        Assert.True(File.Exists(Path.Combine(dir, "DejaTranscrit - Transcript.txt")));
        Assert.DoesNotContain(_notifier.Messages, m => m.Contains("corrigez le modèle"));
    }

    [Fact]
    public async Task NoUsableAudio_SaysSo_AndKeepsThePreviousState()
    {
        var dir = Session("Purge", transcript: true, audio: false);
        _transcriber.Throw = new InvalidOperationException("No usable audio track.");

        await _sm.ProcessAsync(dir, forceTranscribe: true);

        Assert.Null(State(dir));
        Assert.DoesNotContain(_notifier.Messages, m => m.Contains("corrigez le modèle"));
        Assert.Contains(_notifier.Messages, m => m.Contains("audio", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AudioPurged_KeepsTheTranscribedSessionIntact()
    {
        var dir = Session("Retention", transcript: true, audio: false);
        _transcriber.Throw = new AudioPurgedException();

        await _sm.ProcessAsync(dir, forceTranscribe: true);

        Assert.Null(State(dir));
        Assert.Contains(_notifier.Messages, m => m.Contains("rétention", StringComparison.OrdinalIgnoreCase)
                                                 || m.Contains("retention", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(_notifier.Messages, m => m.Contains("corrigez le modèle"));
    }

    [Fact]
    public async Task NoUsableAudio_KeepsAnErrorBadgeAsItWas()
    {
        var dir = Session("PurgeErr", audio: false, status: "error\n1\n0\n");
        _transcriber.Throw = new InvalidOperationException("No usable audio track.");

        await _sm.ProcessAsync(dir, forceTranscribe: true);

        Assert.Equal("error", State(dir));
    }

    [Fact]
    public async Task FirstTranscription_TerminalFailure_StillFlagsError()
    {
        var dir = Session("Nouveau");
        _transcriber.Throw = new MistralApiException(403, "tier", 1);

        await _sm.ProcessAsync(dir);

        Assert.Equal("error", State(dir));
    }

    [Fact]
    public void CanFallback_OnContextOverflow_EvenFor400()
    {
        var on = new AppSettings { AutoFallbackProvider = true };
        Assert.False(SessionManager.CanFallback(new MistralApiException(400, "bad request", 1), on));
        Assert.True(SessionManager.CanFallback(new OverflowException400(), on));
        Assert.True(SessionManager.CanFallback(new MistralApiException(429, "rate", 1), on));
        Assert.False(SessionManager.CanFallback(new OverflowException400(), new AppSettings { AutoFallbackProvider = false }));
    }

    [Fact]
    public void IsNoAudio_MatchesTheTranscriberMessage()
    {
        Assert.True(SessionManager.IsNoAudio(new InvalidOperationException("No usable audio track.")));
        Assert.True(SessionManager.IsNoAudio(new AudioPurgedException()));
        Assert.True(SessionManager.IsNoAudio(new Exception("wrap", new InvalidOperationException("No usable audio track."))));
        Assert.False(SessionManager.IsNoAudio(new MistralApiException(400, "invalid model", 1)));
    }

    // ── startup recovery of interrupted pipelines ────────────────────────────────

    private static string Stale(string state) => $"{state}\n999999\n{DateTimeOffset.Now.ToUnixTimeSeconds()}\n";

    [Fact]
    public void Startup_InterruptedRunWithAudio_IsQueuedAgain()
    {
        var dir = Session("Coupe", status: Stale("running"));

        _sm.RecoverInterrupted(isEchoAlive: _ => false);

        Assert.NotNull(_sm.Queue.Get(dir));
        Assert.True(_sm.Queue.Get(dir)!.NextAtEpoch <= DateTimeOffset.Now.ToUnixTimeSeconds());
        Assert.Equal("waiting", State(dir));
        Assert.Equal(Environment.ProcessId, SessionStatus.Read(dir)!.Pid);
    }

    [Fact]
    public void Startup_InterruptedRunWithoutAudio_IsFlaggedError()
    {
        var dir = Session("SansAudio", audio: false, status: Stale("running"));

        _sm.RecoverInterrupted(isEchoAlive: _ => false);

        Assert.Null(_sm.Queue.Get(dir));
        Assert.Equal("error", State(dir));
    }

    [Fact]
    public void Startup_RunOfAnotherLiveEcho_IsLeftAlone()
    {
        var dir = Session("Autre", status: Stale("running"));

        _sm.RecoverInterrupted(isEchoAlive: _ => true);

        Assert.Null(_sm.Queue.Get(dir));
        Assert.Equal(999999, SessionStatus.Read(dir)!.Pid);
    }

    [Fact]
    public void Startup_InterruptedRunWithEverythingProduced_JustLosesTheBadge()
    {
        var dir = Session("Complet", transcript: true, status: Stale("running"));
        File.WriteAllText(Path.Combine(dir, "Complet - Minutes.md"), "# CR");
        File.WriteAllText(Path.Combine(dir, "resume.md"), "résumé");

        _sm.RecoverInterrupted(isEchoAlive: _ => false);

        Assert.Null(_sm.Queue.Get(dir));
        Assert.Null(State(dir));
    }

    // ── busy / stopping ─────────────────────────────────────────────────────────

    [Fact]
    public void Idle_IsNotBusy()
    {
        Assert.False(_sm.IsBusy);
        Assert.False(_sm.IsStopping);
        Assert.Equal(0, _sm.PipelinesInFlight);
    }

    // ── fakes ───────────────────────────────────────────────────────────────────

    /// <summary>A 400 "prompt too long for this model": the one 400 another provider can absorb.</summary>
    private sealed class OverflowException400 : LlmApiException
    {
        public OverflowException400() : base("context length exceeded") { }
        public override int StatusCode => 400;
        public override string? ServerMessage => "context length exceeded";
        public override int Attempts => 1;
        public override bool IsContextOverflow => true;
        public override string UserMessage => "transcript trop long pour ce modèle";
    }

    private sealed class FakeNotifier : INotifier
    {
        public List<string> Messages { get; } = new();
        public void Notify(string message) { lock (Messages) Messages.Add(message); }
    }

    private sealed class FakeTranscriber : ITranscriber
    {
        public Exception? Throw { get; set; }
        public int Calls { get; private set; }
        public Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, IProgress<string>? log, CancellationToken ct)
        {
            Calls++;
            if (Throw is not null) throw Throw;
            var name = Path.GetFileName(request.SessionDir);
            var raw = Path.Combine(request.SessionDir, $"{name} - Transcript.txt");
            File.WriteAllText(raw, "[00:00] A : bonjour");
            return Task.FromResult(new TranscriptionResult(raw, raw, Array.Empty<TranscriptTurn>()));
        }
    }

    private sealed class FakeMinutes : IMinutesGenerator
    {
        public Task<MinutesResult> GenerateAsync(MinutesRequest request, IProgress<string>? log, CancellationToken ct)
        {
            var p = Path.Combine(request.SessionDir, "CR - Minutes.md");
            File.WriteAllText(p, "# CR");
            return Task.FromResult(new MinutesResult(new[] { p }));
        }
    }

    private sealed class FakeSummarizer : ISummarizer
    {
        public Task<string> SummarizeAsync(string sessionDir, string apiKey, AppSettings settings, CancellationToken ct)
        {
            File.WriteAllText(Path.Combine(sessionDir, "resume.md"), "résumé");
            return Task.FromResult("résumé");
        }
    }

    private sealed class FakeVocab : IVocabulary
    {
        public IReadOnlyList<string> BuildContextBias(AppSettings settings, string? missionHint = null) => Array.Empty<string>();
        public string CanonicalizeDeterministic(string text) => text;
    }

    private sealed class FakeTeams : ITeamsMonitor
    {
        public bool InMeeting() => false;
        public MeetingInfo ReadMeetingInfo(TimeSpan timeout) => throw new NotSupportedException();
        public Task WatchAsync(string sessionDir, CaptureClock clock, CancellationToken ct) => Task.CompletedTask;
        public IReadOnlyList<int> GetTeamsPids() => Array.Empty<int>();
    }

    /// <summary>Only the Mistral key exists (no provider fallback possible).</summary>
    private sealed class FakeSecret : ISecretStore
    {
        public string? ResolveKey() => "test-key";
        public bool KeyPresent() => true;
        public void Save(string key) { }
        public void Clear() { }
        public string? ResolveGoogleKey() => null;
        public bool GoogleKeyPresent() => false;
        public void SaveGoogle(string key) { }
        public void ClearGoogle() { }
        public string? ResolveCloudflareKey() => null;
        public bool CloudflareKeyPresent() => false;
        public void SaveCloudflare(string key) { }
        public void ClearCloudflare() { }
        public string? ResolveOpenRouterKey() => null;
        public bool OpenRouterKeyPresent() => false;
        public void SaveOpenRouter(string key) { }
        public void ClearOpenRouter() { }
        public string? ResolveKeyFor(string? provider) => provider is null or "" or "mistral" ? "test-key" : null;
        public bool KeyPresentFor(string? provider) => ResolveKeyFor(provider) is not null;
    }
}
