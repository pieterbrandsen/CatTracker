using CatTracker.App;
using CatTracker.App.Alerting;
using CatTracker.Core;
using CatTracker.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CatTracker.Tests;

/// <summary>A throwaway SQLite database in a temp directory, migrated and ready.</summary>
public sealed class TestDatabase : IDisposable
{
    public string Directory { get; }
    public SqliteContextFactory Factory { get; }
    public Repository Repository { get; }

    public TestDatabase()
    {
        Directory = Path.Combine(Path.GetTempPath(), "cattracker-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);

        Factory = new SqliteContextFactory(Path.Combine(Directory, "test.db"));
        DatabaseSetup.Migrate(Factory);
        Repository = new Repository(Factory);
    }

    public void Dispose()
    {
        // SQLite pools connections; without this the file stays locked and cleanup fails.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            if (System.IO.Directory.Exists(Directory))
                System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // A leaked handle should fail the run's tidiness, not its correctness.
        }
    }
}

/// <summary>Captures alerts instead of shouting at macOS.</summary>
public sealed class RecordingChannel : IAlertChannel
{
    public List<Alert> Sent { get; } = [];
    public string Name => "recording";
    public bool IsAvailable { get; set; } = true;

    public Task SendAsync(Alert alert, CancellationToken cancellationToken)
    {
        Sent.Add(alert);
        return Task.CompletedTask;
    }
}

public sealed class ThrowingChannel : IAlertChannel
{
    public string Name => "throwing";
    public bool IsAvailable => true;

    public Task SendAsync(Alert alert, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("channel is broken");
}

public static class Build
{
    public const double HomeLat = 52.0907;
    public const double HomeLon = 5.1214;

    public static IOptions<AppOptions> Options(Action<AppOptions>? configure = null)
    {
        var options = new AppOptions();
        configure?.Invoke(options);
        return Microsoft.Extensions.Options.Options.Create(options);
    }

    public static ILogger<T> Logger<T>() => NullLogger<T>.Instance;

    public static Zone HomeZone(double radius = 30, double buffer = 25) => new()
    {
        Name = "Home",
        Kind = ZoneKind.Home,
        CenterLat = HomeLat,
        CenterLon = HomeLon,
        RadiusM = radius,
        ExitBufferM = buffer,
    };

    /// <summary>A fix at a bearing/distance from home, good accuracy unless told otherwise.</summary>
    public static Fix FixAt(
        long tagId,
        long timestampUtc,
        double metresFromHome,
        double bearing = 0,
        double accuracy = 10,
        bool isOld = false,
        bool isInaccurate = false,
        int? battery = 1)
    {
        var (lat, lon) = GeoMath.Destination(HomeLat, HomeLon, bearing, metresFromHome);
        return new Fix
        {
            TagId = tagId,
            TimestampUtc = timestampUtc,
            Latitude = lat,
            Longitude = lon,
            HorizontalAccuracy = accuracy,
            IsOld = isOld,
            IsInaccurate = isInaccurate,
            BatteryStatus = battery,
            IngestedUtc = timestampUtc,
        };
    }
}
