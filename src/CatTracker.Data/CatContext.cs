using CatTracker.Core;
using Microsoft.EntityFrameworkCore;

namespace CatTracker.Data;

/// <summary>A raw cache payload, kept for debugging. Safe to truncate at any time.</summary>
public sealed class RawSnapshot
{
    public long Id { get; set; }
    public long CapturedUtc { get; set; }
    public string Payload { get; set; } = "";
}

/// <summary>
/// The whole database. One SQLite file, WAL mode, no server and no administration: backing the
/// entire system up is a file copy, which is exactly what you want from something that has to
/// survive on a Mac in a cupboard for years.
///
/// Entities are the plain domain types from CatTracker.Core, mapped here with fluent
/// configuration so the domain project stays free of any persistence dependency.
/// </summary>
public sealed class CatContext(DbContextOptions<CatContext> options) : DbContext(options)
{
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<Fix> Fixes => Set<Fix>();
    public DbSet<Zone> Zones => Set<Zone>();
    public DbSet<ZoneTrackerState> ZoneStates => Set<ZoneTrackerState>();
    public DbSet<ZoneEvent> ZoneEvents => Set<ZoneEvent>();
    public DbSet<Excursion> Excursions => Set<Excursion>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<RawSnapshot> RawSnapshots => Set<RawSnapshot>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Tag>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.HasIndex(t => t.SerialNumber).IsUnique();
            entity.Property(t => t.SerialNumber).IsRequired();
            entity.Property(t => t.FindMyName).IsRequired();
            entity.Property(t => t.PetName).IsRequired();
        });

        model.Entity<Fix>(entity =>
        {
            entity.HasKey(f => f.Id);

            // The dedupe guarantee. Find My only ever holds the latest position, so the poll loop
            // re-reads the same fix hundreds of times; this constraint is what makes ingestion
            // idempotent instead of producing a duplicate row every ten seconds.
            // SQLite scans an index in either direction, so this one index serves both the
            // dedupe constraint and every "latest fix" / time-range query we make.
            entity.HasIndex(f => new { f.TagId, f.TimestampUtc }).IsUnique();

            entity.Ignore(f => f.At);

            entity.HasOne<Tag>()
                  .WithMany()
                  .HasForeignKey(f => f.TagId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Zone>(entity =>
        {
            entity.HasKey(z => z.Id);
            entity.Property(z => z.Name).IsRequired();

            // Stored as text: a zone table you can read in any SQLite browser is worth more than
            // three bytes saved per row.
            entity.Property(z => z.Kind).HasConversion<string>().IsRequired();
        });

        model.Entity<ZoneTrackerState>(entity =>
        {
            entity.HasKey(s => new { s.TagId, s.ZoneId });

            entity.HasOne<Tag>().WithMany().HasForeignKey(s => s.TagId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Zone>().WithMany().HasForeignKey(s => s.ZoneId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ZoneEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EventType).HasConversion<string>().IsRequired();
            entity.HasIndex(e => new { e.TagId, e.OccurredUtc });

            entity.HasOne<Tag>().WithMany().HasForeignKey(e => e.TagId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Zone>().WithMany().HasForeignKey(e => e.ZoneId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Excursion>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Ignore(e => e.IsOpen);
            entity.HasIndex(e => new { e.TagId, e.DepartedUtc });

            entity.HasOne<Tag>().WithMany().HasForeignKey(e => e.TagId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Alert>(entity =>
        {
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Kind).HasConversion<string>().IsRequired();
            entity.Property(a => a.Message).IsRequired();
            entity.HasIndex(a => a.RaisedUtc);
        });

        model.Entity<RawSnapshot>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Payload).IsRequired();
            entity.HasIndex(r => r.CapturedUtc);
        });
    }
}
