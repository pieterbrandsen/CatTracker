using Microsoft.EntityFrameworkCore;

namespace CatTracker.Data;

public sealed class MapTile
{
    public int Z { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public byte[] Data { get; set; } = [];
    public long FetchedUtc { get; set; }
}

/// <summary>
/// Cached map tiles, in their own database file.
///
/// Separate from the main database for one reason: a seeded neighbourhood runs to hundreds of
/// megabytes of PNGs, and nobody wants that inside the nightly backup of a cat's location
/// history. Losing this file costs a re-download; losing the other one costs the history.
///
/// It is also the one place we use EnsureCreated rather than migrations, deliberately. This is a
/// rebuildable cache with a single trivial table: if the schema ever needs to change, deleting
/// tiles.db is the correct migration.
/// </summary>
public sealed class TileContext(DbContextOptions<TileContext> options) : DbContext(options)
{
    public DbSet<MapTile> Tiles => Set<MapTile>();

    protected override void OnModelCreating(ModelBuilder model) =>
        model.Entity<MapTile>(entity =>
        {
            entity.ToTable("Tiles");
            entity.HasKey(t => new { t.Z, t.X, t.Y });
            entity.Property(t => t.Data).IsRequired();
        });
}

public sealed class TileContextFactory(string databasePath) : IDbContextFactory<TileContext>
{
    public TileContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<TileContext>()
            .UseSqlite(SqliteContextFactory.BuildConnectionString(databasePath))
            .Options;

        return new TileContext(options);
    }
}

public static class TileSetup
{
    public static void Ensure(IDbContextFactory<TileContext> factory)
    {
        using var context = factory.CreateDbContext();
        context.Database.EnsureCreated();
    }
}
