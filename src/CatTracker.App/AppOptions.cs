using System.Runtime.InteropServices;

namespace CatTracker.App;

public sealed class AppOptions
{
    public const string SectionName = "CatTracker";

    /// <summary>
    /// Everything mutable lives here, deliberately outside the install directory, so that
    /// updating CatTracker is "replace the binaries" and can never touch your data.
    /// </summary>
    public string DataDirectory { get; set; } = "";

    /// <summary>IANA or Windows time zone id. Empty means the machine's local zone.</summary>
    public string TimeZone { get; set; } = "";

    public FindMyOptions FindMy { get; set; } = new();
    public GeofenceSettings Geofence { get; set; } = new();
    public AlertOptions Alerts { get; set; } = new();
    public TileOptions Tiles { get; set; } = new();
    public ReplayOptions Replay { get; set; } = new();
    public DiagnosticsOptions Diagnostics { get; set; } = new();

    /// <summary>
    /// Where the database, logs, spool and your own settings live — deliberately outside the
    /// install directory on every platform, so an update replaces binaries and can never touch
    /// your history.
    /// </summary>
    public string ResolveDataDirectory()
    {
        if (!string.IsNullOrWhiteSpace(DataDirectory))
            return Path.GetFullPath(Expand(DataDirectory));

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return Path.Combine(Home(), "Library", "Application Support", "CatTracker");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // ProgramData, not LocalAppData: a Windows Service runs as LocalSystem, whose profile
            // is buried under System32 and is not where anyone would look for their database.
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!string.IsNullOrWhiteSpace(programData))
                return Path.Combine(programData, "CatTracker");
        }

        return Path.Combine(Environment.CurrentDirectory, ".data");
    }

    public TimeZoneInfo ResolveTimeZone()
    {
        if (string.IsNullOrWhiteSpace(TimeZone)) return TimeZoneInfo.Local;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }

    public static string Home() =>
        Environment.GetEnvironmentVariable("HOME")
        ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// Resolves both conventions a person might reasonably write in a config file or a service
    /// definition: a leading <c>~</c> for the home directory, and <c>%VARIABLE%</c> on Windows.
    /// </summary>
    public static string Expand(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;

        var expanded = path.Contains('%')
            ? Environment.ExpandEnvironmentVariables(path)
            : path;

        return expanded.StartsWith('~')
            ? Path.Combine(Home(), expanded.TrimStart('~', '/', '\\'))
            : expanded;
    }
}

public enum FindMySource
{
    /// <summary>Read what CatTracker.Reader spooled. The normal production choice.</summary>
    Spool,

    /// <summary>Read the Find My cache directly; requires Full Disk Access for this app.</summary>
    Direct,

    /// <summary>Synthesise a plausible cat. Lets the whole system run on Windows.</summary>
    Replay,
}

public sealed class FindMyOptions
{
    /// <summary>Runs the background poll loop. Off in tests, which drive ingestion directly.</summary>
    public bool Enabled { get; set; } = true;

    public FindMySource Source { get; set; } = FindMySource.Spool;

    /// <summary>Empty means &lt;DataDirectory&gt;/spool.</summary>
    public string SpoolDirectory { get; set; } = "";

    /// <summary>Only used when <see cref="Source"/> is Direct.</summary>
    public string DirectPath { get; set; } =
        "~/Library/Caches/com.apple.findmy.fmipcore/Items.data";

    public int PollSeconds { get; set; } = 10;

    /// <summary>
    /// How long without a new fix before we shout. Silence is the dangerous failure here: a dead
    /// reader and a sleeping cat look identical unless something actively complains.
    /// </summary>
    public int StaleAfterMinutes { get; set; } = 45;

    /// <summary>Raw cache payloads retained for debugging. 0 disables.</summary>
    public int KeepRawSnapshots { get; set; } = 200;
}

public sealed class GeofenceSettings
{
    public double MaxAccuracyMeters { get; set; } = 100;
    public int ConfirmationFixes { get; set; } = 2;
    public bool RejectOldFixes { get; set; } = true;

    public Core.GeofenceOptions ToCore() => new()
    {
        MaxAccuracyMeters = MaxAccuracyMeters,
        ConfirmationFixes = ConfirmationFixes,
        RejectOldFixes = RejectOldFixes,
    };
}

public sealed class AlertOptions
{
    public bool MacNotifications { get; set; } = true;
    public bool Sound { get; set; } = true;

    /// <summary>Any name from /System/Library/Sounds.</summary>
    public string SoundName { get; set; } = "Submarine";

    /// <summary>
    /// Phone number or Apple ID to iMessage. Empty disables it. This is the only channel that
    /// leaves the LAN, and it goes through your own Apple account rather than a third party.
    /// </summary>
    public string IMessageTo { get; set; } = "";

    public int CooldownMinutes { get; set; } = 60;

    /// <summary>
    /// The meaning of Find My's batteryStatus integer is undocumented. We therefore alert on any
    /// *change* (always meaningful) and additionally at or above this value. Confirm the real
    /// mapping during the Phase 0 spike and adjust.
    /// </summary>
    public int LowBatteryAtOrAbove { get; set; } = 3;
}

public sealed class TileOptions
{
    /// <summary>
    /// When false the map serves only tiles already cached on disk — genuinely offline. Leave it
    /// on until you have seeded your neighbourhood, then turn it off if you like.
    /// </summary>
    public bool AllowNetwork { get; set; } = true;

    public string UpstreamUrl { get; set; } = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";

    /// <summary>OSM's tile policy requires an identifying User-Agent. Do not send a browser's.</summary>
    public string UserAgent { get; set; } = "CatTracker/1.0 (self-hosted personal pet tracker)";

    /// <summary>Hard cap on a single seeding run, so a stray bounding box cannot hammer OSM.</summary>
    public int MaxSeedTiles { get; set; } = 20_000;

    /// <summary>Upstream requests per second while seeding.</summary>
    public double SeedRatePerSecond { get; set; } = 4;
}

public sealed class DiagnosticsOptions
{
    /// <summary>Days of rolling log files to keep. Roughly 1–5 MB a day at Information.</summary>
    public int RetainedDays { get; set; } = 30;

    /// <summary>A single file rolls once it reaches this size, so one bad day cannot fill the disk.</summary>
    public int FileSizeLimitMb { get; set; } = 32;

    /// <summary>Also write logs to the console. launchd captures these into its own file.</summary>
    public bool Console { get; set; } = true;
}

public sealed class ReplayOptions
{
    /// <summary>Days of synthetic history to generate on first run. 0 disables.</summary>
    public int SeedDays { get; set; } = 14;

    public double HomeLat { get; set; } = 52.0907;
    public double HomeLon { get; set; } = 5.1214;
    public int Seed { get; set; } = 1712;
    public string PetName { get; set; } = "Demo Cat";
}
