using Echo.Audio;
using L = Echo.Audio.AudioCaptureService.TrackLiveness;

namespace Echo.Tests;

/// <summary>
/// <see cref="AudioCaptureService.EvaluateHealth"/>: only REAL failures, all frame-based.
/// A silent mic (user listening without speaking) must never raise anything.
/// </summary>
public class AudioHealthTests
{
    private const double Now = 10_000.0;

    private static L Fresh(bool continuous = true) => new(Now - 1.0, Now - 600.0, continuous);
    private static L Stalled(double seconds, bool continuous = true) => new(Now - seconds, Now - 600.0, continuous);

    [Fact]
    public void Healthy_IsEmpty()
    {
        Assert.Equal("", AudioCaptureService.EvaluateHealth(false, Fresh(), Fresh(), Now, false));
        Assert.Equal("", AudioCaptureService.EvaluateHealth(true, Fresh(), null, Now, false));
    }

    [Fact]
    public void LoopbackNotOpened_IsLoopbackDead_ButNotInPhysicalMode()
    {
        Assert.Equal("loopback_dead", AudioCaptureService.EvaluateHealth(false, Fresh(), null, Now, false));
        Assert.Equal("", AudioCaptureService.EvaluateHealth(true, Fresh(), null, Now, false));
    }

    [Fact]
    public void LoopbackWithoutFrames_IsDeadAfter20s_WhenContinuous()
    {
        Assert.Equal("", AudioCaptureService.EvaluateHealth(false, Fresh(), Stalled(15), Now, false));
        Assert.Equal("loopback_dead", AudioCaptureService.EvaluateHealth(false, Fresh(), Stalled(25), Now, false));
        // A stream that may legitimately pause (no keep-alive / process loopback): 120 s.
        Assert.Equal("", AudioCaptureService.EvaluateHealth(false, Fresh(), Stalled(60, continuous: false), Now, false));
        Assert.Equal("loopback_dead",
            AudioCaptureService.EvaluateHealth(false, Fresh(), Stalled(130, continuous: false), Now, false));
    }

    [Fact]
    public void NeverDelivered_UsesOpenTimeAsReference()
    {
        var neverYoung = new L(null, Now - 5.0, true);
        var neverOld = new L(null, Now - 30.0, true);
        Assert.Equal("", AudioCaptureService.EvaluateHealth(false, Fresh(), neverYoung, Now, false));
        Assert.Equal("loopback_dead", AudioCaptureService.EvaluateHealth(false, Fresh(), neverOld, Now, false));
        Assert.Equal("no_mic", AudioCaptureService.EvaluateHealth(false, neverOld, Fresh(), Now, false));
    }

    [Fact]
    public void NoMic_WhenAbsentOrFrameless_NeverForSilence()
    {
        Assert.Equal("no_mic", AudioCaptureService.EvaluateHealth(false, null, Fresh(), Now, false));
        Assert.Equal("no_mic", AudioCaptureService.EvaluateHealth(true, null, null, Now, false));
        Assert.Equal("no_mic", AudioCaptureService.EvaluateHealth(false, Stalled(25), Fresh(), Now, false));
        // A mic delivering frames of pure silence is healthy: the rule has no level input at all.
        Assert.Equal("", AudioCaptureService.EvaluateHealth(false, Fresh(), Fresh(), Now, false));
    }

    [Fact]
    public void SilentTeams_OnlyWhenTracksAreAlive()
    {
        Assert.Equal("silent_teams", AudioCaptureService.EvaluateHealth(false, Fresh(), Fresh(), Now, true));
        // A dead loopback takes precedence (the inaudible condition is a consequence).
        Assert.Equal("loopback_dead", AudioCaptureService.EvaluateHealth(false, Fresh(), Stalled(30), Now, true));
        Assert.Equal("", AudioCaptureService.EvaluateHealth(true, Fresh(), null, Now, true));
    }
}
