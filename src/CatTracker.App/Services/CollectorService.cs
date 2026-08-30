using CatTracker.App.Alerting;
using CatTracker.App.Readers;
using CatTracker.Core;
using CatTracker.Data;
using Microsoft.Extensions.Options;

namespace CatTracker.App.Services;

/// <summary>
/// The poll loop. Reads whatever the configured source offers, stores anything new, and — just as
/// importantly — notices when nothing is arriving.
/// </summary>
public sealed class CollectorService(
    IFindMyReader reader,
    Ingestor ingestor,
    Repository repository,
    AlertDispatcher alerts,
    CollectorState state,
    DemoDataSeeder seeder,
    IOptions<AppOptions> options,
    ILogger<CollectorService> logger) : BackgroundService
{
    private int _pollCount;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        state.SourceDescription = reader.Description;
        logger.LogInformation("Collector starting. Source: {Source}", reader.Description);

        try
        {
            await seeder.SeedIfRequestedAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Demo seeding failed; continuing without it.");
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.FindMy.PollSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAsync(stoppingToken);
                state.LastError = null;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The collector must outlive every individual failure. A crashed poll loop is the
                // one bug that would leave you thinking the cat never moved.
                state.LastError = ex.Message;
                logger.LogError(ex, "Poll failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Collector stopped.");
    }

    internal async Task PollAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        state.LastPollUtc = now;

        var snapshot = await reader.TryReadAsync(cancellationToken);
        if (snapshot is null)
        {
            logger.LogDebug("Poll: source unchanged.");
        }
        else
        {
            state.LastSnapshotUtc = snapshot.CapturedUtcMs;

            var result = await ingestor.IngestAsync(snapshot.Json, notify: true, cancellationToken);
            state.LastWarnings = result.Warnings;

            logger.LogDebug(
                "Poll: {Bytes} bytes, {Items} item(s), {New} new fix(es), {Warnings} warning(s).",
                snapshot.Json.Length, result.ItemsSeen, result.NewFixes, result.Warnings.Count);

            if (result.NewFixes > 0) state.LastNewFixUtc = now;

            if (result.Warnings.Count > 0)
            {
                await alerts.RaiseAsync(
                    AlertKind.ReaderProblem,
                    $"Find My cache had {result.Warnings.Count} problem(s): {result.Warnings[0]}",
                    "parse-warnings",
                    TimeSpan.FromHours(6),
                    cancellationToken: cancellationToken);
            }

            KeepRawSnapshot(snapshot);
        }

        await CheckStalenessAsync(now, cancellationToken);
    }

    private void KeepRawSnapshot(FindMySnapshot snapshot)
    {
        var keep = options.Value.FindMy.KeepRawSnapshots;
        if (keep <= 0) return;

        repository.InsertRawSnapshot(snapshot.CapturedUtcMs, snapshot.Json);

        // Pruning every time would be wasteful; every 50th snapshot keeps the table bounded.
        if (++_pollCount % 50 == 0) repository.PruneRawSnapshots(keep);
    }

    /// <summary>
    /// Silence is the failure mode that matters. A dead reader, a quit Find My app, a sleeping
    /// Mac and a cat asleep on the sofa all look identical on a map — so we say out loud when we
    /// have stopped hearing anything, and try to name the actual cause.
    /// </summary>
    private async Task CheckStalenessAsync(long now, CancellationToken cancellationToken)
    {
        var threshold = TimeSpan.FromMinutes(options.Value.FindMy.StaleAfterMinutes);

        var latest = repository.ListTags()
            .Where(t => t.IsActive)
            .Select(t => repository.LatestFix(t.Id)?.TimestampUtc)
            .Where(t => t is not null)
            .DefaultIfEmpty(null)
            .Max();

        // No data at all yet is a setup state, not a fault; don't nag during first run.
        if (latest is null) return;

        var age = TimeSpan.FromMilliseconds(now - latest.Value);
        var stale = age > threshold;

        if (stale && !state.IsStale)
        {
            var diagnosis = await DiagnoseAsync(now, cancellationToken);
            await alerts.RaiseAsync(
                AlertKind.DataStale,
                $"No new position for {Describe(age)}. {diagnosis}",
                "stale",
                TimeSpan.FromHours(1),
                cancellationToken: cancellationToken);
        }
        else if (!stale && state.IsStale)
        {
            await alerts.RaiseAsync(
                AlertKind.DataStale,
                "Contact restored — positions are arriving again.",
                "stale-recovered",
                TimeSpan.FromMinutes(5),
                cancellationToken: cancellationToken);
        }

        state.IsStale = stale;
    }

    private async Task<string> DiagnoseAsync(long now, CancellationToken cancellationToken)
    {
        var heartbeat = await reader.ReadHeartbeatAsync(cancellationToken);

        if (heartbeat is null)
        {
            return "No heartbeat from cattracker-reader — the reader agent looks dead. " +
                   "Try: launchctl kickstart -k gui/$UID/nl.brandsen.cattracker.reader";
        }

        var heartbeatAge = TimeSpan.FromMilliseconds(now - heartbeat.WrittenUtcMs);
        if (heartbeatAge > TimeSpan.FromMinutes(5))
        {
            return $"cattracker-reader last checked in {Describe(heartbeatAge)} ago; " +
                   "the reader agent has stopped.";
        }

        return heartbeat.Status switch
        {
            ReaderHeartbeat.PermissionDenied =>
                "cattracker-reader cannot read the cache: grant it Full Disk Access.",
            ReaderHeartbeat.NotFound =>
                "The Find My cache file is missing. Has the Find My app ever run on this account?",
            ReaderHeartbeat.Error =>
                $"cattracker-reader reported an error: {heartbeat.Detail}",
            _ =>
                "The reader is healthy, so Find My itself has stopped refreshing. Check that the " +
                "Find My app is running and the Mac is awake.",
        };
    }

    private static string Describe(TimeSpan span) =>
        span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : $"{(int)span.TotalMinutes}m";
}
