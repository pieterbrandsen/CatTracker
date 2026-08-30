using CatTracker.Core;
using CatTracker.Data;

namespace CatTracker.App.Services;

public sealed record IngestResult(int ItemsSeen, int NewFixes, IReadOnlyList<string> Warnings);

/// <summary>Turns a raw cache payload into stored, processed fixes.</summary>
public sealed class Ingestor(
    Repository repository, FixProcessor processor, ILogger<Ingestor> logger)
{
    public async Task<IngestResult> IngestAsync(
        string json, bool notify, CancellationToken cancellationToken)
    {
        var parsed = FindMyParser.Parse(json);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var newFixes = 0;

        foreach (var item in parsed.Items)
        {
            var tag = repository.GetOrCreateTag(item.SerialNumber, item.Name, now);
            if (!tag.IsActive || item.Location is not { } location) continue;

            var fix = new Fix
            {
                TagId = tag.Id,
                TimestampUtc = location.TimestampUtcMs,
                Latitude = location.Latitude,
                Longitude = location.Longitude,
                HorizontalAccuracy = location.HorizontalAccuracy,
                Altitude = location.Altitude,
                PositionType = location.PositionType,
                IsOld = location.IsOld,
                IsInaccurate = location.IsInaccurate,
                BatteryStatus = item.BatteryStatus,
                IngestedUtc = now,
            };

            // Find My holds only the latest position, so the same fix is re-read on every poll.
            // A null here is the overwhelmingly common case, and means "nothing new happened".
            var stored = repository.TryInsertFix(fix);
            if (stored is null) continue;

            newFixes++;

            logger.LogInformation(
                "New fix for {Pet}: {Lat:F6},{Lon:F6} ±{Accuracy}m at {At:u}{Flags}",
                tag.PetName,
                stored.Latitude,
                stored.Longitude,
                stored.HorizontalAccuracy is { } accuracy ? Math.Round(accuracy) : (object)"?",
                stored.At.UtcDateTime,
                stored.IsOld || stored.IsInaccurate
                    ? $" [{(stored.IsOld ? "old " : "")}{(stored.IsInaccurate ? "inaccurate" : "")}".TrimEnd() + "]"
                    : "");

            await processor.ProcessAsync(tag, stored, notify, cancellationToken);
        }

        foreach (var warning in parsed.Warnings)
            logger.LogWarning("Find My cache: {Warning}", warning);

        return new IngestResult(parsed.Items.Count, newFixes, parsed.Warnings);
    }
}
