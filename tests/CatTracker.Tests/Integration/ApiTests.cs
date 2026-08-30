using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CatTracker.App.Services;
using CatTracker.Core;
using CatTracker.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace CatTracker.Tests.Integration;

/// <summary>
/// Hosts the real application — real DI, real EF, real endpoints — against a throwaway database,
/// with the collector switched off so nothing moves underneath the assertions.
/// </summary>
public sealed class ApiFixture : WebApplicationFactory<Program>
{
    public string DataDirectory { get; } = Path.Combine(
        Path.GetTempPath(), "cattracker-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(DataDirectory);

        builder.UseEnvironment("Testing");
        builder.UseSetting("CatTracker:DataDirectory", DataDirectory);
        builder.UseSetting("CatTracker:FindMy:Enabled", "false");
        builder.UseSetting("CatTracker:FindMy:Source", "Replay");
        builder.UseSetting("CatTracker:Replay:SeedDays", "0");
        builder.UseSetting("CatTracker:Geofence:ConfirmationFixes", "1");
        // Never reach out to OpenStreetMap from a test run.
        builder.UseSetting("CatTracker:Tiles:AllowNetwork", "false");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(DataDirectory, recursive: true); } catch (IOException) { }
    }
}

public class ApiTests : IClassFixture<ApiFixture>, IDisposable
{
    private const long Minute = 60_000;

    private readonly ApiFixture _factory;
    private readonly HttpClient _client;

