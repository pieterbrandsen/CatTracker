using CatTracker.App.Alerting;
using CatTracker.App.Readers;
using CatTracker.App.Services;
using CatTracker.Core;
using CatTracker.Data;
using Microsoft.Extensions.Options;

namespace CatTracker.App.Endpoints;

public sealed record ZoneInput(
    string Name,
    string Kind,
    double CenterLat,
    double CenterLon,
    double RadiusM,
    double? ExitBufferM,
    bool? NotifyOnExit,
    bool? NotifyOnEnter);

public sealed record TagInput(string PetName, bool? IsActive);

public sealed record SeedInput(
    double MinLat, double MinLon, double MaxLat, double MaxLon, int MinZoom, int MaxZoom);

public sealed record TagStatus(
    long Id,
    string PetName,
    string FindMyName,
    string SerialNumber,
    bool IsActive,
    Fix? LatestFix,
    long? AgeMs,
    bool? IsHome,
    int? BatteryStatus,
    Excursion? OpenExcursion,
    double? DistanceFromHomeM,
    int FixCount,
    long? FirstFixUtc);

public sealed record StatusResponse(
    string Version,
    string Source,
    long NowUtc,
    bool IsStale,
    long LastPollUtc,
    IReadOnlyList<string> Warnings,
    string? Error,
    ReaderHeartbeat? Heartbeat,
    Zone? Home,
    IReadOnlyList<TagStatus> Tags,
    IReadOnlyList<string> AlertChannels,
    string TimeZone);

