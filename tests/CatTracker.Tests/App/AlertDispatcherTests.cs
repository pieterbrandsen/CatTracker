using CatTracker.App.Alerting;
using CatTracker.Core;

namespace CatTracker.Tests.App;

public class AlertDispatcherTests : IDisposable
{
    private readonly TestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    private AlertDispatcher Dispatcher(params IAlertChannel[] channels) =>
        new(_db.Repository, channels, Build.Options(), Build.Logger<AlertDispatcher>());

    [Fact]
    public async Task RaiseAsync_RecordsAndDelivers()
    {
        var channel = new RecordingChannel();
        var dispatcher = Dispatcher(channel);

        var alert = await dispatcher.RaiseAsync(AlertKind.ZoneExit, "Pluis has left Home.", "zone:1:Exit");

        Assert.True(alert.Id > 0);
        Assert.NotNull(alert.DeliveredUtc);
        Assert.Equal("Pluis has left Home.", Assert.Single(channel.Sent).Message);
        Assert.Single(_db.Repository.RecentAlerts());
    }

    [Fact]
    public async Task RaiseAsync_SkipsChannelsThatAreUnavailable()
    {
        var channel = new RecordingChannel { IsAvailable = false };
        await Dispatcher(channel).RaiseAsync(AlertKind.ZoneExit, "out", "key");

        Assert.Empty(channel.Sent);
        // Still recorded: the log of what happened should not depend on what could shout about it.
        Assert.Single(_db.Repository.RecentAlerts());
    }

    [Fact]
    public async Task Cooldown_SuppressesDeliveryButNotTheRecord()
    {
        var channel = new RecordingChannel();
        var dispatcher = Dispatcher(channel);

        await dispatcher.RaiseAsync(AlertKind.DataStale, "quiet", "stale", TimeSpan.FromHours(1));
        var second = await dispatcher.RaiseAsync(AlertKind.DataStale, "quiet", "stale", TimeSpan.FromHours(1));

        // An alert that repeats every ten seconds is one you learn to ignore.
        Assert.Single(channel.Sent);
        Assert.Null(second.DeliveredUtc);
        Assert.Equal(2, _db.Repository.RecentAlerts().Count);
    }

    [Fact]
    public async Task Cooldown_IsPerKey()
    {
        var channel = new RecordingChannel();
        var dispatcher = Dispatcher(channel);

        await dispatcher.RaiseAsync(AlertKind.ZoneExit, "a", "zone:1:Exit", TimeSpan.FromHours(1));
        await dispatcher.RaiseAsync(AlertKind.ZoneExit, "b", "zone:2:Exit", TimeSpan.FromHours(1));

        Assert.Equal(2, channel.Sent.Count);
    }

    [Fact]
    public async Task ZeroCooldown_NeverSuppresses()
    {
        var channel = new RecordingChannel();
        var dispatcher = Dispatcher(channel);

        await dispatcher.RaiseAsync(AlertKind.ReaderProblem, "x", "test", TimeSpan.Zero);
        await dispatcher.RaiseAsync(AlertKind.ReaderProblem, "x", "test", TimeSpan.Zero);

        Assert.Equal(2, channel.Sent.Count);
    }

    [Fact]
    public async Task DeliverFalse_RecordsSilently()
    {
        var channel = new RecordingChannel();

        var alert = await Dispatcher(channel)
            .RaiseAsync(AlertKind.ZoneExit, "backfill", "key", deliver: false);

        Assert.Empty(channel.Sent);
        Assert.Null(alert.DeliveredUtc);
        Assert.Single(_db.Repository.RecentAlerts());
    }

    [Fact]
    public async Task ABrokenChannel_DoesNotStopTheOthers()
    {
        var good = new RecordingChannel();
        var dispatcher = Dispatcher(new ThrowingChannel(), good);

        await dispatcher.RaiseAsync(AlertKind.ZoneExit, "out", "key");

        // One misconfigured channel must never take the collector down with it.
        Assert.Single(good.Sent);
    }

    [Fact]
    public void AvailableChannels_ListsOnlyUsableOnes()
    {
        var dispatcher = Dispatcher(
            new RecordingChannel(), new RecordingChannel { IsAvailable = false });

        Assert.Equal("recording", Assert.Single(dispatcher.AvailableChannels()));
    }
}