    public ApiTests(ApiFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public void Dispose() => _client.Dispose();

    private T Service<T>() where T : notnull => _factory.Services.GetRequiredService<T>();

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    /// <summary>Puts a cat, a home zone and one round trip into the database via the real pipeline.</summary>
    private async Task<long> SeedOneExcursion()
    {
        var repository = Service<Repository>();
        var processor = Service<FixProcessor>();

        if (repository.HomeZone() is null) repository.InsertZone(Build.HomeZone());
        var tag = repository.GetOrCreateTag("ITEST", "Pluis", 0);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var script = new (long Offset, double Metres)[]
        {
            (-30 * Minute, 5), (-25 * Minute, 300), (-20 * Minute, 420), (-15 * Minute, 5),
        };

        foreach (var (offset, metres) in script)
        {
            var stored = repository.TryInsertFix(Build.FixAt(tag.Id, now + offset, metres));
            if (stored is not null)
                await processor.ProcessAsync(tag, stored, notify: false, CancellationToken.None);
        }

        return tag.Id;
    }

    // ---- system ------------------------------------------------------------------------------

    [Fact]
    public async Task Health_ReportsTheSchemaItIsRunning()
    {
        var body = await Json(await _client.GetAsync("/api/health"));

        Assert.Equal("ok", body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("migrations").GetInt32() >= 1);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("schema").GetString()));
    }

    [Fact]
    public async Task Status_DescribesTheSourceAndTags()
    {
        await SeedOneExcursion();

        var body = await Json(await _client.GetAsync("/api/status"));

        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("source").GetString()));
        Assert.True(body.GetProperty("nowUtc").GetInt64() > 0);
        Assert.NotEmpty(body.GetProperty("tags").EnumerateArray());
        Assert.NotEmpty(body.GetProperty("alertChannels").EnumerateArray());

        var tag = body.GetProperty("tags").EnumerateArray().First();
        Assert.True(tag.GetProperty("isHome").GetBoolean());
        Assert.True(tag.GetProperty("distanceFromHomeM").GetDouble() < 30);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/css/app.css")]
    [InlineData("/js/app.js")]
    [InlineData("/vendor/leaflet.js")]
    [InlineData("/vendor/leaflet.css")]
    [InlineData("/manifest.webmanifest")]
    public async Task StaticFiles_ServeTheApp(string path)
    {
        // The whole UI is static assets. When the content root is wrong the API answers happily
        // while every one of these 404s, which is a miserable thing to discover by hand.
        var response = await _client.GetAsync(path);

        response.EnsureSuccessStatusCode();
        Assert.True((await response.Content.ReadAsByteArrayAsync()).Length > 0, $"{path} was empty");
    }

    [Fact]
    public async Task TheIndexPage_IsTheApp() =>
        Assert.Contains("CatTracker", await _client.GetStringAsync("/"));

    [Fact]
    public async Task Logs_AreReadableOverHttp()
    {
        var body = await Json(await _client.GetAsync("/api/logs?lines=20"));

        Assert.True(body.TryGetProperty("lines", out _));
        Assert.True(body.TryGetProperty("files", out _));
    }

    [Fact]
    public async Task TestAlert_ExercisesTheNotificationPath()
    {
        var body = await Json(await _client.PostAsync("/api/alerts/test", null));

        Assert.True(body.GetProperty("id").GetInt64() > 0);
        // The log channel is always available, whatever the OS.
        Assert.NotEmpty(body.GetProperty("channels").EnumerateArray());
    }

    [Fact]
    public async Task Alerts_ComeBackAsAList()
    {
        await _client.PostAsync("/api/alerts/test", null);

        var body = await Json(await _client.GetAsync("/api/alerts?limit=5"));
        Assert.NotEmpty(body.EnumerateArray());
    }

    // ---- zones -------------------------------------------------------------------------------

    [Fact]
    public async Task Zones_SupportTheFullLifecycle()
    {
        var created = await _client.PostAsJsonAsync("/api/zones", new
        {
            name = "Neighbour's shed",
            kind = "Hazard",
            centerLat = 52.0910,
            centerLon = 5.1220,
            radiusM = 20.0,
            exitBufferM = 10.0,
            notifyOnExit = false,
            notifyOnEnter = true,
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await Json(created)).GetProperty("id").GetInt64();

        var updated = await _client.PutAsJsonAsync($"/api/zones/{id}", new
        {
            name = "Shed",
            kind = "Watch",
            centerLat = 52.0910,
            centerLon = 5.1220,
            radiusM = 35.0,
            exitBufferM = 15.0,
            notifyOnExit = true,
            notifyOnEnter = true,
        });

        Assert.Equal(35, (await Json(updated)).GetProperty("radiusM").GetDouble());

        var deleted = await _client.DeleteAsync($"/api/zones/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/zones/{id}")).StatusCode);
    }

    [Fact]
    public async Task Zones_RejectAZeroRadius()
    {
        var response = await _client.PostAsJsonAsync("/api/zones", new
        {
            name = "Broken", kind = "Watch", centerLat = 52.0, centerLon = 5.0, radiusM = 0.0,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("radiusM", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Zones_RejectAnUnknownKind()
    {
        var response = await _client.PostAsJsonAsync("/api/zones", new
        {
            name = "Broken", kind = "Volcano", centerLat = 52.0, centerLon = 5.0, radiusM = 30.0,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UpdatingAMissingZone_Is404()
    {
        var response = await _client.PutAsJsonAsync("/api/zones/424242", new
        {
            name = "Ghost", kind = "Watch", centerLat = 52.0, centerLon = 5.0, radiusM = 30.0,
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- tags --------------------------------------------------------------------------------

    [Fact]
    public async Task Tags_CanBeRenamed()
    {
        var tagId = await SeedOneExcursion();

        var response = await _client.PatchAsJsonAsync($"/api/tags/{tagId}", new { petName = "Pluisje" });

        Assert.Equal("Pluisje", (await Json(response)).GetProperty("petName").GetString());

        // Put it back so the shared fixture stays predictable for other tests.
        await _client.PatchAsJsonAsync($"/api/tags/{tagId}", new { petName = "Pluis" });
    }

    [Fact]
    public async Task RenamingAMissingTag_Is404()
    {
        var response = await _client.PatchAsJsonAsync("/api/tags/424242", new { petName = "Ghost" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AnEmptyPetName_IsRejected()
    {
        var tagId = await SeedOneExcursion();

        var response = await _client.PatchAsJsonAsync($"/api/tags/{tagId}", new { petName = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- history and statistics ----------------------------------------------------------------

    [Fact]
    public async Task Fixes_ComeBackForTheRequestedWindow()
    {
        var tagId = await SeedOneExcursion();

        var body = await Json(await _client.GetAsync($"/api/fixes?tagId={tagId}"));

        Assert.NotEmpty(body.EnumerateArray());
        Assert.True(body.EnumerateArray().First().GetProperty("latitude").GetDouble() > 50);
    }

    [Fact]
    public async Task Excursions_AndEvents_AreRecorded()
    {
        var tagId = await SeedOneExcursion();

        var excursions = await Json(await _client.GetAsync($"/api/excursions?tagId={tagId}"));
        var events = await Json(await _client.GetAsync($"/api/events?tagId={tagId}"));

        Assert.NotEmpty(excursions.EnumerateArray());
        Assert.NotEmpty(events.EnumerateArray());

        var excursion = excursions.EnumerateArray().First();
        Assert.True(excursion.GetProperty("maxDistanceM").GetDouble() > 300);
    }

    [Fact]
    public async Task DailyStats_ReportObservedAndUpperBound()
    {
        var tagId = await SeedOneExcursion();

        var body = await Json(await _client.GetAsync($"/api/stats/daily?tagId={tagId}&days=2"));
        var day = body.EnumerateArray().Last();

        Assert.True(day.GetProperty("upperBoundOutdoorMs").GetInt64() > 0);
        Assert.True(day.GetProperty("observedOutdoorMs").GetInt64()
                    <= day.GetProperty("upperBoundOutdoorMs").GetInt64());
    }

    [Fact]
    public async Task Heatmap_ClustersAndRhythm_AllRespond()
    {
        var tagId = await SeedOneExcursion();

        Assert.NotEmpty((await Json(await _client.GetAsync($"/api/stats/heatmap?tagId={tagId}"))).EnumerateArray());

        var clusters = await Json(await _client.GetAsync($"/api/stats/clusters?tagId={tagId}&minPoints=2&eps=50"));
        Assert.True(clusters.ValueKind == JsonValueKind.Array);

        var rhythm = await Json(await _client.GetAsync($"/api/stats/rhythm?tagId={tagId}"));
        Assert.Equal(24, rhythm.GetProperty("departures").GetArrayLength());
    }

    [Fact]
    public async Task Roaming_ReportsDistancesAndCoverage()
    {
        var tagId = await SeedOneExcursion();

        var body = await Json(await _client.GetAsync($"/api/stats/roaming?tagId={tagId}"));

        Assert.True(body.GetProperty("roaming").GetProperty("maxDistanceM").GetDouble() > 300);
        Assert.InRange(body.GetProperty("coverage").GetDouble(), 0, 1);
    }

    // ---- tiles -------------------------------------------------------------------------------

    [Fact]
    public async Task TileStatus_ReportsAnEmptyCache()
    {
        var body = await Json(await _client.GetAsync("/api/tiles/status"));

        Assert.Equal(0, body.GetProperty("cachedTiles").GetInt64());
        Assert.False(body.GetProperty("seeding").GetProperty("running").GetBoolean());
    }

    [Fact]
    public async Task AnUncachedTile_Is404_WhenTheNetworkIsOff()
    {
        // This is what "fully local" looks like: no cached tile, no request to OpenStreetMap.
        var response = await _client.GetAsync("/tiles/16/33615/21489.png");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ACachedTile_IsServedFromDisk()
    {
        var png = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3 };
        Service<TileCache>().Store(new TileRef(16, 1, 1), png);

        var response = await _client.GetAsync("/tiles/16/1/1.png");

        response.EnsureSuccessStatusCode();
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(png, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task AnAbsurdZoom_Is404() =>
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/tiles/44/1/1.png")).StatusCode);

    [Fact]
    public async Task Seeding_IsRefusedWhenTheNetworkIsOff()
    {
        var response = await _client.PostAsJsonAsync("/api/tiles/seed", new
        {
            minLat = 52.08, minLon = 5.11, maxLat = 52.10, maxLon = 5.14, minZoom = 14, maxZoom = 15,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("AllowNetwork", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Seeding_RejectsInvertedBounds()
    {
        var response = await _client.PostAsJsonAsync("/api/tiles/seed", new
        {
            minLat = 52.20, minLon = 5.30, maxLat = 52.10, maxLon = 5.14, minZoom = 14, maxZoom = 15,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
