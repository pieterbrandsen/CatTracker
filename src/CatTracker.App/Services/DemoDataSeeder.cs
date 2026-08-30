using System.Diagnostics;
using CatTracker.App.Readers;
using CatTracker.Core;
using CatTracker.Data;
using Microsoft.Extensions.Options;

namespace CatTracker.App.Services;

/// <summary>
/// Fills an empty replay database with a fortnight of synthetic history, pushed through the very
/// same <see cref="FixProcessor"/> the real collector uses. The point is not a pretty demo: it
/// means the history views, the excursion logic and every statistic are exercised on day one,
/// on Windows, long before the Mac exists.
/// </summary>
public sealed class DemoDataSeeder(
    Repository repository,
    FixProcessor processor,
    IOptions<AppOptions> options,
    ILogger<DemoDataSeeder> logger)
{
    public async Task<int> SeedIfRequestedAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (settings.FindMy.Source != FindMySource.Replay || settings.Replay.SeedDays <= 0)
            return 0;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var tag = repository.GetOrCreateTag(
            ReplayConstants.SerialNumber, settings.Replay.PetName, now);

        var (existing, _, _) = repository.FixSummary(tag.Id);
        if (existing > 0)
        {
            logger.LogInformation("Replay database already has {Count} fixes; not seeding.", existing);
            return 0;
        }

        if (repository.HomeZone() is null)
        {
            repository.InsertZone(new Zone
            {
                Name = "Home",
                Kind = ZoneKind.Home,
                CenterLat = settings.Replay.HomeLat,
                CenterLon = settings.Replay.HomeLon,
                RadiusM = 30,
                ExitBufferM = 25,
            });
        }

        var start = now - settings.Replay.SeedDays * 86_400_000L;
        var simulator = new CatSimulator(
            settings.Replay.HomeLat, settings.Replay.HomeLon, settings.Replay.Seed, start);

        var stopwatch = Stopwatch.StartNew();
        var inserted = 0;

        foreach (var sim in simulator.Advance(now))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var stored = repository.TryInsertFix(new Fix
            {
                TagId = tag.Id,
                TimestampUtc = sim.TimestampUtcMs,
                Latitude = sim.Lat,
                Longitude = sim.Lon,
                HorizontalAccuracy = sim.Accuracy,
                Altitude = 0,
                PositionType = "crowdsourced",
                IsOld = sim.IsOld,
                IsInaccurate = sim.IsInaccurate,
                BatteryStatus = sim.BatteryStatus,
                IngestedUtc = now,
            });

            if (stored is null) continue;

            // notify: false — nobody wants fourteen days of backdated "she's out!" notifications.
            await processor.ProcessAsync(tag, stored, notify: false, cancellationToken);
            inserted++;
        }

        logger.LogInformation(
            "Seeded {Count} synthetic fixes over {Days} days in {Elapsed}.",
            inserted, settings.Replay.SeedDays, stopwatch.Elapsed);

        return inserted;
    }
}
