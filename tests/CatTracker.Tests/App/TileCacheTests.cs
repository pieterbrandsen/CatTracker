using System.Net;
using CatTracker.App.Alerting;
using CatTracker.Core;
using CatTracker.App.Services;
using CatTracker.Data;

namespace CatTracker.Tests.App;

/// <summary>Answers every tile request without touching the network.</summary>
internal sealed class StubTileHandler(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
{
    public int Requests { get; private set; }
    public List<string> Urls { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        Urls.Add(request.RequestUri!.ToString());

        return Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent([0x89, (byte)'P', (byte)'N', (byte)'G']),
        });
    }
}

public class TileCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "cattracker-tests", Guid.NewGuid().ToString("N"));

    private readonly TileContextFactory _factory;

    public TileCacheTests()
    {
        Directory.CreateDirectory(_directory);
        _factory = new TileContextFactory(Path.Combine(_directory, "tiles.db"));
        TileSetup.Ensure(_factory);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private TileCache NewCache(HttpMessageHandler handler, bool allowNetwork = true, int cap = 20_000) =>
        new(_factory,
            new HttpClient(handler),
            Build.Options(o =>
            {
                o.Tiles.AllowNetwork = allowNetwork;
                o.Tiles.MaxSeedTiles = cap;
                o.Tiles.SeedRatePerSecond = 0; // no artificial delay in tests
                o.Tiles.UpstreamUrl = "https://tiles.invalid/{z}/{x}/{y}.png";
            }),
            Build.Logger<TileCache>());

    [Fact]
    public async Task AMissingTile_IsFetchedOnceAndThenServedFromDisk()
    {
        var handler = new StubTileHandler();
        var cache = NewCache(handler);

        var first = await cache.GetAsync(new TileRef(16, 33615, 21489), CancellationToken.None);
        var second = await cache.GetAsync(new TileRef(16, 33615, 21489), CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Equal(1, handler.Requests);
        Assert.Equal("https://tiles.invalid/16/33615/21489.png", handler.Urls[0]);
    }

    [Fact]
    public async Task WithTheNetworkOff_OnlyCachedTilesAreServed()
    {
        var handler = new StubTileHandler();
        var cache = NewCache(handler, allowNetwork: false);

        // This is what "fully local" means: no cached tile, and no request leaves the machine.
        Assert.Null(await cache.GetAsync(new TileRef(16, 1, 1), CancellationToken.None));
        Assert.Equal(0, handler.Requests);

        cache.Store(new TileRef(16, 1, 1), [1, 2, 3]);
        Assert.Equal([1, 2, 3], await cache.GetAsync(new TileRef(16, 1, 1), CancellationToken.None));
    }

    [Fact]
    public async Task AnAbandonedTileRequest_IsNotAnError()
    {
        // Every pan and zoom cancels the tiles still in flight. That is routine, and must not
        // surface as an unhandled exception and a 500 in the log.
        var cache = NewCache(new StubTileHandler());

        using var abandoned = new CancellationTokenSource();
        await abandoned.CancelAsync();

        Assert.Null(await cache.GetAsync(new TileRef(16, 5, 5), abandoned.Token));
    }

    [Fact]
    public async Task AnUpstreamErrorIsNotCached()
    {
        var handler = new StubTileHandler(HttpStatusCode.TooManyRequests);
        var cache = NewCache(handler);

        Assert.Null(await cache.GetAsync(new TileRef(16, 1, 1), CancellationToken.None));
        Assert.Null(cache.TryGetCached(new TileRef(16, 1, 1)));
    }

    [Fact]
    public void Store_OverwritesAnExistingTile()
    {
        var cache = NewCache(new StubTileHandler());

        cache.Store(new TileRef(16, 1, 1), [1]);
        cache.Store(new TileRef(16, 1, 1), [2, 2]);

        Assert.Equal([2, 2], cache.TryGetCached(new TileRef(16, 1, 1)));
        Assert.Equal(1, cache.Statistics().Count);
    }

    [Fact]
    public void Statistics_CountTilesAndBytes()
    {
        var cache = NewCache(new StubTileHandler());

        Assert.Equal((0L, 0L), cache.Statistics());

        cache.Store(new TileRef(16, 1, 1), [1, 2, 3]);
        cache.Store(new TileRef(16, 1, 2), [1, 2]);

        Assert.Equal((2L, 5L), cache.Statistics());
    }

    [Fact]
    public async Task Seeding_FetchesTheWholeArea()
    {
        var handler = new StubTileHandler();
        var cache = NewCache(handler);
        var state = new TileSeedState();

        var bounds = new Bounds(52.089, 5.120, 52.092, 5.124);
        var expected = TileMath.Count(bounds, 16, 16);

        await cache.SeedAsync(bounds, 16, 16, state, CancellationToken.None);

        Assert.False(state.Running);
        Assert.Equal(expected, state.Done);
        Assert.Equal(0, state.Failed);
        Assert.Equal(expected, cache.Statistics().Count);
    }

    [Fact]
    public async Task Seeding_StopsAtTheCap()
    {
        // OSM runs on donations and their tile policy forbids bulk downloading, so a stray
        // bounding box must not be able to hammer them.
        var handler = new StubTileHandler();
        var cache = NewCache(handler, cap: 5);
        var state = new TileSeedState();

        await cache.SeedAsync(new Bounds(52.0, 5.0, 52.3, 5.4), 16, 16, state, CancellationToken.None);

        Assert.Equal(5, state.Total);
        Assert.Equal(5, state.Done);
        Assert.True(handler.Requests <= 5);
        Assert.Contains("capped at", state.Message);
    }

    [Fact]
    public async Task Seeding_SkipsTilesItAlreadyHas()
    {
        var handler = new StubTileHandler();
        var cache = NewCache(handler);
        var bounds = new Bounds(52.089, 5.120, 52.092, 5.124);

        await cache.SeedAsync(bounds, 16, 16, new TileSeedState(), CancellationToken.None);
        var afterFirstRun = handler.Requests;

        var second = new TileSeedState();
        await cache.SeedAsync(bounds, 16, 16, second, CancellationToken.None);

        Assert.Equal(afterFirstRun, handler.Requests);
        Assert.Equal(second.Done, second.Cached);
    }

    [Fact]
    public async Task Seeding_RecordsFailuresRatherThanThrowing()
    {
        var cache = NewCache(new StubTileHandler(HttpStatusCode.InternalServerError));
        var state = new TileSeedState();

        await cache.SeedAsync(new Bounds(52.089, 5.120, 52.092, 5.124), 16, 16, state, CancellationToken.None);

        Assert.True(state.Failed > 0);
        Assert.Equal(0, state.Done);
        Assert.False(state.Running);
    }

    [Fact]
    public async Task Seeding_HonoursCancellation()
    {
        var cache = NewCache(new StubTileHandler());
        var state = new TileSeedState();

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await cache.SeedAsync(new Bounds(52.0, 5.0, 52.3, 5.4), 16, 16, state, cancelled.Token);

        Assert.False(state.Running);
        Assert.Equal(0, state.Done);
    }
}

public class AlertChannelTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("she said \"out\"", "she said \\\"out\\\"")]
    [InlineData("back\\slash", "back\\\\slash")]
    [InlineData("two\nlines", "two lines")]
    public void AppleScriptEscape_MakesAStringSafeToEmbed(string input, string expected) =>
        // An unescaped quote in a pet name would turn a notification into a syntax error.
        Assert.Equal(expected, AppleScript.Escape(input));

    [Fact]
    public void MacChannels_AreUnavailableOffMacOs()
    {
        var options = Build.Options();

        var notification = new MacNotificationChannel(options, Build.Logger<MacNotificationChannel>());
        var sound = new SoundAlertChannel(options, Build.Logger<SoundAlertChannel>());
        var imessage = new IMessageAlertChannel(options, Build.Logger<IMessageAlertChannel>());

        Assert.Equal(OperatingSystem.IsMacOS(), notification.IsAvailable);
        Assert.Equal(OperatingSystem.IsMacOS(), sound.IsAvailable);
        // iMessage additionally needs a configured recipient.
        Assert.False(imessage.IsAvailable);

        Assert.Equal("macos-notification", notification.Name);
        Assert.Equal("sound", sound.Name);
        Assert.Equal("imessage", imessage.Name);
    }

    [Fact]
    public void MacChannels_RespectTheirConfigSwitches()
    {
        var off = Build.Options(o =>
        {
            o.Alerts.MacNotifications = false;
            o.Alerts.Sound = false;
        });

        Assert.False(new MacNotificationChannel(off, Build.Logger<MacNotificationChannel>()).IsAvailable);
        Assert.False(new SoundAlertChannel(off, Build.Logger<SoundAlertChannel>()).IsAvailable);
    }

    [Fact]
    public async Task TheLogChannel_IsAlwaysAvailable()
    {
        var channel = new LogAlertChannel(Build.Logger<LogAlertChannel>());

        Assert.True(channel.IsAvailable);
        Assert.Equal("log", channel.Name);

        await channel.SendAsync(
            new Alert { Kind = AlertKind.ZoneExit, Message = "out" }, CancellationToken.None);
    }
}
