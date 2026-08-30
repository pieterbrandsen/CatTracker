using CatTracker.App;
using CatTracker.App.Alerting;
using CatTracker.App.Readers;
using CatTracker.App.Services;
using CatTracker.Core;
using CatTracker.Data;
using Microsoft.Extensions.Options;

namespace CatTracker.Tests.App;

/// <summary>
/// Exercises the path a fix actually takes: stored, geofenced, turned into an excursion and
/// alerted on. These are the tests that would catch a 3am false alarm before you get one.
/// </summary>
public class PipelineTests : IDisposable
{
    private const long Minute = 60_000;

    private readonly TestDatabase _db = new();
    private readonly RecordingChannel _channel = new();
    private readonly IOptions<AppOptions> _options;
    private readonly FixProcessor _processor;
    private readonly Repository _repo;

    public PipelineTests()
    {
        _repo = _db.Repository;
        _options = Build.Options(o =>
        {
            o.Geofence.ConfirmationFixes = 1; // confirmation itself is covered in GeofenceEngineTests
            o.FindMy.Source = FindMySource.Replay;
            o.Replay.SeedDays = 0;
        });

        var dispatcher = new AlertDispatcher(
            _repo, [_channel], _options, Build.Logger<AlertDispatcher>());

        _processor = new FixProcessor(_repo, dispatcher, _options, Build.Logger<FixProcessor>());
    }

    public void Dispose() => _db.Dispose();

    private async Task<ProcessOutcome> Feed(
        Tag tag, long at, double metres, double accuracy = 10, int? battery = 1)
    {
        var stored = _repo.TryInsertFix(
            Build.FixAt(tag.Id, at, metres, accuracy: accuracy, battery: battery));

        return await _processor.ProcessAsync(tag, stored!, notify: true, CancellationToken.None);
    }

    private Tag SetUpHome()
    {
        _repo.InsertZone(Build.HomeZone());
        return _repo.GetOrCreateTag("SER1", "Pluis", 0);
    }

    // ---- excursions --------------------------------------------------------------------------

    [Fact]
    public async Task LeavingHome_OpensAnExcursionAndAlerts()
    {
        var tag = SetUpHome();

        await Feed(tag, 0, 5);
        var outcome = await Feed(tag, Minute, 300);

        Assert.True(outcome.ExcursionStarted);
        Assert.Equal(ZoneEventType.Exit, Assert.Single(outcome.Events).EventType);

        var open = _repo.OpenExcursion(tag.Id);
        Assert.NotNull(open);
        Assert.Equal(Minute, open.DepartedUtc);
        Assert.Contains("has left Home", Assert.Single(_channel.Sent).Message);
    }

    [Fact]
    public async Task ComingHome_ClosesTheExcursionWithItsStatistics()
    {
        var tag = SetUpHome();

        await Feed(tag, 0, 5);
        await Feed(tag, Minute, 300);
        await Feed(tag, 2 * Minute, 420);
        var outcome = await Feed(tag, 3 * Minute, 5);

        Assert.True(outcome.ExcursionEnded);
        Assert.Null(_repo.OpenExcursion(tag.Id));

        var excursion = Assert.Single(_repo.GetExcursions(tag.Id, 0, 10 * Minute));
        Assert.Equal(3 * Minute, excursion.ReturnedUtc);
        Assert.Equal(3, excursion.FixCount);
        Assert.Equal(420, excursion.MaxDistanceM, 0);
        Assert.Equal(1.0, excursion.CoverageRatio, 3);
    }

    [Fact]
    public async Task AnExcursionWithHugeGaps_ReportsLowCoverage()
    {
        var tag = SetUpHome();

        await Feed(tag, 0, 5);
        await Feed(tag, Minute, 300);
        await Feed(tag, 300 * Minute, 5); // five hours of silence in the middle

        var excursion = Assert.Single(_repo.GetExcursions(tag.Id, 0, 400 * Minute));

        // We have no idea what she did for most of that; the number must say so.
        Assert.True(excursion.CoverageRatio < 0.2);
    }

