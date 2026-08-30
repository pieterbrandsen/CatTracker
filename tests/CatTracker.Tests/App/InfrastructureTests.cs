using CatTracker.App;
using CatTracker.App.Endpoints;
using CatTracker.App.Readers;
using CatTracker.App.Services;
using CatTracker.Core;

namespace CatTracker.Tests.App;

public class TileMathTests
{
    [Fact]
    public void FromLatLon_MapsTheWholeWorldToTileZeroAtZoomZero()
    {
        var tile = TileMath.FromLatLon(52.0907, 5.1214, 0);
        Assert.Equal(new TileRef(0, 0, 0), tile);
    }

    [Fact]
    public void FromLatLon_PutsWesternEuropeInTheExpectedTile()
    {
        // Utrecht at z=1 is the north-eastern quadrant.
        var tile = TileMath.FromLatLon(52.0907, 5.1214, 1);
        Assert.Equal(new TileRef(1, 1, 0), tile);
    }

    [Fact]
    public void FromLatLon_ClampsBeyondTheMercatorLimit()
    {
        var north = TileMath.FromLatLon(89.9, 0, 4);
        var south = TileMath.FromLatLon(-89.9, 0, 4);

        Assert.Equal(0, north.Y);
        Assert.Equal(15, south.Y);
    }

    [Fact]
    public void FromLatLon_ClampsTheZoom() =>
        Assert.Equal(TileMath.MaxZoom, TileMath.FromLatLon(52, 5, 99).Z);

    [Fact]
    public void InBounds_AgreesWithCount()
    {
        var bounds = new Bounds(52.08, 5.11, 52.10, 5.14);

        Assert.Equal(TileMath.Count(bounds, 14, 16), TileMath.InBounds(bounds, 14, 16).Count());
    }

    [Fact]
    public void InBounds_CoversASingleTileForATinyArea()
    {
        var bounds = new Bounds(52.0907, 5.1214, 52.0908, 5.1215);
        Assert.Single(TileMath.InBounds(bounds, 14, 14));
    }

    [Fact]
    public void Count_GrowsRoughlyFourfoldPerZoomLevel()
    {
        var bounds = new Bounds(52.05, 5.05, 52.15, 5.25);

        var atFifteen = TileMath.Count(bounds, 15, 15);
        var atSixteen = TileMath.Count(bounds, 16, 16);

        Assert.InRange(atSixteen / (double)atFifteen, 3.0, 5.0);
    }

    [Fact]
    public void Count_HandlesAnInvertedZoomRange() =>
        Assert.True(TileMath.Count(new Bounds(52.08, 5.11, 52.10, 5.14), 16, 14) > 0);
}

public class CatSimulatorTests
{
    private const long Hour = 3_600_000;

    [Fact]
    public void Advance_ProducesFixesInChronologicalOrder()
    {
        var simulator = new CatSimulator(Build.HomeLat, Build.HomeLon, 42, 0);
        var fixes = simulator.Advance(24 * Hour).ToList();

        Assert.NotEmpty(fixes);
        for (var i = 1; i < fixes.Count; i++)
            Assert.True(fixes[i].TimestampUtcMs > fixes[i - 1].TimestampUtcMs);
    }

    [Fact]
    public void Advance_KeepsHerInTheNeighbourhood()
    {
        var simulator = new CatSimulator(Build.HomeLat, Build.HomeLon, 42, 0);

        foreach (var fix in simulator.Advance(7 * 24 * Hour))
        {
            var distance = GeoMath.DistanceM(fix.Lat, fix.Lon, Build.HomeLat, Build.HomeLon);
            Assert.True(distance < 1200, $"wandered {distance:F0} m from home");
        }
    }

    [Fact]
    public void Advance_ProducesBothHomeAndAwayPositions()
    {
        var simulator = new CatSimulator(Build.HomeLat, Build.HomeLon, 7, 0);
        var distances = simulator.Advance(3 * 24 * Hour)
            .Select(f => GeoMath.DistanceM(f.Lat, f.Lon, Build.HomeLat, Build.HomeLon))
            .ToList();

        Assert.Contains(distances, d => d < 30);
        Assert.Contains(distances, d => d > 100);
    }

    [Fact]
    public void Advance_ReproducesTheVaryingAccuracyOfARealTag()
    {
        var accuracies = new CatSimulator(Build.HomeLat, Build.HomeLon, 7, 0)
            .Advance(3 * 24 * Hour).Select(f => f.Accuracy).ToList();

        // Tight when your own phone sees her, vague when a stranger's does.
        Assert.Contains(accuracies, a => a < 25);
        Assert.Contains(accuracies, a => a > 120);
    }

