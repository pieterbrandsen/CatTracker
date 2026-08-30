namespace CatTracker.Core;

/// <summary>
/// One tracked accessory as it appears in the Find My cache. This is the raw, untrusted shape:
/// everything except the serial number is optional, because the cache is undocumented and a
/// macOS update may drop or rename fields at any time.
/// </summary>
public sealed record FindMyItem(
    string SerialNumber,
    string Name,
    int? BatteryStatus,
    FindMyLocation? Location);

public sealed record FindMyLocation(
    double Latitude,
    double Longitude,
    double? HorizontalAccuracy,
    double? Altitude,
    string? PositionType,
    bool IsOld,
    bool IsInaccurate,
    long TimestampUtcMs);

public sealed class Tag
{
    public long Id { get; set; }
    public string SerialNumber { get; set; } = "";
    public string FindMyName { get; set; } = "";
    public string PetName { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public long CreatedUtc { get; set; }
}

/// <summary>A single stored position report. Timestamps are Unix milliseconds, UTC.</summary>
public sealed class Fix
{
    public long Id { get; set; }
    public long TagId { get; set; }
    public long TimestampUtc { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double? HorizontalAccuracy { get; set; }
    public double? Altitude { get; set; }
    public string? PositionType { get; set; }
    public bool IsOld { get; set; }
    public bool IsInaccurate { get; set; }
    public int? BatteryStatus { get; set; }
    public long IngestedUtc { get; set; }

    public DateTimeOffset At => DateTimeOffset.FromUnixTimeMilliseconds(TimestampUtc);
}

public enum ZoneKind { Home, Watch, Hazard }

public sealed class Zone
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public ZoneKind Kind { get; set; } = ZoneKind.Watch;
    public double CenterLat { get; set; }
    public double CenterLon { get; set; }
    public double RadiusM { get; set; } = 30;

    /// <summary>
    /// Hysteresis dead band. Leaving requires distance &gt; Radius + ExitBuffer; returning requires
    /// distance &lt;= Radius. Without this an AirTag jitters across the boundary all night.
    /// </summary>
    public double ExitBufferM { get; set; } = 25;

    public bool NotifyOnExit { get; set; } = true;
    public bool NotifyOnEnter { get; set; } = true;
}

public enum ZoneEventType { Enter, Exit }

public sealed class ZoneEvent
{
    public long Id { get; set; }
    public long TagId { get; set; }
    public long ZoneId { get; set; }
    public ZoneEventType EventType { get; set; }
    public long FixId { get; set; }
    public long OccurredUtc { get; set; }
}

public sealed class Excursion
{
    public long Id { get; set; }
    public long TagId { get; set; }
    public long DepartedUtc { get; set; }
    public long? ReturnedUtc { get; set; }
    public double MaxDistanceM { get; set; }
    public int FixCount { get; set; }

    /// <summary>Fraction of the excursion we actually observed; see <see cref="Stats"/>.</summary>
    public double CoverageRatio { get; set; }

    public bool IsOpen => ReturnedUtc is null;
}

public enum AlertKind
{
    ZoneExit,
    ZoneEnter,
    LowBattery,
    DataStale,
    ReaderProblem,
}

public sealed class Alert
{
    public long Id { get; set; }
    public AlertKind Kind { get; set; }
    public string Message { get; set; } = "";
    public long RaisedUtc { get; set; }
    public long? DeliveredUtc { get; set; }
}

/// <summary>
/// What the privileged reader last managed to do. Written by CatTracker.Reader, consumed by the
/// app so the UI can distinguish "the cat is quietly at home" from "we are blind".
/// </summary>
public sealed record ReaderHeartbeat(
    long WrittenUtcMs,
    string Status,
    string? Detail,
    long? SourceMTimeUtcMs,
    long? SourceSizeBytes)
{
    public const string Ok = "ok";
    public const string PermissionDenied = "permission_denied";
    public const string NotFound = "not_found";
    public const string Error = "error";
}
