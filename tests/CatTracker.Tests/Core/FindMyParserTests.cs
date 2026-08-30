using CatTracker.Core;

namespace CatTracker.Tests.Core;

public class FindMyParserTests
{
    private const string Realistic = """
        [
          {
            "name": "Pluis",
            "serialNumber": "HK1234ABCD",
            "batteryStatus": 1,
            "productType": { "type": "b389" },
            "location": {
              "latitude": 52.0907,
              "longitude": 5.1214,
              "timeStamp": 1756000000000,
              "horizontalAccuracy": 12.5,
              "altitude": 3.0,
              "isOld": false,
              "isInaccurate": false,
              "positionType": "crowdsourced"
            },
            "address": { "label": "Somewhere" }
          }
        ]
        """;

    [Fact]
    public void Parse_ReadsARealisticPayload()
    {
        var result = FindMyParser.Parse(Realistic);

        var item = Assert.Single(result.Items);
        Assert.Empty(result.Warnings);
        Assert.Equal("HK1234ABCD", item.SerialNumber);
        Assert.Equal("Pluis", item.Name);
        Assert.Equal(1, item.BatteryStatus);

        var location = Assert.IsType<FindMyLocation>(item.Location);
        Assert.Equal(52.0907, location.Latitude, 6);
        Assert.Equal(5.1214, location.Longitude, 6);
        Assert.Equal(12.5, location.HorizontalAccuracy);
        Assert.Equal(3.0, location.Altitude);
        Assert.Equal("crowdsourced", location.PositionType);
        Assert.Equal(1756000000000, location.TimestampUtcMs);
    }

    [Fact]
    public void Parse_AcceptsAnObjectRootWrappingTheArray()
    {
        var result = FindMyParser.Parse($$"""{ "items": {{Realistic}} }""");
        Assert.Single(result.Items);
    }

    [Fact]
    public void Parse_ReportsInvalidJsonRatherThanThrowing()
    {
        var result = FindMyParser.Parse("{not json");
        Assert.Empty(result.Items);
        Assert.Contains("not valid JSON", Assert.Single(result.Warnings));
    }

    [Fact]
    public void Parse_RejectsAnUnexpectedRoot()
    {
        var result = FindMyParser.Parse("42");
        Assert.Empty(result.Items);
        Assert.Contains("Unexpected root", Assert.Single(result.Warnings));
    }

    [Fact]
    public void Parse_SkipsItemsWithNoSerialNumber()
    {
        var result = FindMyParser.Parse("""[{"name":"Anonymous"}]""");
        Assert.Empty(result.Items);
        Assert.Contains("no serial number", Assert.Single(result.Warnings));
    }

    [Fact]
    public void Parse_SkipsNonObjectEntries()
    {
        var result = FindMyParser.Parse("""["nonsense"]""");
        Assert.Empty(result.Items);
        Assert.Contains("not an object", Assert.Single(result.Warnings));
    }

    [Fact]
    public void Parse_TreatsAMissingLocationAsNormal()
    {
        // A tag nobody has walked past simply has no position. That is not an error.
        var result = FindMyParser.Parse("""[{"serialNumber":"X1","name":"Pluis"}]""");
        Assert.Null(Assert.Single(result.Items).Location);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Parse_FallsBackToCrowdSourcedLocation()
    {
        var json = """
            [{"serialNumber":"X1","name":"P","crowdSourcedLocation":
              {"latitude":52.1,"longitude":5.1,"timeStamp":1756000000000}}]
            """;

        Assert.NotNull(Assert.Single(FindMyParser.Parse(json).Items).Location);
    }

    [Fact]
    public void Parse_WarnsOnOutOfRangeCoordinates()
    {
        var json = """[{"serialNumber":"X1","location":{"latitude":991,"longitude":5,"timeStamp":1}}]""";
        var result = FindMyParser.Parse(json);

        Assert.Null(Assert.Single(result.Items).Location);
        Assert.Contains("out-of-range", Assert.Single(result.Warnings));
    }

    [Fact]
    public void Parse_WarnsWhenALocationHasNoCoordinates()
    {
        var result = FindMyParser.Parse("""[{"serialNumber":"X1","location":{"timeStamp":1}}]""");
        Assert.Contains("no usable coordinates", Assert.Single(result.Warnings));
    }

    [Fact]
    public void Parse_WarnsWhenALocationHasNoTimestamp()
    {
        var json = """[{"serialNumber":"X1","location":{"latitude":52.1,"longitude":5.1}}]""";
        Assert.Contains("no timestamp", Assert.Single(FindMyParser.Parse(json).Warnings));
    }

    [Fact]
    public void Parse_AcceptsAStringifiedTimestamp()
    {
        var json = """[{"serialNumber":"X1","location":{"latitude":52.1,"longitude":5.1,"timeStamp":"1756000000000"}}]""";
        Assert.Equal(1756000000000, Assert.Single(FindMyParser.Parse(json).Items).Location!.TimestampUtcMs);
    }

    [Fact]
    public void Parse_AcceptsAlternativeCoordinateNames()
    {
        var json = """[{"serialNumber":"X1","location":{"lat":52.1,"lng":5.1,"timestamp":1756000000000}}]""";
        Assert.NotNull(Assert.Single(FindMyParser.Parse(json).Items).Location);
    }

    [Fact]
    public void Parse_ReadsNumericBooleans()
    {
        var json = """
            [{"serialNumber":"X1","location":{"latitude":52.1,"longitude":5.1,
              "timeStamp":1756000000000,"isOld":1,"isInaccurate":0}}]
            """;

        var location = Assert.Single(FindMyParser.Parse(json).Items).Location!;
        Assert.True(location.IsOld);
        Assert.False(location.IsInaccurate);
    }

    [Fact]
    public void Parse_WarnsWhenTheCacheIsEmpty()
    {
        var result = FindMyParser.Parse("[]");
        Assert.Empty(result.Items);
        Assert.Contains("no items", Assert.Single(result.Warnings));
    }

    [Fact]
    public void Parse_FallsBackToTheSerialWhenThereIsNoName() =>
        Assert.Equal("X1", Assert.Single(FindMyParser.Parse("""[{"serialNumber":"X1"}]""").Items).Name);

    [Theory]
    [InlineData(1756000000L, 1756000000000L)]
    [InlineData(1756000000000L, 1756000000000L)]
    public void NormalizeTimestampMs_HandlesBothUnits(long input, long expected) =>
        Assert.Equal(expected, FindMyParser.NormalizeTimestampMs(input));
}
