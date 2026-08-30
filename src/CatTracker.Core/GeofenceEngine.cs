namespace CatTracker.Core;

public enum FenceState { Unknown = 0, Inside = 1, Outside = 2 }

public sealed class GeofenceOptions
{
    /// <summary>
    /// Fixes vaguer than this never move the state machine. An AirTag located by a stranger's
    /// phone two streets away routinely reports 150 m accuracy, which would otherwise place a
    /// sleeping cat outside the garden.
    /// </summary>
    public double MaxAccuracyMeters { get; set; } = 100;

    /// <summary>Consecutive qualifying fixes required before a transition is believed.</summary>
    public int ConfirmationFixes { get; set; } = 2;

    /// <summary>Ignore fixes Find My has flagged as stale.</summary>
    public bool RejectOldFixes { get; set; } = true;
}

/// <summary>Mutable per (tag, zone) state; persisted so a restart does not replay old alerts.</summary>
public sealed class ZoneTrackerState
{
    public long TagId { get; set; }
    public long ZoneId { get; set; }
    public FenceState State { get; set; } = FenceState.Unknown;

    /// <summary>How many consecutive fixes have disagreed with <see cref="State"/> so far.</summary>
    public int PendingCount { get; set; }

    public long UpdatedUtc { get; set; }
}

public enum FixVerdict
{
    Accepted,
    RejectedInaccurate,
    RejectedOld,
    RejectedNoAccuracy,
    InDeadBand,
}

public sealed record GeofenceOutcome(
    FixVerdict Verdict,
    double DistanceM,
    ZoneEventType? Event,
    FenceState State,
    int PendingCount);

/// <summary>
/// Decides when a cat has genuinely left or come home, given a position source that is sparse,
/// irregular and frequently wrong by more than the size of the garden.
///
/// Three independent defences, in order: an accuracy gate, a hysteresis dead band, and an
/// N-consecutive-fix confirmation. A single rogue fix can never raise an alert.
/// </summary>
public static class GeofenceEngine
{
    public static GeofenceOutcome Observe(
        Zone zone, ZoneTrackerState state, Fix fix, GeofenceOptions options)
    {
        var distance = GeoMath.DistanceM(fix.Latitude, fix.Longitude, zone.CenterLat, zone.CenterLon);

        // 1. Accuracy gate. Rejected fixes are still stored and drawn on the map, just greyed out;
        //    they simply get no vote on where the cat is.
        if (fix.IsInaccurate)
            return Reject(FixVerdict.RejectedInaccurate, distance, state);

        if (options.RejectOldFixes && fix.IsOld)
            return Reject(FixVerdict.RejectedOld, distance, state);

        if (fix.HorizontalAccuracy is null || fix.HorizontalAccuracy > options.MaxAccuracyMeters)
            return Reject(FixVerdict.RejectedNoAccuracy, distance, state);

        // 2. Hysteresis. Between Radius and Radius + ExitBuffer we decline to have an opinion,
        //    which is what stops the boundary oscillating.
        FenceState observed;
        if (distance <= zone.RadiusM)
            observed = FenceState.Inside;
        else if (distance > zone.RadiusM + zone.ExitBufferM)
            observed = FenceState.Outside;
        else
            return Reject(FixVerdict.InDeadBand, distance, state);

        // First ever observation: adopt it silently. Alerting here would fire an "escaped!" the
        // moment the collector starts while the cat is asleep on the sofa.
        if (state.State == FenceState.Unknown)
        {
            state.State = observed;
            state.PendingCount = 0;
            return new GeofenceOutcome(FixVerdict.Accepted, distance, null, observed, 0);
        }

        if (observed == state.State)
        {
            state.PendingCount = 0;
            return new GeofenceOutcome(FixVerdict.Accepted, distance, null, state.State, 0);
        }

        // 3. Confirmation. Disagreement has to persist to count.
        state.PendingCount++;
        if (state.PendingCount < Math.Max(1, options.ConfirmationFixes))
        {
            return new GeofenceOutcome(
                FixVerdict.Accepted, distance, null, state.State, state.PendingCount);
        }

        state.State = observed;
        state.PendingCount = 0;
        var evt = observed == FenceState.Inside ? ZoneEventType.Enter : ZoneEventType.Exit;
        return new GeofenceOutcome(FixVerdict.Accepted, distance, evt, observed, 0);
    }

    private static GeofenceOutcome Reject(FixVerdict verdict, double distance, ZoneTrackerState state)
        => new(verdict, distance, null, state.State, state.PendingCount);
}
