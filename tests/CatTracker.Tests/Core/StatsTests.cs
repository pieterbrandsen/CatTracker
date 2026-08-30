using CatTracker.Core;

namespace CatTracker.Tests.Core;

public class StatsTests
{
    private const long Minute = 60_000;
    private const long Hour = 3_600_000;

    private static List<Fix> Every(long fromUtc, long toUtc, long stepMs, double metresFromHome = 5)
    {
        var fixes = new List<Fix>();
        for (var t = fromUtc; t <= toUtc; t += stepMs)
            fixes.Add(Build.FixAt(1, t, metresFromHome, bearing: (t / stepMs * 37) % 360));
        return fixes;
    }

    // ---- coverage ---------------------------------------------------------------------------

    [Fact]
    public void CoverageRatio_IsOne_WhenFixesAreDense() =>
        Assert.Equal(1.0, Stats.CoverageRatio(Every(0, Hour, 5 * Minute), 0, Hour), 3);

    [Fact]
    public void CoverageRatio_IsZero_WithNoFixes() =>
        Assert.Equal(0, Stats.CoverageRatio([], 0, Hour));

    [Fact]
    public void CoverageRatio_IsZero_ForAnEmptyWindow() =>
        Assert.Equal(0, Stats.CoverageRatio(Every(0, Hour, Minute), 500, 500));

    [Fact]
    public void CoverageRatio_DiscountsLongGaps()
    {
        // Two fixes four hours apart: we saw almost none of that window, and pretending otherwise
        // would turn a guess into a statistic.
        var fixes = new List<Fix> { Build.FixAt(1, 0, 5), Build.FixAt(1, 4 * Hour, 5) };
        Assert.True(Stats.CoverageRatio(fixes, 0, 4 * Hour) < 0.05);
    }

    [Fact]
    public void CoverageRatio_CountsTheTailAfterTheLastFix()
    {
        var withTail = Stats.CoverageRatio(Every(0, Hour, 5 * Minute), 0, 5 * Hour);
        Assert.InRange(withTail, 0.15, 0.30);
    }

    [Fact]
    public void CoverageRatio_IgnoresFixesOutsideTheWindow() =>
        Assert.Equal(0, Stats.CoverageRatio([Build.FixAt(1, 10 * Hour, 5)], 0, Hour));

    // ---- heatmap ----------------------------------------------------------------------------

    [Fact]
    public void Heatmap_IsEmpty_WithoutFixes() => Assert.Empty(Stats.Heatmap([]));

    [Fact]
    public void Heatmap_AccumulatesDwellPerCell()
    {
        // Seven fixes ten minutes apart: six gaps, each credited to the cell of the earlier fix.
        var fixes = Every(0, Hour, 10 * Minute, metresFromHome: 0);
        var cell = Assert.Single(Stats.Heatmap(fixes, cellMeters: 50));

        Assert.Equal(7, fixes.Count);
        Assert.Equal(60 * Minute, cell.DwellMs);
        Assert.Equal(50, cell.CellMeters);
    }

    [Fact]
    public void Heatmap_CapsTheDwellCreditedToOneFix()
    {
        var fixes = new List<Fix> { Build.FixAt(1, 0, 0), Build.FixAt(1, 12 * Hour, 0) };
        var cell = Assert.Single(Stats.Heatmap(fixes, 50));

        // Twelve hours of silence must not paint a twelve-hour hotspot on whichever bush she was
        // near when the network went quiet.
        Assert.Equal(Stats.DefaultMaxGapMs, cell.DwellMs);
    }

    [Fact]
    public void Heatmap_SeparatesDistantPoints()
    {
        var fixes = new List<Fix>
        {
            Build.FixAt(1, 0, 0),
            Build.FixAt(1, 5 * Minute, 0),
            Build.FixAt(1, 10 * Minute, 500, bearing: 90),
            Build.FixAt(1, 15 * Minute, 500, bearing: 90),
        };

        Assert.True(Stats.Heatmap(fixes, 25).Count >= 2);
    }

    // ---- roaming ----------------------------------------------------------------------------

    [Fact]
    public void Roaming_IsAllZeroes_WithoutFixes()
    {
        var roaming = Stats.Roaming([], Build.HomeLat, Build.HomeLon);
        Assert.Equal(0, roaming.MaxDistanceM);
        Assert.Equal(0, roaming.FixCount);
    }