    [Fact]
    public void Advance_ReturnsNothingBeforeItsStartTime() =>
        Assert.Empty(new CatSimulator(Build.HomeLat, Build.HomeLon, 1, 10_000).Advance(5_000));

    [Fact]
    public void Advance_IsDeterministicForAGivenSeed()
    {
        var first = new CatSimulator(Build.HomeLat, Build.HomeLon, 99, 0).Advance(6 * Hour).ToList();
        var second = new CatSimulator(Build.HomeLat, Build.HomeLon, 99, 0).Advance(6 * Hour).ToList();

        Assert.Equal(first.Count, second.Count);
        Assert.Equal(first[^1].Lat, second[^1].Lat);
    }
}

public class ReplayReaderTests
{
    [Fact]
    public async Task ReplayReader_EmitsPayloadsTheRealParserUnderstands()
    {
        var reader = new ReplayFindMyReader(Build.Options(o =>
        {
            o.Replay.HomeLat = Build.HomeLat;
            o.Replay.HomeLon = Build.HomeLon;
        }));

        var snapshot = await reader.TryReadAsync(CancellationToken.None);
        Assert.NotNull(snapshot);

        var parsed = FindMyParser.Parse(snapshot.Json);

        Assert.Empty(parsed.Warnings);
        var item = Assert.Single(parsed.Items);
        Assert.Equal(ReplayConstants.SerialNumber, item.SerialNumber);
        Assert.NotNull(item.Location);
    }

    [Fact]
    public async Task ReplayReader_ReportsItselfHealthy()
    {
        var reader = new ReplayFindMyReader(Build.Options());
        var heartbeat = await reader.ReadHeartbeatAsync(CancellationToken.None);

        Assert.Equal(ReaderHeartbeat.Ok, heartbeat!.Status);
        Assert.Contains("Replay", reader.Description);
    }
}

public class FileFindMyReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "cattracker-tests", Guid.NewGuid().ToString("N"));

    public FileFindMyReaderTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private FileFindMyReader NewReader() => new(
        Path.Combine(_directory, "items.json"),
        Path.Combine(_directory, "heartbeat.json"),
        "test spool",
        Build.Logger<FileFindMyReader>());

    [Fact]
    public async Task ReturnsNull_WhenTheSpoolIsEmpty() =>
        Assert.Null(await NewReader().TryReadAsync(CancellationToken.None));

    [Fact]
    public async Task ReadsOnce_ThenReportsNoChange()
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, "items.json"), "[]");
        var reader = NewReader();

        Assert.NotNull(await reader.TryReadAsync(CancellationToken.None));

        // Polling every ten seconds must not re-ingest an unchanged file.
        Assert.Null(await reader.TryReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PicksUpAChangedFile()
    {
        var path = Path.Combine(_directory, "items.json");
        await File.WriteAllTextAsync(path, "[]");

        var reader = NewReader();
        await reader.TryReadAsync(CancellationToken.None);

        await File.WriteAllTextAsync(path, """[{"serialNumber":"X"}]""");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));

        Assert.NotNull(await reader.TryReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadsTheHeartbeatWrittenByTheReaderAgent()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_directory, "heartbeat.json"),
            """{"writtenUtcMs":123,"status":"ok","detail":"unchanged","sourceMTimeUtcMs":9,"sourceSizeBytes":4}""");

        var heartbeat = await NewReader().ReadHeartbeatAsync(CancellationToken.None);

        Assert.Equal(123, heartbeat!.WrittenUtcMs);
        Assert.Equal(ReaderHeartbeat.Ok, heartbeat.Status);
        Assert.Equal(9, heartbeat.SourceMTimeUtcMs);
    }

    [Fact]
    public async Task AMissingHeartbeat_IsNull() =>
        Assert.Null(await NewReader().ReadHeartbeatAsync(CancellationToken.None));

    [Fact]
    public async Task ACorruptHeartbeat_IsNullRatherThanFatal()
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, "heartbeat.json"), "{oh dear");
        Assert.Null(await NewReader().ReadHeartbeatAsync(CancellationToken.None));
    }
}

