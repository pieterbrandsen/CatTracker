using System.Collections.Concurrent;
using CatTracker.App.Alerting;
using CatTracker.Core;
using CatTracker.Data;
using Microsoft.Extensions.Options;

namespace CatTracker.App.Services;

public sealed record ProcessOutcome(
    IReadOnlyList<ZoneEvent> Events,
    bool ExcursionStarted,
    bool ExcursionEnded);

/// <summary>
/// Everything that happens to a newly stored fix: geofence evaluation, excursion bookkeeping,
/// battery watching and alerting.
///
/// Kept out of the background service on purpose, so the history seeder can drive the identical
/// path over synthetic data and the tests can drive it over hand-built fixes.
/// </summary>
public sealed class FixProcessor(
    Repository repository,
    AlertDispatcher alerts,
    IOptions<AppOptions> options,
    ILogger<FixProcessor> logger)
{
    private readonly ConcurrentDictionary<long, int> _lastBattery = new();

    public async Task<ProcessOutcome> ProcessAsync(
        Tag tag, Fix fix, bool notify, CancellationToken cancellationToken)
    {
        var events = new List<ZoneEvent>();
        var started = false;
        var ended = false;

        await CheckBatteryAsync(tag, fix, notify, cancellationToken);

        var zones = repository.ListZones();
        var home = zones.FirstOrDefault(z => z.Kind == ZoneKind.Home);
        var geofence = options.Value.Geofence.ToCore();

        foreach (var zone in zones)
        {
            var state = repository.GetZoneState(tag.Id, zone.Id);
            var outcome = GeofenceEngine.Observe(zone, state, fix, geofence);
            state.UpdatedUtc = fix.TimestampUtc;
            repository.SaveZoneState(state);

            // Every decision, including every rejection and why. Turn CatTracker up to Debug and
            // this is the log that answers "why did it not tell me she had gone out?" — or the
            // more common "why did it wake me at 3am when she was on the sofa?"
            logger.LogDebug(
                "Geofence {Zone} for {Pet}: {Verdict} at {Distance:F0}m " +
                "(radius {Radius:F0}m, buffer {Buffer:F0}m, accuracy {Accuracy}m) -> {State}, pending {Pending}",
                // ToString() on the enums: Serilog renders bare enums as quoted JSON scalars,
                // and these are the lines you actually read at 3am.
                zone.Name, tag.PetName, outcome.Verdict.ToString(), outcome.DistanceM,
                zone.RadiusM, zone.ExitBufferM,
                fix.HorizontalAccuracy is { } a ? Math.Round(a) : (object)"?",
                outcome.State.ToString(), outcome.PendingCount);

            if (outcome.Event is not { } eventType) continue;

            logger.LogInformation(
                "{Pet} {Event} zone {Zone} at {Distance:F0}m from its centre.",
                tag.PetName, eventType == ZoneEventType.Enter ? "entered" : "left",
                zone.Name, outcome.DistanceM);

            var zoneEvent = new ZoneEvent
            {
                TagId = tag.Id,
                ZoneId = zone.Id,
                EventType = eventType,
                FixId = fix.Id,
                OccurredUtc = fix.TimestampUtc,
            };
            zoneEvent.Id = repository.InsertZoneEvent(zoneEvent);
            events.Add(zoneEvent);

            // notify: false means backfill — the seeder replaying synthetic history. Those events
            // happened in the past, so they get no alert record at all; stamping a fortnight of
            // "she has left Home" with today's timestamp would be a log of things that never
            // happened. A zone with notifications switched off is different: that alert is still
            // recorded, just not delivered.
            if (notify)
            {
                var wanted = eventType == ZoneEventType.Enter ? zone.NotifyOnEnter : zone.NotifyOnExit;
                var verb = eventType == ZoneEventType.Enter ? "is back in" : "has left";

                await alerts.RaiseAsync(
                    eventType == ZoneEventType.Enter ? AlertKind.ZoneEnter : AlertKind.ZoneExit,
                    $"{tag.PetName} {verb} {zone.Name}.",
                    $"zone:{zone.Id}:{eventType}",
                    // The state machine already guarantees transitions alternate, so no real
                    // cooldown is needed here — only a guard against a pathological restart loop.
                    TimeSpan.FromMinutes(1),
                    wanted,
                    cancellationToken);
            }

            if (home is null || zone.Id != home.Id) continue;

            if (eventType == ZoneEventType.Exit)
            {
                StartExcursion(tag, home, fix);
                started = true;
            }
            else
            {
                ended = EndExcursion(tag, home, fix);
            }
        }

        if (home is not null && !started && !ended) RefreshOpenExcursion(tag, home, fix);

        return new ProcessOutcome(events, started, ended);
    }

    private async Task CheckBatteryAsync(
        Tag tag, Fix fix, bool notify, CancellationToken cancellationToken)
    {
        if (fix.BatteryStatus is not { } battery) return;

        if (notify && _lastBattery.TryGetValue(tag.Id, out var previous) && previous != battery)
        {
            var low = battery >= options.Value.Alerts.LowBatteryAtOrAbove;
            var suffix = low ? " — time to buy a CR2032." : "";

            await alerts.RaiseAsync(
                AlertKind.LowBattery,
                $"{tag.PetName}'s AirTag battery status changed from {previous} to {battery}{suffix}",
                $"battery:{tag.Id}",
                TimeSpan.FromHours(12),
                cancellationToken: cancellationToken);
        }

        _lastBattery[tag.Id] = battery;
    }

    private void StartExcursion(Tag tag, Zone home, Fix fix)
    {
        // Defensive: if one is somehow already open, close it at this fix rather than leaking a
        // second open row that would corrupt every daily total from here on.
        var existing = repository.OpenExcursion(tag.Id);
        if (existing is not null)
        {
            logger.LogWarning(
                "Excursion {Id} was still open when a new departure was detected; closing it.",
                existing.Id);
            existing.ReturnedUtc = fix.TimestampUtc;
            repository.UpdateExcursion(existing);
        }

        var id = repository.InsertExcursion(new Excursion
        {
            TagId = tag.Id,
            DepartedUtc = fix.TimestampUtc,
            FixCount = 1,

            // Seed the statistics from the departure fix itself. Leaving these at zero until the
            // next fix arrives would have the live view claim "out, max 0 m from home" for the
            // first several minutes of every excursion.
            MaxDistanceM = GeoMath.DistanceM(
                fix.Latitude, fix.Longitude, home.CenterLat, home.CenterLon),
            CoverageRatio = 1,
        });

        logger.LogInformation(
            "Excursion {Id} opened for {Pet} at {At:u}.", id, tag.PetName, fix.At.UtcDateTime);
    }

    private bool EndExcursion(Tag tag, Zone home, Fix fix)
    {
        var open = repository.OpenExcursion(tag.Id);
        if (open is null) return false;

        open.ReturnedUtc = fix.TimestampUtc;
        Summarise(open, home, open.DepartedUtc, fix.TimestampUtc);
        repository.UpdateExcursion(open);

        logger.LogInformation(
            "Excursion {Id} closed for {Pet}: out {Duration}, max {Distance:F0}m from home, " +
            "{Fixes} fixes, {Coverage:P0} of it observed.",
            open.Id, tag.PetName,
            TimeSpan.FromMilliseconds(fix.TimestampUtc - open.DepartedUtc),
            open.MaxDistanceM, open.FixCount, open.CoverageRatio);

        return true;
    }

    private void RefreshOpenExcursion(Tag tag, Zone home, Fix fix)
    {
        var open = repository.OpenExcursion(tag.Id);
        if (open is null) return;

        Summarise(open, home, open.DepartedUtc, fix.TimestampUtc);
        repository.UpdateExcursion(open);
    }

    private void Summarise(Excursion excursion, Zone home, long fromUtc, long toUtc)
    {
        var fixes = repository.GetFixes(excursion.TagId, fromUtc, toUtc);
        excursion.FixCount = fixes.Count;
        excursion.MaxDistanceM = fixes.Count == 0
            ? 0
            : fixes.Max(f => GeoMath.DistanceM(
                f.Latitude, f.Longitude, home.CenterLat, home.CenterLon));
        excursion.CoverageRatio = Stats.CoverageRatio(fixes, fromUtc, toUtc);
    }
}
