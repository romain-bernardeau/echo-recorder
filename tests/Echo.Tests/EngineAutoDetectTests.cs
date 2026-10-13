using Echo.Core.Engine;

namespace Echo.Tests;

/// <summary>
/// The Teams auto-detect decisions (AutoDetectPolicy). The first .NET port stopped ANY
/// recording 45 s after Teams stopped reporting a call (in-person meetings, manual
/// recordings with no call at all) and re-offered "Démarrer ?" 5 s after a manual Stop in
/// the middle of a call. Grace = 9 polls (45 s / 5 s) in these tests.
/// </summary>
public sealed class EngineAutoDetectTests
{
    private const int Grace = 9;
    private const string Dir = @"C:\rec\20261007 - 10h00 - Point";

    private static AutoDetectTick T(bool inMeeting, bool recording = false, bool physical = false,
                                    string? dir = Dir, bool autoDetect = true)
        => new(inMeeting, autoDetect, recording, physical, recording ? dir : null, Grace);

    /// <summary>Run <paramref name="n"/> identical polls, returning every action taken.</summary>
    private static List<AutoDetectAction> Run(AutoDetectPolicy p, AutoDetectTick t, int n)
    {
        var acts = new List<AutoDetectAction>();
        for (int i = 0; i < n; i++) acts.Add(p.Step(t));
        return acts;
    }

    [Fact]
    public void OffersOnce_OnTheRisingEdgeOfACall()
    {
        var p = new AutoDetectPolicy();
        Assert.Equal(AutoDetectAction.None, p.Step(T(false)));
        Assert.Equal(AutoDetectAction.OfferStart, p.Step(T(true)));
        // Declined: the call goes on, no new offer.
        Assert.All(Run(p, T(true), 20), a => Assert.Equal(AutoDetectAction.None, a));
    }

    [Fact]
    public void NoOffer_WhenAutoDetectIsOff()
    {
        var p = new AutoDetectPolicy();
        Assert.All(Run(p, T(true, autoDetect: false), 5), a => Assert.Equal(AutoDetectAction.None, a));
    }

    [Fact]
    public void PhysicalMeeting_IsNeverAutoStopped()
    {
        var p = new AutoDetectPolicy();
        // A Teams call shows up while an in-person meeting is being recorded, then ends.
        Run(p, T(true, recording: true, physical: true), 3);
        var acts = Run(p, T(false, recording: true, physical: true), Grace * 3);
        Assert.DoesNotContain(AutoDetectAction.AutoStop, acts);
    }

    [Fact]
    public void ManualRecording_WithNoTeamsCall_IsNeverAutoStopped()
    {
        var p = new AutoDetectPolicy();
        var acts = Run(p, T(false, recording: true), Grace * 4);   // 3 minutes, no call ever seen
        Assert.All(acts, a => Assert.Equal(AutoDetectAction.None, a));
    }

    [Fact]
    public void ManualStopDuringACall_DoesNotBringTheOfferBack()
    {
        var p = new AutoDetectPolicy();
        Assert.Equal(AutoDetectAction.OfferStart, p.Step(T(true)));
        Run(p, T(true, recording: true), 10);                      // accepted, recording
        p.MarkManual();                                            // user presses Stop
        var acts = Run(p, T(true, recording: false), 20);          // still in the call
        Assert.All(acts, a => Assert.Equal(AutoDetectAction.None, a));
    }

    [Fact]
    public void ManualStartDuringACall_ThenStop_NoOfferForThatCall()
    {
        var p = new AutoDetectPolicy();
        p.Step(T(true, autoDetect: false));                        // call seen, no offer (setting off)
        p.MarkManual();                                            // manual start
        Run(p, T(true, recording: true, autoDetect: true), 5);
        p.MarkManual();                                            // manual stop
        Assert.All(Run(p, T(true), 10), a => Assert.Equal(AutoDetectAction.None, a));
    }

    [Fact]
    public void RealEndOfCall_StopsAfterTheGrace_AndTheNextCallIsOfferedAgain()
    {
        var p = new AutoDetectPolicy();
        Assert.Equal(AutoDetectAction.OfferStart, p.Step(T(true)));
        Run(p, T(true, recording: true), 30);
        var acts = Run(p, T(false, recording: true), Grace);
        Assert.Equal(Enumerable.Repeat(AutoDetectAction.None, Grace - 1).Append(AutoDetectAction.AutoStop), acts);

        // Next meeting, later on: offered again.
        Run(p, T(false), 10);
        Assert.Equal(AutoDetectAction.OfferStart, p.Step(T(true)));
    }

    [Fact]
    public void DetectionBlinkShorterThanTheGrace_ChangesNothing()
    {
        var p = new AutoDetectPolicy();
        p.Step(T(true));
        Run(p, T(true, recording: true), 5);
        var blink = Run(p, T(false, recording: true), Grace - 1);
        Assert.All(blink, a => Assert.Equal(AutoDetectAction.None, a));
        // Teams reports the call again: no stop, no second offer.
        Assert.All(Run(p, T(true, recording: true), 5), a => Assert.Equal(AutoDetectAction.None, a));
        Assert.Equal(0, p.Missed);
    }

    [Fact]
    public void CallSeenDuringAnEarlierRecording_DoesNotStopTheNextOne()
    {
        var p = new AutoDetectPolicy();
        p.Step(T(true));
        Run(p, T(true, recording: true, dir: @"C:\rec\A"), 5);
        // The user stops A and immediately starts a manual recording B, then the call ends.
        var acts = Run(p, T(false, recording: true, dir: @"C:\rec\B"), Grace * 2);
        Assert.DoesNotContain(AutoDetectAction.AutoStop, acts);
    }

    [Fact]
    public void MarkManual_OutsideACall_KeepsTheNextOffer()
    {
        var p = new AutoDetectPolicy();
        p.MarkManual();                                            // start/stop with no call around
        p.Step(T(false));
        Assert.Equal(AutoDetectAction.OfferStart, p.Step(T(true)));
    }

    [Theory]
    [InlineData(45, 5, 9)]
    [InlineData(46, 5, 10)]
    [InlineData(0, 5, 1)]
    [InlineData(3, 5, 1)]
    public void GraceTicks_RoundsUp_AtLeastOne(int grace, int poll, int expected)
        => Assert.Equal(expected, AutoDetectPolicy.GraceTicks(grace, poll));
}
