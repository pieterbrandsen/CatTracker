using System.Text.Json;
using CatTracker.App.Services;
using CatTracker.Core;
using Microsoft.Extensions.Options;

namespace CatTracker.App.Readers;

public static class ReplayConstants
{
    public const string SerialNumber = "DEMOCAT0001";
}

/// <summary>
/// Emits synthetic cache payloads in the same JSON shape as the real Find My cache, so the parser
/// and everything downstream run exactly as they will on the Mac.
/// </summary>
public sealed class ReplayFindMyReader : IFindMyReader
{
    private readonly CatSimulator _simulator;
    private readonly string _petName;

    public ReplayFindMyReader(IOptions<AppOptions> options)
    {
        var replay = options.Value.Replay;
        _petName = replay.PetName;
        _simulator = new CatSimulator(
            replay.HomeLat, replay.HomeLon, replay.Seed,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public string Description => "Replay (synthetic cat — no Apple hardware involved)";

    public Task<FindMySnapshot?> TryReadAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        SimFix? latest = null;
        foreach (var fix in _simulator.Advance(now)) latest = fix;

        if (latest is null) return Task.FromResult<FindMySnapshot?>(null);

        return Task.FromResult<FindMySnapshot?>(
            new FindMySnapshot(ToCacheJson(latest, _petName), now));
    }

    public Task<ReaderHeartbeat?> ReadHeartbeatAsync(CancellationToken cancellationToken) =>
        Task.FromResult<ReaderHeartbeat?>(new ReaderHeartbeat(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ReaderHeartbeat.Ok,
            "replay source",
            null,
            null));

    /// <summary>Mirrors the observed shape of Items.data closely enough to exercise the parser.</summary>
    public static string ToCacheJson(SimFix fix, string name) =>
        JsonSerializer.Serialize(new[]
        {
            new
            {
                name,
                serialNumber = ReplayConstants.SerialNumber,
                batteryStatus = fix.BatteryStatus,
                productType = new { type = "b389", productInformation = new { productName = "AirTag" } },
                role = new { name = "Pet", emoji = "🐈" },
                location = new
                {
                    latitude = fix.Lat,
                    longitude = fix.Lon,
                    timeStamp = fix.TimestampUtcMs,
                    horizontalAccuracy = fix.Accuracy,
                    verticalAccuracy = 0.0,
                    altitude = 0.0,
                    floorLevel = 0,
                    isOld = fix.IsOld,
                    isInaccurate = fix.IsInaccurate,
                    positionType = "crowdsourced",
                    locationFinished = true,
                },
                address = new { label = "Somewhere pleasant", country = "Netherlands" },
            },
        });
}
