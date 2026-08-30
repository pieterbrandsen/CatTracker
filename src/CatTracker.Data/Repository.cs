using CatTracker.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CatTracker.Data;

/// <summary>
/// All data access, in one place. Deliberately not a generic repository: there are two dozen
/// queries in this system and naming them plainly beats an abstraction nobody needs.
///
/// A context is created per operation from the injected factory, because the collector writes on
/// a background thread while HTTP requests read concurrently, and a DbContext is not thread-safe.
/// SQLite's connection pool makes this cheap, and WAL mode keeps readers out of the writer's way.
/// </summary>
public sealed class Repository(IDbContextFactory<CatContext> factory)
{
    public IReadOnlyList<string> AppliedMigrations() => DatabaseSetup.AppliedMigrations(factory);

    // ---- Tags ------------------------------------------------------------------------------

    public Tag GetOrCreateTag(string serialNumber, string findMyName, long nowUtc)
    {
        using var context = factory.CreateDbContext();

        var existing = context.Tags.FirstOrDefault(t => t.SerialNumber == serialNumber);
        if (existing is not null)
        {
            // Keep the name in step if it was renamed in the Find My app.
            if (existing.FindMyName != findMyName)
            {
                existing.FindMyName = findMyName;
                context.SaveChanges();
            }

            return existing;
        }

        var tag = new Tag
        {
            SerialNumber = serialNumber,
            FindMyName = findMyName,
            PetName = findMyName,
            IsActive = true,
            CreatedUtc = nowUtc,
        };

        context.Tags.Add(tag);
        context.SaveChanges();
        return tag;
    }

    public IReadOnlyList<Tag> ListTags()
    {
        using var context = factory.CreateDbContext();
        return context.Tags.AsNoTracking().OrderBy(t => t.PetName).ToList();
    }

    public Tag? GetTag(long id)
    {
        using var context = factory.CreateDbContext();
        return context.Tags.AsNoTracking().FirstOrDefault(t => t.Id == id);
    }

    public void UpdateTag(long id, string petName, bool isActive)
    {
        using var context = factory.CreateDbContext();
        context.Tags
            .Where(t => t.Id == id)
            .ExecuteUpdate(set => set
                .SetProperty(t => t.PetName, petName)
                .SetProperty(t => t.IsActive, isActive));
    }

    // ---- Fixes -----------------------------------------------------------------------------

    /// <summary>
    /// Stores a fix, returning it with its id, or null when this exact timestamp is already
    /// known. Find My holds only the latest position, so the same fix is re-read on every poll:
    /// the duplicate case is the common one, and is checked before insert rather than caught as
    /// an exception several times a minute.
    /// </summary>
    public Fix? TryInsertFix(Fix fix)
    {
        using var context = factory.CreateDbContext();

        var alreadyKnown = context.Fixes
            .Any(f => f.TagId == fix.TagId && f.TimestampUtc == fix.TimestampUtc);
        if (alreadyKnown) return null;

        context.Fixes.Add(fix);

        try
        {
            context.SaveChanges();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Lost a race with another writer. Same outcome as the check above.
            return null;
        }

        return fix;
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteErrorCode: 19 };

    public Fix? LatestFix(long tagId)
    {
        using var context = factory.CreateDbContext();
        return context.Fixes.AsNoTracking()
            .Where(f => f.TagId == tagId)
            .OrderByDescending(f => f.TimestampUtc)
            .FirstOrDefault();
    }

    public IReadOnlyList<Fix> GetFixes(long tagId, long fromUtc, long toUtc, int limit = 200_000)
    {
        using var context = factory.CreateDbContext();
        return context.Fixes.AsNoTracking()
            .Where(f => f.TagId == tagId && f.TimestampUtc >= fromUtc && f.TimestampUtc <= toUtc)
            .OrderBy(f => f.TimestampUtc)
            .Take(limit)
            .ToList();
    }

    public (int Count, long? First, long? Last) FixSummary(long tagId)
    {
        using var context = factory.CreateDbContext();

        var query = context.Fixes.AsNoTracking().Where(f => f.TagId == tagId);
        var count = query.Count();
        if (count == 0) return (0, null, null);

        return (count, query.Min(f => f.TimestampUtc), query.Max(f => f.TimestampUtc));
    }

    // ---- Zones -----------------------------------------------------------------------------

    public IReadOnlyList<Zone> ListZones()
    {
        using var context = factory.CreateDbContext();
        return context.Zones.AsNoTracking().OrderBy(z => z.Kind).ThenBy(z => z.Name).ToList();
    }

    public Zone? GetZone(long id)
    {
        using var context = factory.CreateDbContext();
        return context.Zones.AsNoTracking().FirstOrDefault(z => z.Id == id);
    }

