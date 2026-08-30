using CatTracker.Core;

namespace CatTracker.Tests.Core;

public class GeofenceEngineTests
{
    private static readonly GeofenceOptions Default = new();

    private static ZoneTrackerState Started(Zone zone, double metres)
    {
        // Prime the machine so tests start from a known Inside/Outside rather than Unknown.
        var state = new ZoneTrackerState();
        GeofenceEngine.Observe(zone, state, Build.FixAt(1, 1000, metres), Default);
        return state;
    }

    [Fact]
    public void FirstObservation_AdoptsSilently()
    {
        var state = new ZoneTrackerState();
        var outcome = GeofenceEngine.Observe(Build.HomeZone(), state, Build.FixAt(1, 0, 5), Default);

        // Alerting here would fire "she escaped!" every time the collector restarts.
        Assert.Null(outcome.Event);
        Assert.Equal(FenceState.Inside, state.State);
    }

    [Fact]
    public void ASingleDistantFix_DoesNotRaiseAnExit()
    {
        var zone = Build.HomeZone();
        var state = Started(zone, 5);

        var outcome = GeofenceEngine.Observe(zone, state, Build.FixAt(1, 2000, 300), Default);

        Assert.Null(outcome.Event);
        Assert.Equal(FenceState.Inside, state.State);
        Assert.Equal(1, state.PendingCount);
    }

    [Fact]
    public void TwoConsecutiveDistantFixes_RaiseAnExit()
    {
        var zone = Build.HomeZone();
        var state = Started(zone, 5);

        GeofenceEngine.Observe(zone, state, Build.FixAt(1, 2000, 300), Default);
        var outcome = GeofenceEngine.Observe(zone, state, Build.FixAt(1, 3000, 320), Default);

        Assert.Equal(ZoneEventType.Exit, outcome.Event);
        Assert.Equal(FenceState.Outside, state.State);
        Assert.Equal(0, state.PendingCount);
    }

    [Fact]
    public void AFixBackInside_ResetsThePendingCount()
    {
        var zone = Build.HomeZone();
        var state = Started(zone, 5);

        GeofenceEngine.Observe(zone, state, Build.FixAt(1, 2000, 300), Default);
        GeofenceEngine.Observe(zone, state, Build.FixAt(1, 3000, 5), Default);
        var outcome = GeofenceEngine.Observe(zone, state, Build.FixAt(1, 4000, 300), Default);

        Assert.Null(outcome.Event);
        Assert.Equal(FenceState.Inside, state.State);
    }

    [Fact]
    public void ComingHome_RaisesAnEnter()
    {
        var zone = Build.HomeZone();
        var state = Started(zone, 400);

        GeofenceEngine.Observe(zone, state, Build.FixAt(1, 2000, 5), Default);
        var outcome = GeofenceEngine.Observe(zone, state, Build.FixAt(1, 3000, 3), Default);

        Assert.Equal(ZoneEventType.Enter, outcome.Event);
        Assert.Equal(FenceState.Inside, state.State);
    }

    [Fact]
    public void TheDeadBand_ProducesNoOpinion()
    {
        // Radius 30, buffer 25: between 30 m and 55 m we decline to decide, which is what stops
        // the boundary oscillating all night.
        var zone = Build.HomeZone();
        var state = Started(zone, 5);

        var outcome = GeofenceEngine.Observe(zone, state, Build.FixAt(1, 2000, 45), Default);

        Assert.Equal(FixVerdict.InDeadBand, outcome.Verdict);
        Assert.Null(outcome.Event);
        Assert.Equal(0, state.PendingCount);
    }

    [Fact]
    public void JustPastTheBuffer_Counts()
    {
        var zone = Build.HomeZone();
        var state = Started(zone, 5);

        var outcome = GeofenceEngine.Observe(zone, state, Build.FixAt(1, 2000, 60), Default);

        Assert.Equal(FixVerdict.Accepted, outcome.Verdict);
        Assert.Equal(1, state.PendingCount);
    }

    [Fact]
    public void VagueFixes_AreRejected()
    {
        var zone = Build.HomeZone();
        var state = Started(zone, 5);

        var outcome = GeofenceEngine.Observe(
            zone, state, Build.FixAt(1, 2000, 300, accuracy: 400), Default);

        Assert.Equal(FixVerdict.RejectedNoAccuracy, outcome.Verdict);
        Assert.Equal(0, state.PendingCount);
    }

    [Fact]
    public void FixesWithNoAccuracyAtAll_AreRejected()
    {
        var zone = Build.HomeZone();
        var state = Started(zone, 5);

        var fix = Build.FixAt(1, 2000, 300);
        fix.HorizontalAccuracy = null;

        Assert.Equal(FixVerdict.RejectedNoAccuracy,
            GeofenceEngine.Observe(zone, state, fix, Default).Verdict);
    }

    [Fact]
    public void FixesFindMyCallsInaccurate_AreRejected()
    {
        var zone = Build.HomeZone();
        var state = Started(zone, 5);

        var outcome = GeofenceEngine.Observe(
            zone, state, Build.FixAt(1, 2000, 300, isInaccurate: true), Default);

        Assert.Equal(FixVerdict.RejectedInaccurate, outcome.Verdict);
    }

    [Fact]
    public void OldFixes_AreRejectedByDefault()
    {
        var zone = Build.HomeZone();
        var state = Started(zone, 5);

        var outcome = GeofenceEngine.Observe(
            zone, state, Build.FixAt(1, 2000, 300, isOld: true), Default);

        Assert.Equal(FixVerdict.RejectedOld, outcome.Verdict);
    }

    [Fact]
    public void OldFixes_CanBeAllowed()
    {
        var zone = Build.HomeZone();
        var options = new GeofenceOptions { RejectOldFixes = false };

        var state = new ZoneTrackerState();
        GeofenceEngine.Observe(zone, state, Build.FixAt(1, 1000, 5), options);

        var outcome = GeofenceEngine.Observe(
            zone, state, Build.FixAt(1, 2000, 300, isOld: true), options);

        Assert.Equal(FixVerdict.Accepted, outcome.Verdict);
    }

    [Fact]
    public void ConfirmationCount_IsConfigurable()
    {
        var zone = Build.HomeZone();
        var options = new GeofenceOptions { ConfirmationFixes = 1 };

        var state = new ZoneTrackerState();
        GeofenceEngine.Observe(zone, state, Build.FixAt(1, 1000, 5), options);
        var outcome = GeofenceEngine.Observe(zone, state, Build.FixAt(1, 2000, 300), options);

        Assert.Equal(ZoneEventType.Exit, outcome.Event);
    }

    [Fact]
    public void RejectedFixes_LeaveTheStateUntouched()
    {
        var zone = Build.HomeZone();
        var state = Started(zone, 5);
        state.PendingCount = 1;

        var outcome = GeofenceEngine.Observe(
            zone, state, Build.FixAt(1, 2000, 300, accuracy: 999), Default);

        Assert.Equal(FenceState.Inside, outcome.State);
        Assert.Equal(1, outcome.PendingCount);
    }

    [Fact]
    public void Distance_IsReportedEvenWhenRejected()
    {
        var zone = Build.HomeZone();
        var state = Started(zone, 5);

        var outcome = GeofenceEngine.Observe(
            zone, state, Build.FixAt(1, 2000, 250, accuracy: 999), Default);

        Assert.Equal(250, outcome.DistanceM, 0);
    }
}
