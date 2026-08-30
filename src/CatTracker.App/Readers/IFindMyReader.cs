using CatTracker.Core;

namespace CatTracker.App.Readers;

public sealed record FindMySnapshot(string Json, long CapturedUtcMs);

/// <summary>
/// Where position data comes from. The abstraction exists for one practical reason: this project
/// is developed on Windows and only ever runs for real on macOS. A replay implementation means
/// geofencing, excursions and every chart can be exercised end to end without Apple hardware.
/// </summary>
public interface IFindMyReader
{
    /// <summary>Human-readable description of the source, shown on the status page.</summary>
    string Description { get; }

    /// <summary>Returns a snapshot, or null when nothing has changed since the last read.</summary>
    Task<FindMySnapshot?> TryReadAsync(CancellationToken cancellationToken);

    /// <summary>Health of the upstream reader, when there is one.</summary>
    Task<ReaderHeartbeat?> ReadHeartbeatAsync(CancellationToken cancellationToken);
}