    [Fact]
    public async Task AnOpenExcursion_KeepsItsMaxDistanceUpToDate()
    {
        var tag = SetUpHome();

        await Feed(tag, 0, 5);
        await Feed(tag, Minute, 200);
        Assert.Equal(200, _repo.OpenExcursion(tag.Id)!.MaxDistanceM, 0);

        await Feed(tag, 2 * Minute, 450);
        Assert.Equal(450, _repo.OpenExcursion(tag.Id)!.MaxDistanceM, 0);
    }

    [Fact]
    public async Task WithoutAHomeZone_NoExcursionsAreCreated()
    {
        var tag = _repo.GetOrCreateTag("SER1", "Pluis", 0);

        await Feed(tag, 0, 5);
        var outcome = await Feed(tag, Minute, 300);

        Assert.False(outcome.ExcursionStarted);
        Assert.Null(_repo.OpenExcursion(tag.Id));
    }

    [Fact]
    public async Task AStrayOpenExcursion_IsClosedRatherThanDuplicated()
    {
        var tag = SetUpHome();
        _repo.InsertExcursion(new Excursion { TagId = tag.Id, DepartedUtc = 0 });

        await Feed(tag, 0, 5);
        await Feed(tag, Minute, 300);

        // Two open excursions would corrupt every daily total from here on.
        Assert.Equal(2, _repo.GetExcursions(tag.Id, -1, 10 * Minute).Count);
        Assert.Equal(Minute, _repo.OpenExcursion(tag.Id)!.DepartedUtc);
    }

    [Fact]
    public async Task AVagueFix_CannotTriggerAnExit()
    {
        var tag = SetUpHome();

        await Feed(tag, 0, 5);
        var outcome = await Feed(tag, Minute, 300, accuracy: 400);

        Assert.Empty(outcome.Events);
        Assert.Null(_repo.OpenExcursion(tag.Id));
    }

    [Fact]
    public async Task ZoneNotifications_CanBeTurnedOff()
    {
        var zone = Build.HomeZone();
        zone.NotifyOnExit = false;
        _repo.InsertZone(zone);

        var tag = _repo.GetOrCreateTag("SER1", "Pluis", 0);
        await Feed(tag, 0, 5);
        await Feed(tag, Minute, 300);

        Assert.Empty(_channel.Sent);
        // The event is still recorded even when it is not shouted about.
        Assert.Single(_repo.RecentZoneEvents(tag.Id));
    }

    // ---- battery -----------------------------------------------------------------------------

    [Fact]
    public async Task TheFirstBatteryReading_IsJustABaseline()
    {
        var tag = SetUpHome();
        await Feed(tag, 0, 5, battery: 2);

        Assert.Empty(_channel.Sent);
    }

    [Fact]
    public async Task AChangeInBatteryStatus_Alerts()
    {
        var tag = SetUpHome();

        await Feed(tag, 0, 5, battery: 1);
        await Feed(tag, Minute, 5, battery: 3);

        var alert = Assert.Single(_channel.Sent);
        Assert.Equal(AlertKind.LowBattery, alert.Kind);
        Assert.Contains("from 1 to 3", alert.Message);
        Assert.Contains("CR2032", alert.Message);
    }

    [Fact]
    public async Task AMissingBatteryReading_IsIgnored()
    {
        var tag = SetUpHome();

        await Feed(tag, 0, 5, battery: 1);
        await Feed(tag, Minute, 5, battery: null);

        Assert.Empty(_channel.Sent);
    }

    // ---- ingestion ---------------------------------------------------------------------------

    private Ingestor NewIngestor() =>
        new(_repo, _processor, Build.Logger<Ingestor>());

    /// <summary>
    /// A realistic epoch. Small values would be promoted from seconds to milliseconds by the
    /// parser's unit guard, which is correct behaviour but makes for confusing assertions.
    /// </summary>
    private const long Epoch = 1_756_000_000_000;

    // Placeholders rather than interpolation: JSON is all braces, and raw-string interpolation
    // turns every literal }} into an escape.
    private static string Payload(long offsetMs) =>
        """
        [{"name":"Pluis","serialNumber":"HK1","batteryStatus":1,
          "location":{"latitude":52.0907,"longitude":5.1214,"timeStamp":__TS__,
                      "horizontalAccuracy":10,"isOld":false,"isInaccurate":false}}]
        """.Replace("__TS__", (Epoch + offsetMs).ToString());

