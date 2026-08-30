namespace CatTracker.Core;

/// <summary>
/// Spherical earth maths. At neighbourhood scale the error versus a proper ellipsoid model is
/// centimetres, which is four orders of magnitude below AirTag accuracy.
/// </summary>
public static class GeoMath
{
    public const double EarthRadiusM = 6_371_008.8;

    public static double ToRadians(double deg) => deg * Math.PI / 180.0;
    public static double ToDegrees(double rad) => rad * 180.0 / Math.PI;

    /// <summary>Great-circle distance in metres.</summary>
    public static double DistanceM(double lat1, double lon1, double lat2, double lon2)
    {
        var p1 = ToRadians(lat1);
        var p2 = ToRadians(lat2);
        var dp = ToRadians(lat2 - lat1);
        var dl = ToRadians(lon2 - lon1);

        var a = Math.Sin(dp / 2) * Math.Sin(dp / 2)
              + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);

        return 2 * EarthRadiusM * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
    }

    public static double DistanceM(Fix a, Fix b) =>
        DistanceM(a.Latitude, a.Longitude, b.Latitude, b.Longitude);

    /// <summary>Point at <paramref name="distanceM"/> along <paramref name="bearingDeg"/>.</summary>
    public static (double Lat, double Lon) Destination(
        double lat, double lon, double bearingDeg, double distanceM)
    {
        var d = distanceM / EarthRadiusM;
        var b = ToRadians(bearingDeg);
        var p1 = ToRadians(lat);
        var l1 = ToRadians(lon);

        var p2 = Math.Asin(Math.Sin(p1) * Math.Cos(d) + Math.Cos(p1) * Math.Sin(d) * Math.Cos(b));
        var l2 = l1 + Math.Atan2(
            Math.Sin(b) * Math.Sin(d) * Math.Cos(p1),
            Math.Cos(d) - Math.Sin(p1) * Math.Sin(p2));

        return (ToDegrees(p2), NormalizeLon(ToDegrees(l2)));
    }

    public static double NormalizeLon(double lon) => ((lon + 540) % 360) - 180;

    /// <summary>Metres per degree of longitude at a given latitude; used for square-ish grids.</summary>
    public static double MetresPerDegreeLon(double lat) =>
        111_320.0 * Math.Cos(ToRadians(lat));

    public const double MetresPerDegreeLat = 110_574.0;
}
