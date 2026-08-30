using CatTracker.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CatTracker.App.Services;

public sealed class TileSeedState
{
    public bool Running { get; set; }
    public long Total { get; set; }
    public long Done { get; set; }
    public long Failed { get; set; }
    public long Cached { get; set; }
    public string Message { get; set; } = "idle";
}

/// <summary>
/// A caching tile proxy, which is what makes "fully local" true of the map as well as the data.
///
/// Tiles are fetched from OpenStreetMap once and kept forever. Pan around your neighbourhood
/// once — or seed it deliberately — and the map keeps working with the network unplugged. Set
/// Tiles:AllowNetwork to false afterwards to guarantee nothing leaves the machine at all.
/// </summary>
public sealed class TileCache(
    IDbContextFactory<TileContext> factory,
    HttpClient http,
    IOptions<AppOptions> options,
    ILogger<TileCache> logger)
{
    private readonly SemaphoreSlim _upstream = new(2, 2);

    public byte[]? TryGetCached(TileRef tile)
    {
        using var context = factory.CreateDbContext();
        return context.Tiles.AsNoTracking()
            .Where(t => t.Z == tile.Z && t.X == tile.X && t.Y == tile.Y)
            .Select(t => t.Data)
            .FirstOrDefault();
    }

    /// <summary>Cached tile, or a freshly fetched one when the network is permitted.</summary>
    public async Task<byte[]?> GetAsync(TileRef tile, CancellationToken cancellationToken)
    {
        var cached = TryGetCached(tile);
        if (cached is not null) return cached;

        if (!options.Value.Tiles.AllowNetwork) return null;

        var bytes = await FetchAsync(tile, cancellationToken);
        if (bytes is not null) Store(tile, bytes);
        return bytes;
    }

    private async Task<byte[]?> FetchAsync(TileRef tile, CancellationToken cancellationToken)
    {
        var url = options.Value.Tiles.UpstreamUrl
            .Replace("{z}", tile.Z.ToString())
            .Replace("{x}", tile.X.ToString())
            .Replace("{y}", tile.Y.ToString());

        // Queueing for an upstream slot throws on cancellation like anything else. This sat
        // outside the try below, so a browser abandoning a tile — which happens on every pan and
        // zoom — escaped as an unhandled exception and a 500.
        try
        {
            await _upstream.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        try
        {
            using var response = await http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("Tile {Z}/{X}/{Y} upstream returned {Status}",
                    tile.Z, tile.X, tile.Y, response.StatusCode);
                return null;
            }

            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The viewer panned away before this tile arrived. Completely routine — logging it
            // would bury the real entries under a wall of noise every time the map moves.
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // A cancellation with our own token still healthy is the HttpClient timeout instead,
            // which is worth a quiet word.
            logger.LogDebug(ex, "Tile {Z}/{X}/{Y} fetch failed", tile.Z, tile.X, tile.Y);
            return null;
        }
        finally
        {
            _upstream.Release();
        }
    }

    public void Store(TileRef tile, byte[] data)
    {
        using var context = factory.CreateDbContext();

        var existing = context.Tiles
            .FirstOrDefault(t => t.Z == tile.Z && t.X == tile.X && t.Y == tile.Y);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (existing is null)
        {
            context.Tiles.Add(new MapTile
            {
                Z = tile.Z, X = tile.X, Y = tile.Y, Data = data, FetchedUtc = now,
            });
        }
        else
        {
            existing.Data = data;
            existing.FetchedUtc = now;
        }

        context.SaveChanges();
    }

    public (long Count, long Bytes) Statistics()
    {
        using var context = factory.CreateDbContext();
        var count = context.Tiles.LongCount();
        var bytes = count == 0 ? 0 : context.Tiles.Sum(t => (long)t.Data.Length);
        return (count, bytes);
    }

    /// <summary>
    /// Pre-fetches an area so the map works offline. Deliberately capped and rate limited: OSM
    /// runs on donations and their tile policy forbids bulk downloading. A neighbourhood is a
    /// reasonable ask; a province is not.
    /// </summary>
    public async Task SeedAsync(
        Bounds bounds,
        int minZoom,
        int maxZoom,
        TileSeedState state,
        CancellationToken cancellationToken)
    {
        var settings = options.Value.Tiles;
        var planned = TileMath.Count(bounds, minZoom, maxZoom);
        var truncated = planned > settings.MaxSeedTiles;

        state.Running = true;
        state.Total = Math.Min(planned, settings.MaxSeedTiles);
        state.Done = 0;
        state.Failed = 0;
        state.Cached = 0;
        state.Message = truncated
            ? $"Area needs {planned:N0} tiles; capped at {settings.MaxSeedTiles:N0}. Zoom out less, or narrow the box."
            : $"Seeding {planned:N0} tiles.";

        var delay = settings.SeedRatePerSecond > 0
            ? TimeSpan.FromSeconds(1.0 / settings.SeedRatePerSecond)
            : TimeSpan.Zero;

        try
        {
            foreach (var tile in TileMath.InBounds(bounds, minZoom, maxZoom))
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (state.Done + state.Failed >= state.Total) break;

                if (TryGetCached(tile) is not null)
                {
                    state.Cached++;
                    state.Done++;
                    continue;
                }

                var bytes = await FetchAsync(tile, cancellationToken);
                if (bytes is null)
                {
                    state.Failed++;
                }
                else
                {
                    Store(tile, bytes);
                    state.Done++;
                }

                if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            }

            // Say so when the area was truncated. A bare "Done" over a capped run reads as full
            // coverage, and you would only discover otherwise when the map went blank offline.
            state.Message =
                $"Done. {state.Done:N0} tiles available ({state.Cached:N0} already cached), {state.Failed:N0} failed."
                + (truncated
                    ? $" Area was capped at {settings.MaxSeedTiles:N0} of {planned:N0} tiles — " +
                      "narrow the box or lower the max zoom to cover the rest."
                    : "");
        }
        catch (OperationCanceledException)
        {
            state.Message = "Cancelled.";
        }
        catch (Exception ex)
        {
            state.Message = $"Failed: {ex.Message}";
            logger.LogError(ex, "Tile seeding failed");
        }
        finally
        {
            state.Running = false;
        }
    }
}
