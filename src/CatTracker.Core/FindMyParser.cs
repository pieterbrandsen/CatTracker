using System.Text.Json;

namespace CatTracker.Core;

public sealed record ParseResult(
    IReadOnlyList<FindMyItem> Items,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Parses <c>Items.data</c> from the Find My cache.
///
/// This file is undocumented and Apple owes us nothing, so the parser is deliberately paranoid:
/// it accepts several spellings for every field, tolerates a root that is either an array or an
/// object wrapping one, and never throws on a single malformed entry. What it will not do is fail
/// silently — everything it could not understand comes back in <see cref="ParseResult.Warnings"/>
/// so the app can raise a loud alert instead of quietly reporting "no movement".
/// </summary>
public static class FindMyParser
{
    public static ParseResult Parse(string json)
    {
        var items = new List<FindMyItem>();
        var warnings = new List<string>();

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return new ParseResult(items, [$"Cache is not valid JSON: {ex.Message}"]);
        }

        using (doc)
        {
            var root = doc.RootElement;

            JsonElement array;
            if (root.ValueKind == JsonValueKind.Array)
            {
                array = root;
            }
            else if (root.ValueKind == JsonValueKind.Object
                     && (TryGetProperty(root, out var wrapped, "items", "content", "accessories")
                         && wrapped.ValueKind == JsonValueKind.Array))
            {
                array = wrapped;
            }
            else
            {
                return new ParseResult(
                    items,
                    [$"Unexpected root element '{root.ValueKind}'; expected an array of items."]);
            }

            var index = -1;
            foreach (var element in array.EnumerateArray())
            {
                index++;
                if (element.ValueKind != JsonValueKind.Object)
                {
                    warnings.Add($"Item {index} is a {element.ValueKind}, not an object; skipped.");
                    continue;
                }

                var serial = GetString(element, "serialNumber", "serial", "identifier");
                if (string.IsNullOrWhiteSpace(serial))
                {
                    warnings.Add($"Item {index} has no serial number; skipped (cannot identify it).");
                    continue;
                }

                var name = GetString(element, "name") ?? serial;
                var battery = GetInt(element, "batteryStatus", "batteryLevel");

                var location = ParseLocation(element, index, warnings);
                items.Add(new FindMyItem(serial, name, battery, location));
            }
        }

        if (items.Count == 0 && warnings.Count == 0)
            warnings.Add("Cache parsed cleanly but contained no items. Is the AirTag still paired?");

        return new ParseResult(items, warnings);
    }

    private static FindMyLocation? ParseLocation(JsonElement item, int index, List<string> warnings)
    {
        // `location` is the live position; `crowdSourcedLocation` sometimes carries a fix when
        // `location` is null (the item has been seen by the network but not resolved yet).
        if (!TryGetProperty(item, out var loc, "location", "crowdSourcedLocation")
            || loc.ValueKind != JsonValueKind.Object)
        {
            return null; // Not an error: a tag nobody has walked past simply has no location.
        }

        var lat = GetDouble(loc, "latitude", "lat");
        var lon = GetDouble(loc, "longitude", "lon", "lng");
        if (lat is null || lon is null)
        {
            warnings.Add($"Item {index} has a location object with no usable coordinates.");
            return null;
        }

        if (lat is < -90 or > 90 || lon is < -180 or > 180)
        {
            warnings.Add($"Item {index} reported an out-of-range coordinate ({lat}, {lon}); skipped.");
            return null;
        }

        var ts = GetLong(loc, "timeStamp", "timestamp", "locationTimestamp");
        if (ts is null)
        {
            warnings.Add($"Item {index} has a location with no timestamp; skipped.");
            return null;
        }

        return new FindMyLocation(
            lat.Value,
            lon.Value,
            GetDouble(loc, "horizontalAccuracy", "horizontalAccuracyMeters"),
            GetDouble(loc, "altitude"),
            GetString(loc, "positionType"),
            GetBool(loc, "isOld") ?? false,
            GetBool(loc, "isInaccurate") ?? false,
            NormalizeTimestampMs(ts.Value));
    }

    /// <summary>
    /// The cache uses Unix milliseconds, but treat a suspiciously small value as seconds rather
    /// than dating every fix to 1970 and silently corrupting a year of history.
    /// </summary>
    public static long NormalizeTimestampMs(long value) =>
        Math.Abs(value) < 100_000_000_000L ? value * 1000 : value;

    private static bool TryGetProperty(JsonElement obj, out JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null)
                return true;
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement obj, params string[] names) =>
        TryGetProperty(obj, out var v, names) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static double? GetDouble(JsonElement obj, params string[] names) =>
        TryGetProperty(obj, out var v, names) && v.ValueKind == JsonValueKind.Number
                                              && v.TryGetDouble(out var d)
            ? d
            : null;

    private static long? GetLong(JsonElement obj, params string[] names)
    {
        if (!TryGetProperty(obj, out var v, names)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)) return l;
        // Seen in the wild as a stringified number after some macOS updates.
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    private static int? GetInt(JsonElement obj, params string[] names) =>
        TryGetProperty(obj, out var v, names) && v.ValueKind == JsonValueKind.Number
                                              && v.TryGetInt32(out var i)
            ? i
            : null;

    private static bool? GetBool(JsonElement obj, params string[] names)
    {
        if (!TryGetProperty(obj, out var v, names)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when v.TryGetInt32(out var i) => i != 0,
            _ => null,
        };
    }
}