    [Fact]
    public void Roaming_ReportsTheFurthestPoint()
    {
        var fixes = new List<Fix>
        {
            Build.FixAt(1, 0, 10), Build.FixAt(1, 1, 50), Build.FixAt(1, 2, 400),
        };

        var roaming = Stats.Roaming(fixes, Build.HomeLat, Build.HomeLon);

        Assert.Equal(400, roaming.MaxDistanceM, 0);
        Assert.Equal(3, roaming.FixCount);
        Assert.InRange(roaming.MeanDistanceM, 150, 155);
    }

    [Theory]
    [InlineData(0.0, 10)]
    [InlineData(1.0, 50)]
    [InlineData(0.5, 30)]
    public void Percentile_InterpolatesBetweenPoints(double p, double expected) =>
        Assert.Equal(expected, Stats.Percentile([10, 20, 30, 40, 50], p), 6);

    [Fact]
    public void Percentile_HandlesDegenerateInputs()
    {
        Assert.Equal(0, Stats.Percentile([], 0.5));
        Assert.Equal(7, Stats.Percentile([7], 0.95));
    }

    // ---- clusters ---------------------------------------------------------------------------

    [Fact]
    public void Clusters_AreEmpty_WithoutFixes() => Assert.Empty(Stats.Clusters([]));

    [Fact]
    public void Clusters_FindsTwoFavouriteSpots()
    {
        var fixes = new List<Fix>();
        for (var i = 0; i < 8; i++) fixes.Add(Build.FixAt(1, i * Minute, 2, bearing: i * 40));
        for (var i = 0; i < 8; i++) fixes.Add(Build.FixAt(1, (20 + i) * Minute, 300, bearing: 90 + i));

        var clusters = Stats.Clusters(fixes, epsMeters: 30, minPoints: 4);

        Assert.Equal(2, clusters.Count);
        Assert.All(clusters, c => Assert.True(c.FixCount >= 4));
        Assert.True(clusters[0].DwellMs >= clusters[1].DwellMs);
    }

    [Fact]
    public void Clusters_RespectTheSearchRadiusExactly()
    {
        // The neighbour grid is indexed at eps, so points in adjacent cells must still be found.
        // Get the cell size wrong and pairs just inside the radius are silently missed.
        var fixes = new List<Fix>();
        for (var i = 0; i < 6; i++)
            fixes.Add(Build.FixAt(1, i * Minute, i * 19, bearing: 90)); // 19 m apart, eps = 20

        var chained = Stats.Clusters(fixes, epsMeters: 20, minPoints: 3);

        Assert.Equal(6, Assert.Single(chained).FixCount);

        // The same points spaced just beyond the radius must not chain at all.
        var spread = new List<Fix>();
        for (var i = 0; i < 6; i++)
            spread.Add(Build.FixAt(1, i * Minute, i * 21, bearing: 90));

        Assert.Empty(Stats.Clusters(spread, epsMeters: 20, minPoints: 3));
    }

