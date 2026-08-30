namespace CatTracker.Core;

public sealed record HeatCell(double Lat, double Lon, double CellMeters, long DwellMs);

public sealed record RoamingStats(
    double MaxDistanceM, double P95DistanceM, double MeanDistanceM, int FixCount);

public sealed record Cluster(
    double Lat, double Lon, long DwellMs, int FixCount, double RadiusM);

public sealed record DailySummary(
    DateOnly Date,
    long ObservedOutdoorMs,
    long UpperBoundOutdoorMs,
    int ExcursionCount,
    double MaxDistanceM,
    int FixCount,
    double Coverage);

public sealed record HourHistogram(int[] Departures, int[] Returns);

/// <summary>
/// Derived statistics over a signal that is sparse, irregularly sampled and occasionally wrong.
///
/// Every figure here is an estimate, and the API is built so callers cannot forget that: durations
/// come in observed/upper-bound pairs and everything carries a coverage ratio. A chart that draws
/// a confident line through a four-hour hole in the data is worse than no chart.
/// </summary>
public static class Stats
{
    /// <summary>Default gap beyond which we admit we simply do not know where the cat was.</summary>
    public const long DefaultMaxGapMs = 30 * 60 * 1000;

    /// <summary>
    /// Fraction of [from, to] we actually observed. Any interval longer than
    /// <paramref name="maxGapMs"/> without a fix — including before the first and after the last —
    /// counts as unobserved.
    /// </summary>
    public static double CoverageRatio(
        IReadOnlyList<Fix> fixes, long fromUtc, long toUtc, long maxGapMs = DefaultMaxGapMs)
    {
        var total = toUtc - fromUtc;
        if (total <= 0) return 0;
        if (fixes.Count == 0) return 0;

        var ordered = fixes.Select(f => f.TimestampUtc)
                           .Where(t => t >= fromUtc && t <= toUtc)
                           .OrderBy(t => t)
                           .ToArray();
        if (ordered.Length == 0) return 0;

        long unobserved = 0;
        var previous = fromUtc;
        foreach (var t in ordered)
        {
            var gap = t - previous;
            if (gap > maxGapMs) unobserved += gap;
            previous = t;
        }

        var tail = toUtc - previous;
        if (tail > maxGapMs) unobserved += tail;

        return Math.Clamp((total - unobserved) / (double)total, 0, 1);
    }

    /// <summary>
    /// Dwell-weighted occupancy grid. Each fix is credited with the time until the next one,
    /// capped at <paramref name="maxDwellMs"/> so a single overnight gap cannot paint a fake
    /// hotspot on whichever bush she happened to be near at midnight.
    /// </summary>
    public static IReadOnlyList<HeatCell> Heatmap(
        IReadOnlyList<Fix> fixes, double cellMeters = 25, long maxDwellMs = DefaultMaxGapMs)
    {
        if (fixes.Count == 0) return [];

        var ordered = fixes.OrderBy(f => f.TimestampUtc).ToArray();
        var refLat = ordered.Average(f => f.Latitude);

        var dLat = cellMeters / GeoMath.MetresPerDegreeLat;
        var dLon = cellMeters / Math.Max(1e-6, GeoMath.MetresPerDegreeLon(refLat));

        var cells = new Dictionary<(long, long), long>();
        for (var i = 0; i < ordered.Length; i++)
        {
            var dwell = i < ordered.Length - 1
                ? Math.Min(ordered[i + 1].TimestampUtc - ordered[i].TimestampUtc, maxDwellMs)
                : 0;
            if (dwell <= 0) continue;

            var key = ((long)Math.Floor(ordered[i].Latitude / dLat),
                       (long)Math.Floor(ordered[i].Longitude / dLon));
            cells[key] = cells.TryGetValue(key, out var existing) ? existing + dwell : dwell;
        }

        return cells
            .Select(kv => new HeatCell(
                (kv.Key.Item1 + 0.5) * dLat,
                (kv.Key.Item2 + 0.5) * dLon,
                cellMeters,
                kv.Value))
            .OrderByDescending(c => c.DwellMs)
            .ToArray();
    }

    public static RoamingStats Roaming(IReadOnlyList<Fix> fixes, double homeLat, double homeLon)
    {
        if (fixes.Count == 0) return new RoamingStats(0, 0, 0, 0);

        var distances = fixes
            .Select(f => GeoMath.DistanceM(f.Latitude, f.Longitude, homeLat, homeLon))
            .OrderBy(d => d)
            .ToArray();

        return new RoamingStats(
            distances[^1],
            Percentile(distances, 0.95),
            distances.Average(),
            distances.Length);
    }

    /// <summary>Percentile of a pre-sorted array, linearly interpolated.</summary>
    public static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        if (sorted.Length == 1) return sorted[0];

