using CatTracker.Core;
using CatTracker.Data;

namespace CatTracker.Tests.Data;

public class RepositoryTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private Repository Repo => _db.Repository;

    public void Dispose() => _db.Dispose();

    // ---- schema ------------------------------------------------------------------------------

    [Fact]
    public void Migrate_AppliesTheSchema() =>
        Assert.NotEmpty(Repo.AppliedMigrations());

    [Fact]
    public void Migrate_IsIdempotent()
    {
        // Runs on every start, so a second call must be a no-op rather than an error.
        Assert.Empty(DatabaseSetup.Migrate(_db.Factory));
    }

    // ---- tags --------------------------------------------------------------------------------

    [Fact]
    public void GetOrCreateTag_CreatesOnceAndThenReturnsTheSameRow()
    {
        var first = Repo.GetOrCreateTag("SER1", "Pluis", 1000);
        var second = Repo.GetOrCreateTag("SER1", "Pluis", 2000);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("Pluis", second.PetName);
        Assert.True(second.IsActive);
        Assert.Single(Repo.ListTags());
    }

    [Fact]
    public void GetOrCreateTag_PicksUpARenameInFindMy()
    {
        var tag = Repo.GetOrCreateTag("SER1", "Pluis", 1000);
        Repo.UpdateTag(tag.Id, "Pluisje", isActive: true);

        var renamed = Repo.GetOrCreateTag("SER1", "Mr Pluis", 2000);

        Assert.Equal("Mr Pluis", renamed.FindMyName);
        // The name you chose is yours; only the Find My name follows Apple.
        Assert.Equal("Pluisje", Repo.GetTag(tag.Id)!.PetName);
    }

    [Fact]
    public void GetTag_ReturnsNullWhenMissing() => Assert.Null(Repo.GetTag(999));

    [Fact]
    public void UpdateTag_CanDeactivate()
    {
        var tag = Repo.GetOrCreateTag("SER1", "Pluis", 1000);
        Repo.UpdateTag(tag.Id, "Pluis", isActive: false);

        Assert.False(Repo.GetTag(tag.Id)!.IsActive);
    }

    // ---- fixes -------------------------------------------------------------------------------

    [Fact]
    public void TryInsertFix_StoresTheFirstAndRejectsTheDuplicate()
    {
        var tag = Repo.GetOrCreateTag("SER1", "Pluis", 1000);

        var stored = Repo.TryInsertFix(Build.FixAt(tag.Id, 5000, 10));
        var duplicate = Repo.TryInsertFix(Build.FixAt(tag.Id, 5000, 10));

        Assert.NotNull(stored);
        Assert.True(stored.Id > 0);

        // This is the property the whole poll loop depends on: Find My holds only the latest
        // position, so the same fix arrives over and over.
        Assert.Null(duplicate);
        Assert.Equal(1, Repo.FixSummary(tag.Id).Count);
    }

    [Fact]
    public void LatestFix_ReturnsTheNewest()
    {
        var tag = Repo.GetOrCreateTag("SER1", "Pluis", 1000);
        Repo.TryInsertFix(Build.FixAt(tag.Id, 1000, 10));
        Repo.TryInsertFix(Build.FixAt(tag.Id, 9000, 10));
        Repo.TryInsertFix(Build.FixAt(tag.Id, 5000, 10));

        Assert.Equal(9000, Repo.LatestFix(tag.Id)!.TimestampUtc);
    }

    [Fact]
    public void LatestFix_IsNullForAnUnknownTag() => Assert.Null(Repo.LatestFix(42));

    [Fact]
    public void GetFixes_ReturnsTheRangeInOrder()
    {
        var tag = Repo.GetOrCreateTag("SER1", "Pluis", 1000);
        foreach (var t in new long[] { 1000, 2000, 3000, 4000 })
            Repo.TryInsertFix(Build.FixAt(tag.Id, t, 10));

        var fixes = Repo.GetFixes(tag.Id, 2000, 3000);

        Assert.Equal(2, fixes.Count);
        Assert.Equal(2000, fixes[0].TimestampUtc);
        Assert.Equal(3000, fixes[1].TimestampUtc);
    }

    [Fact]
    public void GetFixes_RespectsTheLimit()
    {
        var tag = Repo.GetOrCreateTag("SER1", "Pluis", 1000);
        for (var t = 1000; t < 1010; t++) Repo.TryInsertFix(Build.FixAt(tag.Id, t, 10));

        Assert.Equal(3, Repo.GetFixes(tag.Id, 0, long.MaxValue, limit: 3).Count);
    }

    [Fact]
    public void FixSummary_ReportsCountAndBounds()
    {
        var tag = Repo.GetOrCreateTag("SER1", "Pluis", 1000);
        Repo.TryInsertFix(Build.FixAt(tag.Id, 1000, 10));
        Repo.TryInsertFix(Build.FixAt(tag.Id, 7000, 10));

        var (count, first, last) = Repo.FixSummary(tag.Id);

        Assert.Equal(2, count);
        Assert.Equal(1000, first);
        Assert.Equal(7000, last);
    }

    [Fact]
    public void FixSummary_IsEmptyForAnUnknownTag()
    {
        var (count, first, last) = Repo.FixSummary(42);

        Assert.Equal(0, count);
        Assert.Null(first);
        Assert.Null(last);
    }

    [Fact]
    public void Fix_RoundTripsEveryField()
    {
        var tag = Repo.GetOrCreateTag("SER1", "Pluis", 1000);
        var original = Build.FixAt(tag.Id, 5000, 10, accuracy: 42.5, isOld: true, isInaccurate: true, battery: 3);
        original.PositionType = "crowdsourced";
        original.Altitude = 12.25;

        Repo.TryInsertFix(original);
        var stored = Repo.LatestFix(tag.Id)!;

        Assert.Equal(42.5, stored.HorizontalAccuracy);
        Assert.Equal(12.25, stored.Altitude);
        Assert.Equal("crowdsourced", stored.PositionType);
        Assert.True(stored.IsOld);
        Assert.True(stored.IsInaccurate);
        Assert.Equal(3, stored.BatteryStatus);
    }

    // ---- zones -------------------------------------------------------------------------------

    [Fact]
    public void Zones_RoundTripThroughCrud()
    {
        var id = Repo.InsertZone(Build.HomeZone());
        var zone = Repo.GetZone(id)!;

        Assert.Equal(ZoneKind.Home, zone.Kind);
        Assert.Equal(30, zone.RadiusM);
        Assert.Equal(25, zone.ExitBufferM);

        zone.Name = "Back garden";
        zone.RadiusM = 45;
        Repo.UpdateZone(zone);

        Assert.Equal("Back garden", Repo.GetZone(id)!.Name);
        Assert.Equal(45, Repo.GetZone(id)!.RadiusM);

        Repo.DeleteZone(id);
        Assert.Null(Repo.GetZone(id));
        Assert.Empty(Repo.ListZones());
    }

    [Fact]
    public void HomeZone_PicksTheFirstHomeKind()
    {
        Repo.InsertZone(new Zone { Name = "Park", Kind = ZoneKind.Watch, RadiusM = 50 });
        var homeId = Repo.InsertZone(Build.HomeZone());

        Assert.Equal(homeId, Repo.HomeZone()!.Id);
    }

    [Fact]
    public void HomeZone_IsNullWhenNoneIsSet() => Assert.Null(Repo.HomeZone());

    [Fact]
    public void DeletingAZone_TakesItsEventsWithIt()
    {
        var tag = Repo.GetOrCreateTag("SER1", "Pluis", 1000);
        var zoneId = Repo.InsertZone(Build.HomeZone());
        var fix = Repo.TryInsertFix(Build.FixAt(tag.Id, 1000, 10))!;

        Repo.InsertZoneEvent(new ZoneEvent
        {
            TagId = tag.Id, ZoneId = zoneId, EventType = ZoneEventType.Exit,
            FixId = fix.Id, OccurredUtc = 1000,
        });

        Repo.DeleteZone(zoneId);

        Assert.Empty(Repo.RecentZoneEvents(tag.Id));
    }

    // ---- zone state --------------------------------------------------------------------------

    [Fact]
    public void ZoneState_DefaultsToUnknownAndThenUpserts()
    {
        var tag = Repo.GetOrCreateTag("SER1", "Pluis", 1000);
        var zoneId = Repo.InsertZone(Build.HomeZone());

        var initial = Repo.GetZoneState(tag.Id, zoneId);
        Assert.Equal(FenceState.Unknown, initial.State);

        initial.State = FenceState.Outside;
        initial.PendingCount = 1;
        initial.UpdatedUtc = 5000;
        Repo.SaveZoneState(initial);

        // Saving twice must update, not fail on the composite key.
        initial.PendingCount = 2;
        Repo.SaveZoneState(initial);

        var reloaded = Repo.GetZoneState(tag.Id, zoneId);
        Assert.Equal(FenceState.Outside, reloaded.State);
        Assert.Equal(2, reloaded.PendingCount);
        Assert.Equal(5000, reloaded.UpdatedUtc);
    }

    // ---- zone events -------------------------------------------------------------------------

    [Fact]
    public void ZoneEvents_ComeBackNewestFirst()
    {
        var tag = Repo.GetOrCreateTag("SER1", "Pluis", 1000);
        var zoneId = Repo.InsertZone(Build.HomeZone());
        var fix = Repo.TryInsertFix(Build.FixAt(tag.Id, 1000, 10))!;

        foreach (var (type, at) in new[]
                 {
                     (ZoneEventType.Exit, 1000L), (ZoneEventType.Enter, 2000L),
                 })
        {
            Repo.InsertZoneEvent(new ZoneEvent
            {
                TagId = tag.Id, ZoneId = zoneId, EventType = type, FixId = fix.Id, OccurredUtc = at,
            });
        }

        var events = Repo.RecentZoneEvents(tag.Id);

        Assert.Equal(2, events.Count);
        Assert.Equal(ZoneEventType.Enter, events[0].EventType);
        Assert.Equal(ZoneEventType.Exit, events[1].EventType);
    }

    // ---- excursions --------------------------------------------------------------------------

    [Fact]
    public void Excursions_OpenThenClose()
    {
        var tag = Repo.GetOrCreateTag("SER1", "Pluis", 1000);

        Assert.Null(Repo.OpenExcursion(tag.Id));

        var id = Repo.InsertExcursion(new Excursion { TagId = tag.Id, DepartedUtc = 1000 });
        var open = Repo.OpenExcursion(tag.Id)!;

        Assert.Equal(id, open.Id);
        Assert.True(open.IsOpen);

        open.ReturnedUtc = 5000;
        open.MaxDistanceM = 320;
        open.CoverageRatio = 0.5;
        Repo.UpdateExcursion(open);

        Assert.Null(Repo.OpenExcursion(tag.Id));

        var stored = Assert.Single(Repo.GetExcursions(tag.Id, 0, 10_000));
        Assert.Equal(5000, stored.ReturnedUtc);
        Assert.Equal(320, stored.MaxDistanceM);
        Assert.Equal(0.5, stored.CoverageRatio);
    }

    [Fact]
    public void GetExcursions_IncludesOnesStillRunning()
    {
        var tag = Repo.GetOrCreateTag("SER1", "Pluis", 1000);
        Repo.InsertExcursion(new Excursion { TagId = tag.Id, DepartedUtc = 1000 });

        Assert.Single(Repo.GetExcursions(tag.Id, 900, 1100));
    }

    [Fact]
    public void GetExcursions_ExcludesOnesOutsideTheWindow()
    {
        var tag = Repo.GetOrCreateTag("SER1", "Pluis", 1000);
        Repo.InsertExcursion(new Excursion { TagId = tag.Id, DepartedUtc = 1000, ReturnedUtc = 2000 });

        Assert.Empty(Repo.GetExcursions(tag.Id, 5000, 9000));
    }

    // ---- alerts ------------------------------------------------------------------------------

    [Fact]
    public void Alerts_AreStoredAndReadBackNewestFirst()
    {
        Repo.InsertAlert(new Alert { Kind = AlertKind.ZoneExit, Message = "out", RaisedUtc = 1000 });
        Repo.InsertAlert(new Alert { Kind = AlertKind.DataStale, Message = "quiet", RaisedUtc = 2000 });

        var alerts = Repo.RecentAlerts();

        Assert.Equal(2, alerts.Count);
        Assert.Equal(AlertKind.DataStale, alerts[0].Kind);
        Assert.Equal("quiet", alerts[0].Message);
    }

    [Fact]
    public void LastAlertUtc_IsPerKind()
    {
        Repo.InsertAlert(new Alert { Kind = AlertKind.ZoneExit, Message = "a", RaisedUtc = 1000 });
        Repo.InsertAlert(new Alert { Kind = AlertKind.ZoneExit, Message = "b", RaisedUtc = 3000 });
        Repo.InsertAlert(new Alert { Kind = AlertKind.LowBattery, Message = "c", RaisedUtc = 2000 });

        Assert.Equal(3000, Repo.LastAlertUtc(AlertKind.ZoneExit));
        Assert.Equal(2000, Repo.LastAlertUtc(AlertKind.LowBattery));
        Assert.Null(Repo.LastAlertUtc(AlertKind.ReaderProblem));
    }

    [Fact]
    public void RecentAlerts_RespectsTheLimit()
    {
        for (var i = 0; i < 10; i++)
            Repo.InsertAlert(new Alert { Kind = AlertKind.ZoneExit, Message = $"m{i}", RaisedUtc = i });

        Assert.Equal(3, Repo.RecentAlerts(3).Count);
    }

    // ---- raw snapshots -----------------------------------------------------------------------

    [Fact]
    public void RawSnapshots_ArePrunedToTheNewest()
    {
        for (var i = 0; i < 10; i++) Repo.InsertRawSnapshot(i, $"payload {i}");

        var deleted = Repo.PruneRawSnapshots(keep: 3);

        Assert.Equal(7, deleted);
        using var context = _db.Factory.CreateDbContext();
        Assert.Equal(3, context.RawSnapshots.Count());
    }
}
