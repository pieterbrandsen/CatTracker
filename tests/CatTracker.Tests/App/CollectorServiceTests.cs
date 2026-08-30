using CatTracker.App;
using CatTracker.App.Alerting;
using CatTracker.App.Readers;
using CatTracker.App.Services;
using CatTracker.Core;
using Microsoft.Extensions.Options;

namespace CatTracker.Tests.App;

/// <summary>A reader we can drive by hand, standing in for the spool or the Mac.</summary>
internal sealed class StubReader : IFindMyReader
{
    public string? NextJson { get; set; }
    public ReaderHeartbeat? Heartbeat { get; set; }
    public int Reads { get; private set; }

    public string Description => "stub";

    public Task<FindMySnapshot?> TryReadAsync(CancellationToken cancellationToken)
    {
        Reads++;
        if (NextJson is null) return Task.FromResult<FindMySnapshot?>(null);

        var snapshot = new FindMySnapshot(NextJson, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        NextJson = null; // a real reader only returns a payload when something changed
        return Task.FromResult<FindMySnapshot?>(snapshot);
    }

    public Task<ReaderHeartbeat?> ReadHeartbeatAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Heartbeat);
}

public class CollectorServiceTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly RecordingChannel _channel = new();
    private readonly StubReader _reader = new();
    private readonly CollectorState _state = new();
    private readonly CollectorService _collector;

    public CollectorServiceTests()
    {
        var options = Build.Options(o =>
        {
            o.Geofence.ConfirmationFixes = 1;
            o.FindMy.StaleAfterMinutes = 45;
            o.FindMy.KeepRawSnapshots = 5;
        });

        var dispatcher = new AlertDispatcher(
            _db.Repository, [_channel], options, Build.Logger<AlertDispatcher>());

        var processor = new FixProcessor(
            _db.Repository, dispatcher, options, Build.Logger<FixProcessor>());

        var ingestor = new Ingestor(_db.Repository, processor, Build.Logger<Ingestor>());
        var seeder = new DemoDataSeeder(_db.Repository, processor, options, Build.Logger<DemoDataSeeder>());

        _collector = new CollectorService(
            _reader, ingestor, _db.Repository, dispatcher, _state, seeder, options,
            Build.Logger<CollectorService>());
    }

    public void Dispose() => _db.Dispose();

    // Placeholder rather than interpolation: JSON is all braces, and raw-string interpolation
    // turns every literal }} into an escape.
    private static string PayloadAt(long timestamp) =>
        """
        [{"name":"Pluis","serialNumber":"HK1","batteryStatus":1,
          "location":{"latitude":52.0907,"longitude":5.1214,"timeStamp":__TS__,
                      "horizontalAccuracy":10,"isOld":false,"isInaccurate":false}}]
        """.Replace("__TS__", timestamp.ToString());

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static long HoursAgo(double hours) => Now - (long)TimeSpan.FromHours(hours).TotalMilliseconds;

    [Fact]
    public async Task Poll_StoresWhatTheReaderOffers()
    {
        _reader.NextJson = PayloadAt(Now);

        await _collector.PollAsync(CancellationToken.None);

        Assert.Single(_db.Repository.ListTags());
        Assert.True(_state.LastNewFixUtc > 0);
        Assert.True(_state.LastPollUtc > 0);
        Assert.Empty(_state.LastWarnings);
    }

    [Fact]
    public async Task Poll_DoesNothingWhenTheSourceIsUnchanged()
    {
        await _collector.PollAsync(CancellationToken.None);

        Assert.Empty(_db.Repository.ListTags());
        Assert.Equal(1, _reader.Reads);
    }

    [Fact]
    public async Task Poll_KeepsRawSnapshotsForDebugging()
    {
        _reader.NextJson = PayloadAt(Now);
        await _collector.PollAsync(CancellationToken.None);

        using var context = _db.Factory.CreateDbContext();
        Assert.Equal(1, context.RawSnapshots.Count());
    }

    [Fact]
    public async Task Poll_RaisesAnAlertWhenTheCacheLooksWrong()
    {
        _reader.NextJson = """[{"name":"no serial here"}]""";

        await _collector.PollAsync(CancellationToken.None);

        var alert = Assert.Single(_channel.Sent);
        Assert.Equal(AlertKind.ReaderProblem, alert.Kind);
        Assert.NotEmpty(_state.LastWarnings);
    }

    [Fact]
    public async Task Silence_IsReportedLoudly()
    {
        // A dead reader and a cat asleep on the sofa look identical on a map. Only an alert
        // distinguishes them, which is why this is a feature rather than polish.
        _reader.NextJson = PayloadAt(HoursAgo(3));
        _reader.Heartbeat = new ReaderHeartbeat(Now, ReaderHeartbeat.Ok, "unchanged", null, null);

        await _collector.PollAsync(CancellationToken.None);

        var alert = Assert.Single(_channel.Sent, a => a.Kind == AlertKind.DataStale);
        Assert.Contains("No new position", alert.Message);
        Assert.Contains("Find My itself has stopped refreshing", alert.Message);
        Assert.True(_state.IsStale);
    }

    [Fact]
    public async Task Staleness_IsOnlyAnnouncedOnce()
    {
        _reader.NextJson = PayloadAt(HoursAgo(3));

        await _collector.PollAsync(CancellationToken.None);
        await _collector.PollAsync(CancellationToken.None);

        Assert.Single(_channel.Sent, a => a.Kind == AlertKind.DataStale);
    }

    [Fact]
    public async Task RecoveringContact_IsAnnouncedToo()
    {
        _reader.NextJson = PayloadAt(HoursAgo(3));
        await _collector.PollAsync(CancellationToken.None);
        Assert.True(_state.IsStale);

        _reader.NextJson = PayloadAt(Now);
        await _collector.PollAsync(CancellationToken.None);

        Assert.False(_state.IsStale);
        Assert.Contains(_channel.Sent, a => a.Message.Contains("Contact restored"));
    }

    [Fact]
    public async Task ADeadReader_IsNamedInTheAlert()
    {
        _reader.NextJson = PayloadAt(HoursAgo(3));
        _reader.Heartbeat = null;

        await _collector.PollAsync(CancellationToken.None);

        Assert.Contains("reader agent looks dead",
            Assert.Single(_channel.Sent, a => a.Kind == AlertKind.DataStale).Message);
    }

    [Fact]
    public async Task MissingFullDiskAccess_IsNamedInTheAlert()
    {
        _reader.NextJson = PayloadAt(HoursAgo(3));
        _reader.Heartbeat = new ReaderHeartbeat(Now, ReaderHeartbeat.PermissionDenied, "denied", null, null);

        await _collector.PollAsync(CancellationToken.None);

        Assert.Contains("Full Disk Access",
            Assert.Single(_channel.Sent, a => a.Kind == AlertKind.DataStale).Message);
    }

    [Fact]
    public async Task AStoppedReaderAgent_IsNamedInTheAlert()
    {
        _reader.NextJson = PayloadAt(HoursAgo(3));
        _reader.Heartbeat = new ReaderHeartbeat(
            Now - (long)TimeSpan.FromMinutes(30).TotalMilliseconds, ReaderHeartbeat.Ok, null, null, null);

        await _collector.PollAsync(CancellationToken.None);

        Assert.Contains("has stopped",
            Assert.Single(_channel.Sent, a => a.Kind == AlertKind.DataStale).Message);
    }

    [Fact]
    public async Task AMissingCacheFile_IsNamedInTheAlert()
    {
        _reader.NextJson = PayloadAt(HoursAgo(3));
        _reader.Heartbeat = new ReaderHeartbeat(Now, ReaderHeartbeat.NotFound, null, null, null);

        await _collector.PollAsync(CancellationToken.None);

        Assert.Contains("cache file is missing",
            Assert.Single(_channel.Sent, a => a.Kind == AlertKind.DataStale).Message);
    }

    [Fact]
    public async Task AReaderError_IsNamedInTheAlert()
    {
        _reader.NextJson = PayloadAt(HoursAgo(3));
        _reader.Heartbeat = new ReaderHeartbeat(Now, ReaderHeartbeat.Error, "disk on fire", null, null);

        await _collector.PollAsync(CancellationToken.None);

        Assert.Contains("disk on fire",
            Assert.Single(_channel.Sent, a => a.Kind == AlertKind.DataStale).Message);
    }

    [Fact]
    public async Task NoDataYet_IsSetupRatherThanFailure()
    {
        // Nagging before the first fix has ever arrived would train you to ignore the alert.
        await _collector.PollAsync(CancellationToken.None);

        Assert.False(_state.IsStale);
        Assert.Empty(_channel.Sent);
    }
}