    [Fact]
    public void Clusters_CopeWithAFortnightOfFixes()
    {
        // 3000 points is what the API caps a 14-day range to. The naive O(n^2) form took nearly
        // three seconds here, which is long enough for the card to look broken.
        var fixes = new List<Fix>();
        for (var i = 0; i < 3000; i++)
            fixes.Add(Build.FixAt(1, i * 60_000L, 5 + i % 40, bearing: i % 360));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var clusters = Stats.Clusters(fixes, epsMeters: 20, minPoints: 5);
        stopwatch.Stop();

        Assert.NotEmpty(clusters);
        Assert.True(stopwatch.ElapsedMilliseconds < 2000,
            $"clustering 3000 fixes took {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Clusters_IgnoreIsolatedNoise()
    {
        var fixes = new List<Fix>();
        for (var i = 0; i < 8; i++) fixes.Add(Build.FixAt(1, i * Minute, 2, bearing: i * 40));
        fixes.Add(Build.FixAt(1, 60 * Minute, 5000, bearing: 12));

        var clusters = Stats.Clusters(fixes, epsMeters: 30, minPoints: 4);

        Assert.Single(clusters);
        Assert.Equal(8, clusters[0].FixCount);
    }

    // ---- day splitting ----------------------------------------------------------------------

    [Fact]
    public void SplitByLocalDay_KeepsASingleDayWhole()
    {
        var start = new DateTimeOffset(2026, 5, 4, 9, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var slice = Assert.Single(Stats.SplitByLocalDay(start, start + 2 * Hour, TimeZoneInfo.Utc));

        Assert.Equal(new DateOnly(2026, 5, 4), slice.Day);
        Assert.Equal(2 * Hour, slice.Ms);
    }

    [Fact]
    public void SplitByLocalDay_SplitsAcrossMidnight()
    {
        var start = new DateTimeOffset(2026, 5, 4, 23, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var slices = Stats.SplitByLocalDay(start, start + 3 * Hour, TimeZoneInfo.Utc).ToList();

        Assert.Equal(2, slices.Count);
        Assert.Equal(Hour, slices[0].Ms);
        Assert.Equal(2 * Hour, slices[1].Ms);
        Assert.Equal(new DateOnly(2026, 5, 5), slices[1].Day);
    }

    [Fact]
    public void SplitByLocalDay_ReturnsNothingForAnEmptyRange() =>
        Assert.Empty(Stats.SplitByLocalDay(1000, 1000, TimeZoneInfo.Utc));

    // ---- daily / rhythm ---------------------------------------------------------------------

    [Fact]
    public void Daily_ReportsObservedAndUpperBoundSeparately()
    {
        var start = new DateTimeOffset(2026, 5, 4, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var excursion = new Excursion
        {
            TagId = 1,
            DepartedUtc = start,
            ReturnedUtc = start + 4 * Hour,
            CoverageRatio = 0.25,
            MaxDistanceM = 300,
        };

        var summary = Assert.Single(Stats.Daily([excursion], [], TimeZoneInfo.Utc, start + 5 * Hour));

        Assert.Equal(4 * Hour, summary.UpperBoundOutdoorMs);
        Assert.Equal(Hour, summary.ObservedOutdoorMs);
        Assert.Equal(1, summary.ExcursionCount);
        Assert.Equal(300, summary.MaxDistanceM);
    }

    [Fact]
    public void Daily_TreatsAnOpenExcursionAsRunningUntilNow()
    {
        var start = new DateTimeOffset(2026, 5, 4, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var excursion = new Excursion { TagId = 1, DepartedUtc = start, CoverageRatio = 1 };

        var summary = Assert.Single(Stats.Daily([excursion], [], TimeZoneInfo.Utc, start + 2 * Hour));
        Assert.Equal(2 * Hour, summary.UpperBoundOutdoorMs);
    }

    [Fact]
    public void Daily_CountsFixesPerDay()
    {
        var start = new DateTimeOffset(2026, 5, 4, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var fixes = Every(start, start + 2 * Hour, 30 * Minute);

        var summary = Assert.Single(Stats.Daily([], fixes, TimeZoneInfo.Utc, start + 3 * Hour));
        Assert.Equal(fixes.Count, summary.FixCount);
    }

    [Fact]
    public void Daily_SkipsExcursionsThatEndBeforeTheyStart()
    {
        var excursion = new Excursion { TagId = 1, DepartedUtc = 5000, ReturnedUtc = 1000 };

        Assert.DoesNotContain(
            Stats.Daily([excursion], [], TimeZoneInfo.Utc, 9000),
            d => d.UpperBoundOutdoorMs > 0);
    }

    [Fact]
    public void Rhythm_BucketsDeparturesAndReturnsByLocalHour()
    {
        var depart = new DateTimeOffset(2026, 5, 4, 22, 15, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var back = new DateTimeOffset(2026, 5, 5, 6, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var rhythm = Stats.Rhythm(
            [new Excursion { DepartedUtc = depart, ReturnedUtc = back }], TimeZoneInfo.Utc);

        Assert.Equal(1, rhythm.Departures[22]);
        Assert.Equal(1, rhythm.Returns[6]);
        Assert.Equal(24, rhythm.Departures.Length);
    }

    [Fact]
    public void Rhythm_LeavesOpenExcursionsOutOfTheReturnsHistogram()
    {
        var depart = new DateTimeOffset(2026, 5, 4, 22, 15, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var rhythm = Stats.Rhythm([new Excursion { DepartedUtc = depart }], TimeZoneInfo.Utc);

        Assert.Equal(1, rhythm.Departures[22]);
        Assert.Equal(0, rhythm.Returns.Sum());
    }

    [Fact]
    public void LocalDate_RespectsTheZone()
    {
        var utc = new DateTimeOffset(2026, 5, 4, 22, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var amsterdam = TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "W. Europe Standard Time" : "Europe/Amsterdam");

        Assert.Equal(new DateOnly(2026, 5, 5), Stats.LocalDate(utc + 3 * Hour, amsterdam));
    }
}
