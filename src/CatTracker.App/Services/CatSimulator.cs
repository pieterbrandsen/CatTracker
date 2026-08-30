using CatTracker.Core;

namespace CatTracker.App.Services;

public sealed record SimFix(
    long TimestampUtcMs,
    double Lat,
    double Lon,
    double Accuracy,
    bool IsOld,
    bool IsInaccurate,
    int BatteryStatus);

/// <summary>
/// A plausible fake cat.
///
/// This is not decoration — it is what makes the system developable on Windows and testable at
/// all. It reproduces the properties that make the real signal awkward: irregular intervals,
/// accuracy that degrades sharply once she leaves the garden, occasional multi-hour blackouts,
/// and the odd fix Find My has already flagged as junk. Code that looks right against a tidy
/// synthetic track and falls over on the real thing has learned nothing.
/// </summary>
public sealed class CatSimulator
{
    private readonly double _homeLat;
    private readonly double _homeLon;
    private readonly Random _random;

    private bool _isHome = true;
    private long _stateUntil;
    private long _nextFixAt;
    private double _lat;
    private double _lon;
    private double _bearing;
    private int _battery = 1;
    private long _batteryDecayAt;

    public CatSimulator(double homeLat, double homeLon, int seed, long startUtcMs)
    {
        _homeLat = homeLat;
        _homeLon = homeLon;
        _random = new Random(seed);
        _lat = homeLat;
        _lon = homeLon;
        _nextFixAt = startUtcMs;
        _stateUntil = startUtcMs + Minutes(60, 300);
        _batteryDecayAt = startUtcMs + Days(60);
    }

    /// <summary>
    /// Emits every fix generated between the last call and <paramref name="nowUtcMs"/>. Live
    /// callers take the last one; the history seeder consumes them all.
    /// </summary>
    public IEnumerable<SimFix> Advance(long nowUtcMs)
    {
        var guard = 0;
        while (_nextFixAt <= nowUtcMs && guard++ < 500_000)
        {
            var at = _nextFixAt;

            if (at >= _stateUntil) SwitchState(at);
            if (at >= _batteryDecayAt)
            {
                _battery = Math.Min(4, _battery + 1);
                _batteryDecayAt = at + Days(60);
            }

            SimFix fix;
            if (_isHome)
            {
                // Indoors and in the garden: located by your own phone, so tight and frequent.
                var (lat, lon) = Jitter(_homeLat, _homeLon, 8);
                _lat = lat;
                _lon = lon;
                fix = new SimFix(at, lat, lon, 5 + _random.NextDouble() * 20, false, false, _battery);
                _nextFixAt = at + Minutes(1, 6);
            }
            else
            {
                Wander();
                var accuracy = 15 + _random.NextDouble() * 185;
                var inaccurate = _random.NextDouble() < 0.04;
                var old = _random.NextDouble() < 0.03;
                fix = new SimFix(at, _lat, _lon, accuracy, old, inaccurate, _battery);

                // Away from home the crowd network is patchy, and every so often it goes quiet
                // for hours. The coverage-ratio machinery exists precisely for these holes.
                _nextFixAt = at + (_random.NextDouble() < 0.10
                    ? Minutes(35, 190)
                    : Minutes(2, 22));
            }

            yield return fix;
        }
    }

    private void SwitchState(long at)
    {
        _isHome = !_isHome;

        if (_isHome)
        {
            _lat = _homeLat;
            _lon = _homeLon;
            _stateUntil = at + Minutes(90, 420);
        }
        else
        {
            _bearing = _random.NextDouble() * 360;
            _stateUntil = at + Minutes(20, 240);
        }
    }

    private void Wander()
    {
        // Bearing persistence with occasional sharp turns: a random walk with no memory produces
        // a cloud around the house, which is not what cats do.
        _bearing += (_random.NextDouble() - 0.5) * 70;
        if (_random.NextDouble() < 0.12) _bearing += (_random.NextDouble() - 0.5) * 220;

        var metres = _random.NextDouble() < 0.25
            ? _random.NextDouble() * 5      // sitting under a car
            : 10 + _random.NextDouble() * 120;

        var (lat, lon) = GeoMath.Destination(_lat, _lon, _bearing, metres);

        // Keep her in the neighbourhood; real cats have a home range of a few hundred metres.
        if (GeoMath.DistanceM(lat, lon, _homeLat, _homeLon) > 450)
        {
            _bearing = (_bearing + 180) % 360;
            (lat, lon) = GeoMath.Destination(_lat, _lon, _bearing, metres);
        }

        _lat = lat;
        _lon = lon;
    }

    private (double Lat, double Lon) Jitter(double lat, double lon, double sigmaM)
    {
        var distance = Math.Abs(Gaussian()) * sigmaM;
        return GeoMath.Destination(lat, lon, _random.NextDouble() * 360, distance);
    }

    private double Gaussian()
    {
        var u1 = 1.0 - _random.NextDouble();
        var u2 = 1.0 - _random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }

    private long Minutes(int min, int max) => _random.Next(min, max) * 60_000L;

    private static long Days(int days) => days * 86_400_000L;
}