        var rank = p * (sorted.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper) return sorted[lower];
        return sorted[lower] + (rank - lower) * (sorted[upper] - sorted[lower]);
    }

    /// <summary>
    /// DBSCAN over the fixes, ranked by total dwell — "her favourite spots".
    ///
    /// Run naively this is O(n²) in the size of the *densest* cluster, and a house cat puts most
    /// of a fortnight's fixes inside one 50 m circle: nearly three seconds, long enough for the
    /// card to look broken. A spatial index alone does not help, because those points genuinely
    /// are neighbours — the crowded cell has to be scanned however it is indexed.
    ///
    /// So co-located fixes are first collapsed onto a grid a quarter of the search radius across,
    /// each cell becoming one representative carrying the *weight* and dwell of the fixes it
    /// swallowed. Weight counts towards minPoints, so a spot visited five times still qualifies
    /// even when those five fixes collapse to one representative — which is what separates this
    /// from simply throwing data away.
    /// </summary>
    public static IReadOnlyList<Cluster> Clusters(
        IReadOnlyList<Fix> fixes,
        double epsMeters = 20,
        int minPoints = 5,
        long maxDwellMs = DefaultMaxGapMs)
    {
        var ordered = fixes.OrderBy(f => f.TimestampUtc).ToArray();
        if (ordered.Length == 0) return [];

        var referenceLat = ordered.Average(f => f.Latitude);
        var metresPerLon = Math.Max(1e-9, GeoMath.MetresPerDegreeLon(referenceLat));

        // ---- collapse co-located fixes into weighted representatives ------------------------

        var snapMetres = Math.Max(0.5, epsMeters / 4);
        var snapLat = snapMetres / GeoMath.MetresPerDegreeLat;
        var snapLon = snapMetres / metresPerLon;

        var buckets = new Dictionary<(long, long), (double Lat, double Lon, int Weight, long Dwell)>();

        for (var i = 0; i < ordered.Length; i++)
        {
            var dwell = i < ordered.Length - 1
                ? Math.Min(ordered[i + 1].TimestampUtc - ordered[i].TimestampUtc, maxDwellMs)
                : 0;

            var key = ((long)Math.Floor(ordered[i].Latitude / snapLat),
                       (long)Math.Floor(ordered[i].Longitude / snapLon));

            var slot = buckets.GetValueOrDefault(key);
            buckets[key] = (slot.Lat + ordered[i].Latitude,
                            slot.Lon + ordered[i].Longitude,
                            slot.Weight + 1,
                            slot.Dwell + dwell);
        }

        var reps = buckets.Values
            .Select(b => (Lat: b.Lat / b.Weight, Lon: b.Lon / b.Weight, b.Weight, b.Dwell))
            .ToArray();

        var n = reps.Length;

        // ---- neighbour grid, one search radius across ---------------------------------------

        var cellLat = epsMeters / GeoMath.MetresPerDegreeLat;
        var cellLon = epsMeters / metresPerLon;

        var grid = new Dictionary<(long, long), List<int>>();
        for (var i = 0; i < n; i++)
        {
            var key = ((long)Math.Floor(reps[i].Lat / cellLat), (long)Math.Floor(reps[i].Lon / cellLon));
            if (!grid.TryGetValue(key, out var bucket)) grid[key] = bucket = [];
            bucket.Add(i);
        }

        List<int> RegionQuery(int index)
        {
            var lat = (long)Math.Floor(reps[index].Lat / cellLat);
            var lon = (long)Math.Floor(reps[index].Lon / cellLon);
            var result = new List<int>();

            for (var dLat = -1; dLat <= 1; dLat++)
            for (var dLon = -1; dLon <= 1; dLon++)
            {
                if (!grid.TryGetValue((lat + dLat, lon + dLon), out var bucket)) continue;

                foreach (var candidate in bucket)
                {
                    var distance = GeoMath.DistanceM(
                        reps[index].Lat, reps[index].Lon, reps[candidate].Lat, reps[candidate].Lon);

                    if (distance <= epsMeters) result.Add(candidate);
                }
            }

            return result;
        }

        int WeightOf(List<int> members)
        {
            var total = 0;
            foreach (var i in members) total += reps[i].Weight;
            return total;
        }

        // ---- DBSCAN over the representatives -------------------------------------------------

        const int Unclassified = 0, Noise = -1;
        var labels = new int[n]; // 0 = unvisited, -1 = noise, >0 = cluster id
        var clusterId = 0;

        for (var i = 0; i < n; i++)
        {
            if (labels[i] != Unclassified) continue;

            var neighbours = RegionQuery(i);
            if (WeightOf(neighbours) < minPoints)
            {
                labels[i] = Noise;
                continue;
            }

            clusterId++;
            labels[i] = clusterId;

            // Iterate with an index: the frontier grows while we walk it.
            for (var q = 0; q < neighbours.Count; q++)
            {
                var j = neighbours[q];
                if (labels[j] == Noise) labels[j] = clusterId;
                if (labels[j] != Unclassified) continue;

                labels[j] = clusterId;
                var inner = RegionQuery(j);
                if (WeightOf(inner) >= minPoints)
                {
                    foreach (var k in inner)
                        if (labels[k] is Unclassified or Noise)
                            neighbours.Add(k);
                }
            }
        }

        var result = new List<Cluster>();
        for (var id = 1; id <= clusterId; id++)
        {
            var members = Enumerable.Range(0, n).Where(i => labels[i] == id).ToArray();
            if (members.Length == 0) continue;

            var weight = members.Sum(i => reps[i].Weight);
            var lat = members.Sum(i => reps[i].Lat * reps[i].Weight) / weight;
            var lon = members.Sum(i => reps[i].Lon * reps[i].Weight) / weight;
            var radius = members.Max(i => GeoMath.DistanceM(reps[i].Lat, reps[i].Lon, lat, lon));

            result.Add(new Cluster(lat, lon, members.Sum(i => reps[i].Dwell), weight, radius));
        }

        return result.OrderByDescending(c => c.DwellMs).ToArray();
    }

    /// <summary>
    /// Time outdoors per local day. Excursions spanning midnight are split across days, and each
    /// day reports both what we observed and the upper bound that includes unobserved gaps.
    /// </summary>
    public static IReadOnlyList<DailySummary> Daily(
        IReadOnlyList<Excursion> excursions,
        IReadOnlyList<Fix> fixes,
        TimeZoneInfo tz,
        long nowUtcMs)
    {
        var days = new Dictionary<DateOnly, (long Observed, long Upper, int Count, double MaxD, int Fixes, double CovSum, int CovN)>();

        foreach (var e in excursions)
        {
            var end = e.ReturnedUtc ?? nowUtcMs;
            if (end <= e.DepartedUtc) continue;

            foreach (var (day, sliceMs) in SplitByLocalDay(e.DepartedUtc, end, tz))
            {
                var entry = days.GetValueOrDefault(day);
                entry.Observed += (long)(sliceMs * e.CoverageRatio);
                entry.Upper += sliceMs;
                entry.MaxD = Math.Max(entry.MaxD, e.MaxDistanceM);
                entry.CovSum += e.CoverageRatio;
                entry.CovN++;
                days[day] = entry;
            }

            // Count the excursion on the day it started, so "3 trips out" means what you expect.
            var startDay = LocalDate(e.DepartedUtc, tz);
            var s = days.GetValueOrDefault(startDay);
            s.Count++;
            days[startDay] = s;
        }

        foreach (var f in fixes)
        {
            var day = LocalDate(f.TimestampUtc, tz);
            var entry = days.GetValueOrDefault(day);
            entry.Fixes++;
            days[day] = entry;
        }

        return days
            .OrderBy(kv => kv.Key)
            .Select(kv => new DailySummary(
                kv.Key,
                kv.Value.Observed,
                kv.Value.Upper,
                kv.Value.Count,
                kv.Value.MaxD,
                kv.Value.Fixes,
                kv.Value.CovN > 0 ? kv.Value.CovSum / kv.Value.CovN : 0))
            .ToArray();
    }

    /// <summary>When she typically goes out and comes back, as 24-bin local-hour histograms.</summary>
    public static HourHistogram Rhythm(IReadOnlyList<Excursion> excursions, TimeZoneInfo tz)
    {
        var departures = new int[24];
        var returns = new int[24];

        foreach (var e in excursions)
        {
            departures[LocalTime(e.DepartedUtc, tz).Hour]++;
            if (e.ReturnedUtc is { } r) returns[LocalTime(r, tz).Hour]++;
        }

        return new HourHistogram(departures, returns);
    }

    public static IEnumerable<(DateOnly Day, long Ms)> SplitByLocalDay(
        long fromUtcMs, long toUtcMs, TimeZoneInfo tz)
    {
        var cursor = fromUtcMs;
        var guard = 0;

        while (cursor < toUtcMs && guard++ < 4000)
        {
            var localStart = LocalTime(cursor, tz);
            var nextMidnightLocal = localStart.Date.AddDays(1);
            var nextMidnightUtc = ToUtcMs(nextMidnightLocal, tz);

            // Defensive: a DST transition can make the computed boundary land on or before the
            // cursor. Step a whole day rather than spinning.
            if (nextMidnightUtc <= cursor) nextMidnightUtc = cursor + 86_400_000;

            var sliceEnd = Math.Min(nextMidnightUtc, toUtcMs);
            yield return (DateOnly.FromDateTime(localStart.Date), sliceEnd - cursor);
            cursor = sliceEnd;
        }
    }

    public static DateTime LocalTime(long utcMs, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTimeFromUtc(
            DateTimeOffset.FromUnixTimeMilliseconds(utcMs).UtcDateTime, tz);

    public static DateOnly LocalDate(long utcMs, TimeZoneInfo tz) =>
        DateOnly.FromDateTime(LocalTime(utcMs, tz));

    private static long ToUtcMs(DateTime localUnspecified, TimeZoneInfo tz)
    {
        var local = DateTime.SpecifyKind(localUnspecified, DateTimeKind.Unspecified);

        // Midnight can be skipped entirely by a DST jump; nudge forward until it exists.
        var guard = 0;
        while (tz.IsInvalidTime(local) && guard++ < 24)
            local = local.AddHours(1);

        var offset = tz.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUnixTimeMilliseconds();
    }
}
