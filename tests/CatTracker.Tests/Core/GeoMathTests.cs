using CatTracker.Core;

namespace CatTracker.Tests.Core;

public class GeoMathTests
{
    [Fact]
    public void DistanceM_IsZero_ForTheSamePoint() =>
        Assert.Equal(0, GeoMath.DistanceM(52.0907, 5.1214, 52.0907, 5.1214), 6);

    [Fact]
    public void DistanceM_MatchesAKnownSeparation()
    {
        // Utrecht to Amsterdam Centraal, ~35 km. Within a percent is plenty at this scale.
        var distance = GeoMath.DistanceM(52.0907, 5.1214, 52.3791, 4.9003);
        Assert.InRange(distance, 34_000, 36_000);
    }

    [Fact]
    public void DistanceM_IsSymmetric()
    {
        var forward = GeoMath.DistanceM(52.0907, 5.1214, 52.1, 5.2);
        var backward = GeoMath.DistanceM(52.1, 5.2, 52.0907, 5.1214);
        Assert.Equal(forward, backward, 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(275)]
    public void Destination_ThenDistance_RoundTrips(double bearing)
    {
        var (lat, lon) = GeoMath.Destination(52.0907, 5.1214, bearing, 250);
        Assert.Equal(250, GeoMath.DistanceM(52.0907, 5.1214, lat, lon), 1);
    }

    [Fact]
    public void Destination_NorthIncreasesLatitude()
    {
        var (lat, lon) = GeoMath.Destination(52.0907, 5.1214, 0, 1000);
        Assert.True(lat > 52.0907);
        Assert.Equal(5.1214, lon, 4);
    }

    [Fact]
    public void DistanceM_AcceptsFixes()
    {
        var a = Build.FixAt(1, 0, 0);
        var b = Build.FixAt(1, 0, 100, bearing: 90);
        Assert.Equal(100, GeoMath.DistanceM(a, b), 1);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(190, -170)]
    [InlineData(-190, 170)]
    // The antimeridian normalises to -180; ±180 are the same line of longitude.
    [InlineData(180, -180)]
    public void NormalizeLon_WrapsIntoRange(double input, double expected) =>
        Assert.Equal(expected, GeoMath.NormalizeLon(input), 6);

    [Fact]
    public void MetresPerDegreeLon_ShrinksTowardsThePoles()
    {
        Assert.True(GeoMath.MetresPerDegreeLon(0) > GeoMath.MetresPerDegreeLon(52));
        Assert.True(GeoMath.MetresPerDegreeLon(52) > GeoMath.MetresPerDegreeLon(80));
    }

    [Fact]
    public void ToRadians_AndBack_RoundTrips() =>
        Assert.Equal(137.5, GeoMath.ToDegrees(GeoMath.ToRadians(137.5)), 9);
}