    public Zone? HomeZone()
    {
        using var context = factory.CreateDbContext();
        return context.Zones.AsNoTracking()
            .Where(z => z.Kind == ZoneKind.Home)
            .OrderBy(z => z.Id)
            .FirstOrDefault();
    }

    public long InsertZone(Zone zone)
    {
        using var context = factory.CreateDbContext();
        context.Zones.Add(zone);
        context.SaveChanges();
        return zone.Id;
    }

    public void UpdateZone(Zone zone)
    {
        using var context = factory.CreateDbContext();
        context.Zones.Update(zone);
        context.SaveChanges();
    }

    public void DeleteZone(long id)
    {
        using var context = factory.CreateDbContext();
        context.Zones.Where(z => z.Id == id).ExecuteDelete();
    }

    // ---- Zone state & events ---------------------------------------------------------------

    public ZoneTrackerState GetZoneState(long tagId, long zoneId)
    {
        using var context = factory.CreateDbContext();
        return context.ZoneStates.AsNoTracking()
                   .FirstOrDefault(s => s.TagId == tagId && s.ZoneId == zoneId)
               ?? new ZoneTrackerState { TagId = tagId, ZoneId = zoneId };
    }

    public void SaveZoneState(ZoneTrackerState state)
    {
        using var context = factory.CreateDbContext();

        var exists = context.ZoneStates
            .Any(s => s.TagId == state.TagId && s.ZoneId == state.ZoneId);

        if (exists) context.ZoneStates.Update(state);
        else context.ZoneStates.Add(state);

        context.SaveChanges();
    }

    public long InsertZoneEvent(ZoneEvent zoneEvent)
    {
        using var context = factory.CreateDbContext();
        context.ZoneEvents.Add(zoneEvent);
        context.SaveChanges();
        return zoneEvent.Id;
    }

    public IReadOnlyList<ZoneEvent> RecentZoneEvents(long tagId, int limit = 100)
    {
        using var context = factory.CreateDbContext();
        return context.ZoneEvents.AsNoTracking()
            .Where(e => e.TagId == tagId)
            .OrderByDescending(e => e.OccurredUtc)
            .Take(limit)
            .ToList();
    }

    // ---- Excursions ------------------------------------------------------------------------

    public Excursion? OpenExcursion(long tagId)
    {
        using var context = factory.CreateDbContext();
        return context.Excursions.AsNoTracking()
            .Where(e => e.TagId == tagId && e.ReturnedUtc == null)
            .OrderByDescending(e => e.DepartedUtc)
            .FirstOrDefault();
    }

    public long InsertExcursion(Excursion excursion)
    {
        using var context = factory.CreateDbContext();
        context.Excursions.Add(excursion);
        context.SaveChanges();
        return excursion.Id;
    }

    public void UpdateExcursion(Excursion excursion)
    {
        using var context = factory.CreateDbContext();
        context.Excursions.Update(excursion);
        context.SaveChanges();
    }

    public IReadOnlyList<Excursion> GetExcursions(long tagId, long fromUtc, long toUtc)
    {
        using var context = factory.CreateDbContext();
        return context.Excursions.AsNoTracking()
            .Where(e => e.TagId == tagId
                        && e.DepartedUtc <= toUtc
                        && (e.ReturnedUtc == null || e.ReturnedUtc >= fromUtc))
            .OrderBy(e => e.DepartedUtc)
            .ToList();
    }

    // ---- Alerts ----------------------------------------------------------------------------

    public long InsertAlert(Alert alert)
    {
        using var context = factory.CreateDbContext();
        context.Alerts.Add(alert);
        context.SaveChanges();
        return alert.Id;
    }

    public long? LastAlertUtc(AlertKind kind)
    {
        using var context = factory.CreateDbContext();
        return context.Alerts.AsNoTracking()
            .Where(a => a.Kind == kind)
            .Select(a => (long?)a.RaisedUtc)
            .Max();
    }

    public IReadOnlyList<Alert> RecentAlerts(int limit = 50)
    {
        using var context = factory.CreateDbContext();
        return context.Alerts.AsNoTracking()
            .OrderByDescending(a => a.RaisedUtc)
            .Take(limit)
            .ToList();
    }

    // ---- Raw snapshots (debug aid; safe to truncate) ----------------------------------------

    public void InsertRawSnapshot(long capturedUtc, string payload)
    {
        using var context = factory.CreateDbContext();
        context.RawSnapshots.Add(new RawSnapshot { CapturedUtc = capturedUtc, Payload = payload });
        context.SaveChanges();
    }

    public int PruneRawSnapshots(int keep)
    {
        using var context = factory.CreateDbContext();

        var keepIds = context.RawSnapshots
            .OrderByDescending(r => r.CapturedUtc)
            .Take(keep)
            .Select(r => r.Id);

        return context.RawSnapshots.Where(r => !keepIds.Contains(r.Id)).ExecuteDelete();
    }
}