public class LogTailTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "cattracker-tests", Guid.NewGuid().ToString("N"));

    public LogTailTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private void WriteLog(string name, params string[] lines) =>
        File.WriteAllLines(Path.Combine(_directory, name), lines);

    [Fact]
    public void Read_ReturnsNothingForAnEmptyDirectory()
    {
        var page = new LogTail(_directory).Read(100, null, null);

        Assert.Empty(page.Files);
        Assert.Empty(page.Lines);
    }

    [Fact]
    public void Read_ReturnsNothingWhenTheDirectoryIsMissing() =>
        Assert.Empty(new LogTail(Path.Combine(_directory, "nope")).Read(100, null, null).Lines);

    [Fact]
    public void Read_TakesTheNewestFileByDefault()
    {
        WriteLog("cattracker-20260101.log", "old line");
        WriteLog("cattracker-20260202.log", "new line");

        var page = new LogTail(_directory).Read(100, null, null);

        Assert.Equal("cattracker-20260202.log", page.File);
        Assert.Equal("new line", Assert.Single(page.Lines));
        Assert.Equal(2, page.Files.Count);
    }

    [Fact]
    public void Read_CanSelectAnOlderFile()
    {
        WriteLog("cattracker-20260101.log", "old line");
        WriteLog("cattracker-20260202.log", "new line");

        var page = new LogTail(_directory).Read(100, null, "cattracker-20260101.log");

        Assert.Equal("old line", Assert.Single(page.Lines));
    }

    [Fact]
    public void Read_LimitsToTheLastNLines()
    {
        WriteLog("cattracker-20260101.log", "a", "b", "c", "d");

        var page = new LogTail(_directory).Read(2, null, null);

        Assert.Equal(["c", "d"], page.Lines);
    }

    [Fact]
    public void Read_FiltersCaseInsensitively()
    {
        WriteLog("cattracker-20260101.log", "[INF] Excursion 4 opened", "[DBG] Geofence Home");

        var page = new LogTail(_directory).Read(100, "excursion", null);

        Assert.Contains("Excursion 4 opened", Assert.Single(page.Lines));
    }

    [Fact]
    public void Read_IgnoresAPathTraversalAttempt()
    {
        WriteLog("cattracker-20260101.log", "real line");

        // A log viewer is not a reason to hand out arbitrary file reads.
        var page = new LogTail(_directory).Read(100, null, "../../../etc/passwd");

        Assert.Equal("cattracker-20260101.log", page.File);
        Assert.Equal("real line", Assert.Single(page.Lines));
    }

    [Fact]
    public void Read_IgnoresFilesThatAreNotOurs()
    {
        WriteLog("cattracker-20260101.log", "ours");
        File.WriteAllText(Path.Combine(_directory, "secrets.log"), "not ours");

        Assert.Equal("cattracker-20260101.log", Assert.Single(new LogTail(_directory).Files()));
    }

    [Fact]
    public void Read_HandlesAFileLargerThanTheTailWindow()
    {
        var lines = Enumerable.Range(0, 40_000).Select(i => $"line {i} " + new string('x', 40));
        File.WriteAllLines(Path.Combine(_directory, "cattracker-20260101.log"), lines);

        var page = new LogTail(_directory).Read(5, null, null);

        Assert.Equal(5, page.Lines.Count);
        Assert.Contains("line 39999", page.Lines[^1]);
    }
}

public class ApiHelperTests
{
    [Fact]
    public void Decimate_LeavesShortTracksAlone()
    {
        var fixes = Enumerable.Range(0, 10).Select(i => Build.FixAt(1, i, 5)).ToList();
        Assert.Same(fixes, ApiEndpoints.Decimate(fixes, 100));
    }

    [Fact]
    public void Decimate_ThinsToTheRequestedSizeAndKeepsBothEnds()
    {
        var fixes = Enumerable.Range(0, 1000).Select(i => Build.FixAt(1, i, 5)).ToList();

        var thinned = ApiEndpoints.Decimate(fixes, 100);

        Assert.Equal(100, thinned.Count);
        Assert.Equal(0, thinned[0].TimestampUtc);
        Assert.Equal(999, thinned[^1].TimestampUtc);
    }

    [Fact]
    public void Window_DefaultsToTheFallbackEndingNow()
    {
        var (from, to) = ApiEndpoints.Window(null, null, TimeSpan.FromHours(24));

        var expected = (long)TimeSpan.FromHours(24).TotalMilliseconds;
        Assert.InRange(to - from, expected - 2000, expected + 2000);
    }

    [Fact]
    public void Window_UsesExplicitBounds()
    {
        var (from, to) = ApiEndpoints.Window(100, 500, TimeSpan.FromHours(1));

        Assert.Equal(100, from);
        Assert.Equal(500, to);
    }

    [Fact]
    public void Window_StraightensAnInvertedRange()
    {
        var (from, to) = ApiEndpoints.Window(500, 100, TimeSpan.FromHours(1));

        Assert.Equal(100, from);
        Assert.Equal(500, to);
    }