public static class ApiEndpoints
{
    public static void MapApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        MapSystem(api);
        MapTags(api);
        MapFixes(api);
        MapZones(api);
        MapHistory(api);
        MapStats(api);
        MapTiles(app, api);
    }

    // ---- system --------------------------------------------------------------------------

    private static void MapSystem(RouteGroupBuilder api)
    {
        api.MapGet("/health", (Repository repository, IOptions<AppOptions> options) =>
        {
            var migrations = repository.AppliedMigrations();
            return Results.Ok(new
            {
                status = "ok",
                version = Version(),
                migrations = migrations.Count,
                schema = migrations.Count > 0 ? migrations[^1] : null,
                dataDirectory = options.Value.ResolveDataDirectory(),
            });
        });

        api.MapGet("/status", async (
            Repository repository,
            CollectorState state,
            IFindMyReader reader,
            AlertDispatcher alerts,
            IOptions<AppOptions> options,
            CancellationToken cancellationToken) =>
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var home = repository.HomeZone();

            var tags = repository.ListTags().Select(tag =>
            {
                var latest = repository.LatestFix(tag.Id);
                bool? isHome = null;
                double? distance = null;

                if (home is not null)
                {
                    var zoneState = repository.GetZoneState(tag.Id, home.Id);
                    isHome = zoneState.State switch
                    {
                        FenceState.Inside => true,
                        FenceState.Outside => false,
                        _ => null,
                    };

                    if (latest is not null)
                    {
                        distance = GeoMath.DistanceM(
                            latest.Latitude, latest.Longitude, home.CenterLat, home.CenterLon);
                    }
                }

                var (fixCount, firstFix, _) = repository.FixSummary(tag.Id);

                return new TagStatus(
                    tag.Id, tag.PetName, tag.FindMyName, tag.SerialNumber, tag.IsActive,
                    latest,
                    latest is null ? null : now - latest.TimestampUtc,
                    isHome,
                    latest?.BatteryStatus,
                    repository.OpenExcursion(tag.Id),
                    distance,
                    fixCount,
                    firstFix);
            }).ToArray();

            return Results.Ok(new StatusResponse(
                Version(),
                state.SourceDescription,
                now,
                state.IsStale,
                state.LastPollUtc,
                state.LastWarnings,
                state.LastError,
                await reader.ReadHeartbeatAsync(cancellationToken),
                home,
                tags,
                alerts.AvailableChannels(),
                options.Value.ResolveTimeZone().Id));
        });

        api.MapGet("/alerts", (Repository repository, int? limit) =>
            Results.Ok(repository.RecentAlerts(Math.Clamp(limit ?? 50, 1, 500))));

        // Read the running logs from wherever you are. The Mac this runs on has no screen you
        // are ever going to look at.
        api.MapGet("/logs", (LogTail logs, int? lines, string? contains, string? file) =>
            Results.Ok(logs.Read(lines ?? 300, contains, file)));

        // Proves the notification path end to end. Worth its weight during setup, when the
        // question is always "is it broken, or has the cat simply not moved?"
        api.MapPost("/alerts/test", async (AlertDispatcher alerts, CancellationToken ct) =>
        {
            var alert = await alerts.RaiseAsync(
                AlertKind.ReaderProblem,
                "Test alert from CatTracker. If you can see this, notifications work.",
                $"test:{Guid.NewGuid()}",
                TimeSpan.Zero,
                cancellationToken: ct);

            return Results.Ok(new { alert.Id, channels = alerts.AvailableChannels() });
        });
    }

    // ---- tags ----------------------------------------------------------------------------

    private static void MapTags(RouteGroupBuilder api)
    {
        api.MapGet("/tags", (Repository repository) => Results.Ok(repository.ListTags()));

        api.MapPatch("/tags/{id:long}", (Repository repository, long id, TagInput input) =>
        {
            var tag = repository.GetTag(id);
            if (tag is null) return Results.NotFound();

            if (string.IsNullOrWhiteSpace(input.PetName))
                return Results.BadRequest(new { error = "petName is required." });

            repository.UpdateTag(id, input.PetName.Trim(), input.IsActive ?? tag.IsActive);
            return Results.Ok(repository.GetTag(id));
        });
    }

    // ---- fixes ---------------------------------------------------------------------------

    private static void MapFixes(RouteGroupBuilder api)
    {
        api.MapGet("/fixes", (
            Repository repository, long tagId, long? from, long? to, int? max) =>
        {
            var (start, end) = Window(from, to, TimeSpan.FromHours(24));
            var fixes = repository.GetFixes(tagId, start, end);
            return Results.Ok(Decimate(fixes, Math.Clamp(max ?? 5000, 100, 100_000)));
        });
    }

    /// <summary>
    /// Uniformly thins a track for display, always keeping the first and last point. A fortnight
    /// of history is tens of thousands of points; the phone does not need all of them to show
    /// where she went.
    /// </summary>
    internal static IReadOnlyList<Fix> Decimate(IReadOnlyList<Fix> fixes, int max)
    {
        if (fixes.Count <= max) return fixes;

        var step = (double)(fixes.Count - 1) / (max - 1);
        var result = new List<Fix>(max);
        for (var i = 0; i < max; i++) result.Add(fixes[(int)Math.Round(i * step)]);
        return result;
    }

    // ---- zones ---------------------------------------------------------------------------

    private static void MapZones(RouteGroupBuilder api)
    {
        api.MapGet("/zones", (Repository repository) => Results.Ok(repository.ListZones()));

        api.MapPost("/zones", (Repository repository, ZoneInput input) =>
        {
            if (!TryBuildZone(input, out var zone, out var error)) return Results.BadRequest(new { error });

            zone.Id = repository.InsertZone(zone);
            return Results.Created($"/api/zones/{zone.Id}", zone);
        });

        api.MapPut("/zones/{id:long}", (Repository repository, long id, ZoneInput input) =>
        {
            if (repository.GetZone(id) is null) return Results.NotFound();
            if (!TryBuildZone(input, out var zone, out var error)) return Results.BadRequest(new { error });

            zone.Id = id;
            repository.UpdateZone(zone);
            return Results.Ok(zone);
        });

        api.MapDelete("/zones/{id:long}", (Repository repository, long id) =>
        {
            if (repository.GetZone(id) is null) return Results.NotFound();
            repository.DeleteZone(id);
            return Results.NoContent();
        });
    }

    internal static bool TryBuildZone(ZoneInput input, out Zone zone, out string error)
    {
        zone = new Zone();
        error = "";

        if (string.IsNullOrWhiteSpace(input.Name)) { error = "name is required."; return false; }

        if (!Enum.TryParse<ZoneKind>(input.Kind, ignoreCase: true, out var kind))
        {
            error = $"kind must be one of: {string.Join(", ", Enum.GetNames<ZoneKind>())}.";
            return false;
        }

        if (input.CenterLat is < -90 or > 90) { error = "centerLat is out of range."; return false; }
        if (input.CenterLon is < -180 or > 180) { error = "centerLon is out of range."; return false; }

        // A zero or negative radius would make the geofence fire on every fix forever.
        if (input.RadiusM is <= 0 or > 100_000)
        {
            error = "radiusM must be between 0 and 100000.";
            return false;
        }

        var buffer = input.ExitBufferM ?? 25;
        if (buffer is < 0 or > 100_000) { error = "exitBufferM is out of range."; return false; }

        zone = new Zone
        {
            Name = input.Name.Trim(),
            Kind = kind,
            CenterLat = input.CenterLat,
            CenterLon = input.CenterLon,
            RadiusM = input.RadiusM,
            ExitBufferM = buffer,
            NotifyOnExit = input.NotifyOnExit ?? true,
            NotifyOnEnter = input.NotifyOnEnter ?? true,
        };

        return true;
    }

    // ---- history -------------------------------------------------------------------------

    private static void MapHistory(RouteGroupBuilder api)
    {
        api.MapGet("/events", (Repository repository, long tagId, int? limit) =>
            Results.Ok(repository.RecentZoneEvents(tagId, Math.Clamp(limit ?? 100, 1, 1000))));

        api.MapGet("/excursions", (Repository repository, long tagId, long? from, long? to) =>
        {
            var (start, end) = Window(from, to, TimeSpan.FromDays(14));
            return Results.Ok(repository.GetExcursions(tagId, start, end));
        });
    }

    // ---- stats ---------------------------------------------------------------------------

    private static void MapStats(RouteGroupBuilder api)
    {
        var stats = api.MapGroup("/stats");

        stats.MapGet("/daily", (
            Repository repository, IOptions<AppOptions> options, long tagId, int? days) =>
        {
            var window = TimeSpan.FromDays(Math.Clamp(days ?? 14, 1, 3650));
            var (start, end) = Window(null, null, window);

            return Results.Ok(Stats.Daily(
                repository.GetExcursions(tagId, start, end),
                repository.GetFixes(tagId, start, end),
                options.Value.ResolveTimeZone(),
                end));
        });

        stats.MapGet("/heatmap", (
            Repository repository, long tagId, long? from, long? to, double? cell) =>
        {
            var (start, end) = Window(from, to, TimeSpan.FromDays(14));
            var cells = Stats.Heatmap(
                repository.GetFixes(tagId, start, end), Math.Clamp(cell ?? 25, 5, 500));

            return Results.Ok(cells.Take(4000));
        });

        stats.MapGet("/clusters", (
            Repository repository, long tagId, long? from, long? to, double? eps, int? minPoints) =>
        {
            var (start, end) = Window(from, to, TimeSpan.FromDays(14));

            // Stats.Clusters collapses co-located fixes into weighted representatives, so a dense
            // fortnight costs little. The cap is only a backstop against an absurd date range.
            var fixes = Decimate(repository.GetFixes(tagId, start, end), 20_000);

            return Results.Ok(Stats.Clusters(
                fixes, Math.Clamp(eps ?? 20, 2, 500), Math.Clamp(minPoints ?? 5, 2, 100)).Take(25));
        });

        stats.MapGet("/rhythm", (
            Repository repository, IOptions<AppOptions> options, long tagId, int? days) =>
        {
            var (start, end) = Window(null, null, TimeSpan.FromDays(Math.Clamp(days ?? 30, 1, 3650)));
            return Results.Ok(Stats.Rhythm(
                repository.GetExcursions(tagId, start, end), options.Value.ResolveTimeZone()));
        });

        stats.MapGet("/roaming", (Repository repository, long tagId, long? from, long? to) =>
        {
            var home = repository.HomeZone();
            if (home is null) return Results.BadRequest(new { error = "No Home zone is defined yet." });

            var (start, end) = Window(from, to, TimeSpan.FromDays(14));
            var fixes = repository.GetFixes(tagId, start, end);

            return Results.Ok(new
            {
                roaming = Stats.Roaming(fixes, home.CenterLat, home.CenterLon),
                coverage = Stats.CoverageRatio(fixes, start, end),
                from = start,
                to = end,
            });
        });
    }

    // ---- tiles ---------------------------------------------------------------------------

    private static void MapTiles(WebApplication app, RouteGroupBuilder api)
    {
        app.MapGet("/tiles/{z:int}/{x:int}/{y:int}.png", async (
            TileCache tiles, int z, int x, int y, CancellationToken cancellationToken) =>
        {
            if (z is < 0 or > TileMath.MaxZoom) return Results.NotFound();

            var bytes = await tiles.GetAsync(new TileRef(z, x, y), cancellationToken);
            if (bytes is null) return Results.NotFound();

            return Results.File(bytes, "image/png");
        });

        api.MapGet("/tiles/status", (TileCache tiles, TileSeedState state) =>
        {
            var (count, bytes) = tiles.Statistics();
            return Results.Ok(new
            {
                cachedTiles = count,
                cachedBytes = bytes,
                seeding = new { state.Running, state.Total, state.Done, state.Failed, state.Cached, state.Message },
            });
        });

        api.MapPost("/tiles/seed", (
            TileCache tiles,
            TileSeedState state,
            IOptions<AppOptions> options,
            SeedInput input,
            IHostApplicationLifetime lifetime) =>
        {
            if (state.Running) return Results.Conflict(new { error = "A seeding run is already in progress." });
            if (!options.Value.Tiles.AllowNetwork)
                return Results.BadRequest(new { error = "Tiles:AllowNetwork is false." });

            if (input.MinLat >= input.MaxLat || input.MinLon >= input.MaxLon)
                return Results.BadRequest(new { error = "Bounds are empty or inverted." });

            var bounds = new Bounds(input.MinLat, input.MinLon, input.MaxLat, input.MaxLon);
            var planned = TileMath.Count(bounds, input.MinZoom, input.MaxZoom);

            _ = Task.Run(
                () => tiles.SeedAsync(bounds, input.MinZoom, input.MaxZoom, state, lifetime.ApplicationStopping),
                CancellationToken.None);

            return Results.Accepted("/api/tiles/status", new { planned, cap = options.Value.Tiles.MaxSeedTiles });
        });
    }

    // ---- helpers -------------------------------------------------------------------------

    internal static (long From, long To) Window(long? from, long? to, TimeSpan fallback)
    {
        var end = to ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var start = from ?? end - (long)fallback.TotalMilliseconds;
        return start <= end ? (start, end) : (end, start);
    }

    internal static string Version() =>
        typeof(ApiEndpoints).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