    [Fact]
    public async Task Ingest_CreatesTheTagAndStoresTheFix()
    {
        var result = await NewIngestor().IngestAsync(Payload(5000), true, CancellationToken.None);

        Assert.Equal(1, result.ItemsSeen);
        Assert.Equal(1, result.NewFixes);
        Assert.Empty(result.Warnings);

        var tag = Assert.Single(_repo.ListTags());
        Assert.Equal("HK1", tag.SerialNumber);
        Assert.Equal(Epoch + 5000, _repo.LatestFix(tag.Id)!.TimestampUtc);
    }

    [Fact]
    public async Task Ingest_IsIdempotent()
    {
        var ingestor = NewIngestor();

        await ingestor.IngestAsync(Payload(5000), true, CancellationToken.None);
        var again = await ingestor.IngestAsync(Payload(5000), true, CancellationToken.None);

        // Find My hands us the same position on every poll; only new timestamps count.
        Assert.Equal(0, again.NewFixes);
        Assert.Equal(1, _repo.FixSummary(_repo.ListTags()[0].Id).Count);
    }

    [Fact]
    public async Task Ingest_SurfacesParserWarnings()
    {
        var result = await NewIngestor().IngestAsync("[]", true, CancellationToken.None);

        Assert.Equal(0, result.NewFixes);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public async Task Ingest_SkipsDeactivatedTags()
    {
        var ingestor = NewIngestor();
        await ingestor.IngestAsync(Payload(5000), true, CancellationToken.None);

        var tag = _repo.ListTags()[0];
        _repo.UpdateTag(tag.Id, tag.PetName, isActive: false);

        var result = await ingestor.IngestAsync(Payload(9000), true, CancellationToken.None);

        Assert.Equal(1, result.ItemsSeen);
        Assert.Equal(0, result.NewFixes);
    }

    [Fact]
    public async Task Ingest_HandlesAnItemWithNoLocation()
    {
        var result = await NewIngestor()
            .IngestAsync("""[{"serialNumber":"HK1","name":"Pluis"}]""", true, CancellationToken.None);

        Assert.Equal(1, result.ItemsSeen);
        Assert.Equal(0, result.NewFixes);
        Assert.Single(_repo.ListTags());
    }

    // ---- demo seeding ------------------------------------------------------------------------

    [Fact]
    public async Task Seeder_FillsAnEmptyReplayDatabaseAndThenLeavesItAlone()
    {
        var options = Build.Options(o =>
        {
            o.FindMy.Source = FindMySource.Replay;
            o.Replay.SeedDays = 2;
        });

        var dispatcher = new AlertDispatcher(_repo, [_channel], options, Build.Logger<AlertDispatcher>());
        var processor = new FixProcessor(_repo, dispatcher, options, Build.Logger<FixProcessor>());
        var seeder = new DemoDataSeeder(_repo, processor, options, Build.Logger<DemoDataSeeder>());

        var inserted = await seeder.SeedIfRequestedAsync(CancellationToken.None);

        Assert.True(inserted > 50, $"expected a couple of days of fixes, got {inserted}");
        Assert.NotNull(_repo.HomeZone());
        Assert.NotEmpty(_repo.GetExcursions(_repo.ListTags()[0].Id, 0, long.MaxValue));

        // Backdated history must neither fire notifications nor leave an alert log of things that
        // never happened — every one of them would be stamped with today's date.
        Assert.Empty(_channel.Sent);
        Assert.Empty(_repo.RecentAlerts());

        Assert.Equal(0, await seeder.SeedIfRequestedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Seeder_DoesNothingForARealSource()
    {
        var options = Build.Options(o => o.FindMy.Source = FindMySource.Spool);
        var dispatcher = new AlertDispatcher(_repo, [], options, Build.Logger<AlertDispatcher>());
        var processor = new FixProcessor(_repo, dispatcher, options, Build.Logger<FixProcessor>());
        var seeder = new DemoDataSeeder(_repo, processor, options, Build.Logger<DemoDataSeeder>());

        Assert.Equal(0, await seeder.SeedIfRequestedAsync(CancellationToken.None));
    }
}
