using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CatTracker.Data;

/// <summary>
/// Creates contexts against a SQLite file. Used by the tests and by `dotnet ef` at design time;
/// the app itself registers a pooled factory for the same context.
/// </summary>
public sealed class SqliteContextFactory(string databasePath) : IDbContextFactory<CatContext>
{
    public string DatabasePath { get; } = databasePath;

    public static string BuildConnectionString(string databasePath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();

    public CatContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<CatContext>()
            .UseSqlite(BuildConnectionString(DatabasePath))
            .Options;

        return new CatContext(options);
    }
}

/// <summary>
/// Lets `dotnet ef migrations add` work without booting the web app. The path is irrelevant —
/// nothing is opened, EF only needs the model.
/// </summary>
public sealed class DesignTimeCatContextFactory : IDesignTimeDbContextFactory<CatContext>
{
    public CatContext CreateDbContext(string[] args) =>
        new SqliteContextFactory(Path.Combine(Path.GetTempPath(), "cattracker-design.db"))
            .CreateDbContext();
}

public static class DatabaseSetup
{
    /// <summary>
    /// Applies outstanding migrations and sets the pragmas we depend on. Safe to call on every
    /// start; this is what makes updating the app a matter of swapping binaries.
    /// </summary>
    public static IReadOnlyList<string> Migrate(IDbContextFactory<CatContext> factory)
    {
        using var context = factory.CreateDbContext();

        var pending = context.Database.GetPendingMigrations().ToArray();
        context.Database.Migrate();

        // WAL is persistent in the file; the rest are per-connection but harmless to repeat.
        context.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        context.Database.ExecuteSqlRaw("PRAGMA synchronous=NORMAL;");

        return pending;
    }

    public static IReadOnlyList<string> AppliedMigrations(IDbContextFactory<CatContext> factory)
    {
        using var context = factory.CreateDbContext();
        return context.Database.GetAppliedMigrations().ToArray();
    }
}