    [Fact]
    public void TryBuildZone_AcceptsAValidZone()
    {
        var input = new ZoneInput("Home", "home", 52.09, 5.12, 30, null, null, null);

        Assert.True(ApiEndpoints.TryBuildZone(input, out var zone, out _));
        Assert.Equal(ZoneKind.Home, zone.Kind);
        Assert.Equal(25, zone.ExitBufferM);
        Assert.True(zone.NotifyOnExit);
    }

    [Theory]
    [InlineData("", "Home", 52.0, 5.0, 30.0, "name is required")]
    [InlineData("Home", "Nowhere", 52.0, 5.0, 30.0, "kind must be one of")]
    [InlineData("Home", "Home", 91.0, 5.0, 30.0, "centerLat is out of range")]
    [InlineData("Home", "Home", 52.0, 181.0, 30.0, "centerLon is out of range")]
    [InlineData("Home", "Home", 52.0, 5.0, 0.0, "radiusM must be between")]
    [InlineData("Home", "Home", 52.0, 5.0, -5.0, "radiusM must be between")]
    public void TryBuildZone_RejectsBadInput(
        string name, string kind, double lat, double lon, double radius, string expected)
    {
        // A zero or negative radius would make the geofence fire on every fix, forever.
        var input = new ZoneInput(name, kind, lat, lon, radius, null, null, null);

        Assert.False(ApiEndpoints.TryBuildZone(input, out _, out var error));
        Assert.Contains(expected, error);
    }

    [Fact]
    public void TryBuildZone_RejectsAnAbsurdExitBuffer()
    {
        var input = new ZoneInput("Home", "Home", 52.0, 5.0, 30, -1, null, null);

        Assert.False(ApiEndpoints.TryBuildZone(input, out _, out var error));
        Assert.Contains("exitBufferM", error);
    }
}

public class AppOptionsTests
{
    [Fact]
    public void ResolveDataDirectory_FallsBackToAPlatformDefault() =>
        Assert.NotEmpty(new AppOptions().ResolveDataDirectory());

    [Fact]
    public void ResolveDataDirectory_HonoursAnExplicitPath()
    {
        var options = new AppOptions { DataDirectory = Path.Combine(Path.GetTempPath(), "ct") };
        Assert.Equal(options.DataDirectory, options.ResolveDataDirectory());
    }

    [Fact]
    public void Expand_ResolvesTheHomeShorthand()
    {
        var expanded = AppOptions.Expand("~/Library/Caches/x");

        Assert.DoesNotContain("~", expanded);
        Assert.Contains("Library", expanded);
    }

    [Fact]
    public void Expand_LeavesAbsolutePathsAlone() =>
        Assert.Equal("/var/log/x", AppOptions.Expand("/var/log/x"));

    [Fact]
    public void Expand_ResolvesEnvironmentVariables()
    {
        // So a Windows service definition or config file can say %ProgramData%\CatTracker.
        Environment.SetEnvironmentVariable("CATTRACKER_TEST_ROOT", "somewhere");
        try
        {
            Assert.Equal("somewhere/data", AppOptions.Expand("%CATTRACKER_TEST_ROOT%/data"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CATTRACKER_TEST_ROOT", null);
        }
    }

    [Fact]
    public void Expand_HandlesAnEmptyPath() => Assert.Equal("", AppOptions.Expand(""));

    [Fact]
    public void ResolveDataDirectory_IsAlwaysAbsolute()
    {
        // A Windows service starts in System32; a relative data path would put the database there.
        var options = new AppOptions { DataDirectory = "relative/path" };
        Assert.True(Path.IsPathRooted(options.ResolveDataDirectory()));
    }

    [Fact]
    public void ResolveTimeZone_FallsBackToLocalForNonsense() =>
        Assert.Equal(TimeZoneInfo.Local, new AppOptions { TimeZone = "Mars/Olympus" }.ResolveTimeZone());

    [Fact]
    public void ResolveTimeZone_UsesTheSystemZoneWhenUnset() =>
        Assert.Equal(TimeZoneInfo.Local, new AppOptions().ResolveTimeZone());

    [Fact]
    public void GeofenceSettings_MapOntoTheCoreOptions()
    {
        var settings = new GeofenceSettings
        {
            MaxAccuracyMeters = 55, ConfirmationFixes = 3, RejectOldFixes = false,
        };

        var core = settings.ToCore();

        Assert.Equal(55, core.MaxAccuracyMeters);
        Assert.Equal(3, core.ConfirmationFixes);
        Assert.False(core.RejectOldFixes);
    }
}
