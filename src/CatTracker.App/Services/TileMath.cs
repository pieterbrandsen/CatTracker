namespace CatTracker.App.Services;

public readonly record struct TileRef(int Z, int X, int Y);

public readonly record struct Bounds(double MinLat, double MinLon, double MaxLat, double MaxLon);

/// <summary>Standard slippy-map tile arithmetic (Web Mercator, XYZ scheme).</summary>
public static class TileMath
{
    public const int MaxZoom = 19;

    public static TileRef FromLatLon(double lat, double lon, int zoom)
    {
        zoom = Math.Clamp(zoom, 0, MaxZoom);
        var n = 1 << zoom;

        var x = (int)Math.Floor((lon + 180.0) / 360.0 * n);

        var latRad = Math.Clamp(lat, -85.05112878, 85.05112878) * Math.PI / 180.0;
        var y = (int)Math.Floor(
            (1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * n);

        return new TileRef(zoom, Math.Clamp(x, 0, n - 1), Math.Clamp(y, 0, n - 1));
    }

    public static IEnumerable<TileRef> InBounds(Bounds bounds, int minZoom, int maxZoom)
    {
        minZoom = Math.Clamp(minZoom, 0, MaxZoom);
        maxZoom = Math.Clamp(maxZoom, minZoom, MaxZoom);

        for (var z = minZoom; z <= maxZoom; z++)
        {
            // Tile Y increases southwards, so the northern edge gives the smaller index.
            var topLeft = FromLatLon(bounds.MaxLat, bounds.MinLon, z);
            var bottomRight = FromLatLon(bounds.MinLat, bounds.MaxLon, z);

            for (var x = Math.Min(topLeft.X, bottomRight.X); x <= Math.Max(topLeft.X, bottomRight.X); x++)
            for (var y = Math.Min(topLeft.Y, bottomRight.Y); y <= Math.Max(topLeft.Y, bottomRight.Y); y++)
                yield return new TileRef(z, x, y);
        }
    }

    public static long Count(Bounds bounds, int minZoom, int maxZoom)
    {
        minZoom = Math.Clamp(minZoom, 0, MaxZoom);
        maxZoom = Math.Clamp(maxZoom, minZoom, MaxZoom);

        long total = 0;
        for (var z = minZoom; z <= maxZoom; z++)
        {
            var topLeft = FromLatLon(bounds.MaxLat, bounds.MinLon, z);
            var bottomRight = FromLatLon(bounds.MinLat, bounds.MaxLon, z);

            long width = Math.Abs(bottomRight.X - topLeft.X) + 1;
            long height = Math.Abs(bottomRight.Y - topLeft.Y) + 1;
            total += width * height;
        }

        return total;
    }
}
