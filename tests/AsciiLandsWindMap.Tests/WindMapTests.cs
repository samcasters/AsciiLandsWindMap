using Microsoft.Data.Sqlite;
using Xunit;

namespace AsciiLandsWindMap.Tests;

public class HexTests
{
    [Fact]
    public void NeighborsAreSixDistinctHexes()
    {
        var neighbors = Axial.Neighbors(3, -2).ToList();
        Assert.Equal(6, neighbors.Distinct().Count());
        Assert.All(neighbors, hex => Assert.NotEqual((3, -2), hex));
    }

    [Fact]
    public void PixelRoundTripKeepsTheHex()
    {
        var layout = new HexLayout(36);
        foreach (var (q, r) in new (int, int)[] { (0, 0), (2, -1), (-4, 3), (21, -10) })
        {
            var (x, y) = layout.Center(q, r);
            Assert.Equal((q, r), layout.AxialAt(x, y));
        }
    }

    [Fact]
    public void EastIsPositiveXAndSouthIsPositiveY()
    {
        var layout = new HexLayout(36);
        var origin = layout.Center(0, 0);
        var east = layout.Center(1, 0);
        var southEast = layout.Center(0, 1);
        Assert.True(east.X > origin.X);
        Assert.Equal(origin.Y, east.Y, 6);
        Assert.True(southEast.Y > origin.Y);
    }
}

public class WindRuleTests
{
    [Theory]
    [InlineData(10 * 60, WindWindow.OntoLand)]
    [InlineData(14 * 60, WindWindow.OntoLand)]
    [InlineData(18 * 60 + 59, WindWindow.OntoLand)]
    [InlineData(19 * 60, WindWindow.Turning)]
    [InlineData(22 * 60 + 59, WindWindow.Turning)]
    [InlineData(23 * 60, WindWindow.OutToSea)]
    [InlineData(2 * 60, WindWindow.OutToSea)]
    [InlineData(8 * 60 + 59, WindWindow.OutToSea)]
    [InlineData(9 * 60, WindWindow.Turning)]
    [InlineData(9 * 60 + 59, WindWindow.Turning)]
    public void WindowBoundaries(int minute, WindWindow expected) =>
        Assert.Equal(expected, WindRules.WindowOf(minute));

    [Fact]
    public void ArrowGrowsWithStrength()
    {
        Assert.Equal(0, WindRules.ArrowLength(0, 36));
        Assert.True(WindRules.ArrowLength(1, 36) < WindRules.ArrowLength(2, 36));
        Assert.True(WindRules.ArrowLength(2, 36) < WindRules.ArrowLength(3, 36));
    }

    [Fact]
    public void AverageKeepsASingleDirection()
    {
        var sample = WindRules.Average([new ReadingLite(0, 270, 2), new ReadingLite(1, 180, 3)]);
        Assert.NotNull(sample);
        var west = WindRules.Average([new ReadingLite(0, 270, 2)])!.Value;
        var south = WindRules.Average([new ReadingLite(0, 180, 3)])!.Value;
        Assert.InRange(west.DirectionDeg, 269, 271);
        Assert.InRange(south.DirectionDeg, 179, 181);
        Assert.False(sample!.Value.Cancelled);
    }

    [Fact]
    public void OppositeReadingsCancel()
    {
        var sample = WindRules.Average([
            new ReadingLite(0, 0, 3),
            new ReadingLite(1, 180, 3),
        ])!.Value;
        Assert.True(sample.Cancelled);
        Assert.Equal(0, sample.Strength);
    }

    [Fact]
    public void CalmAveragesDown()
    {
        var sample = WindRules.Average([
            new ReadingLite(0, 90, 1),
            new ReadingLite(1, 0, 0),
        ])!.Value;
        Assert.Equal(1, sample.Strength);
        Assert.InRange(sample.DirectionDeg, 89, 91);
        Assert.False(sample.Cancelled);
    }
}

public class GameClockTests
{
    [Fact]
    public void HeldClockStaysPutUntilADayLengthIsSet()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Null(GameClock.CurrentMinute(start, null, null, 48));
        Assert.Equal(14 * 60, GameClock.CurrentMinute(start.AddHours(3), start, 14 * 60, null));
    }

    [Fact]
    public void ClockAdvancesAcrossAGameDay()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var half = GameClock.CurrentMinute(start.AddMinutes(24), start, 23 * 60, 48);
        Assert.Equal(11 * 60, half);
        var full = GameClock.CurrentMinute(start.AddMinutes(48), start, 23 * 60, 48);
        Assert.Equal(23 * 60, full);
    }

    [Theory]
    [InlineData("14:05", 14 * 60 + 5)]
    [InlineData("2:00", 2 * 60)]
    [InlineData("1405", 14 * 60 + 5)]
    public void ParsesGameTime(string text, int minute)
    {
        Assert.True(GameClock.TryParseTime(text, out var parsed));
        Assert.Equal(minute, parsed);
    }
}

public class MapDataTests
{
    [Fact]
    public void RenameKeepsWindAndAPlaceholderDoesNotEraseARealName()
    {
        using var db = new TempDb();
        db.Database.ApplyWorld([Node("explore-p1-m1", "Unknown", "forest", 1, -1)], "frontier-v2");
        db.Database.SaveObservation("explore-p1-m1", "eu", 14 * 60, 90, 2);

        var renamed = db.Database.ApplyWorld([Node("explore-p1-m1", "Amber Woods", "forest", 1, -1)], "frontier-v2");
        var map = db.Database.LoadMap("eu");
        Assert.Equal(1, renamed.Renamed);
        Assert.Equal("Amber Woods", map.Regions[0].Name);
        Assert.Equal(2, map.Observations["explore-p1-m1"][0].Strength);

        db.Database.ApplyWorld([Node("explore-p1-m1", "Unknown", "plains", 1, -1)], "frontier-v2");
        map = db.Database.LoadMap("eu");
        Assert.Equal("Amber Woods", map.Regions[0].Name);
        Assert.Equal("plains", map.Regions[0].Biome);
        Assert.Single(map.Observations["explore-p1-m1"]);
    }

    [Fact]
    public void DuplicateNamesGetTheCoordSlug()
    {
        using var db = new TempDb();
        db.Database.ApplyWorld([
            Node("explore-p23-m9", "Redleaf Westwind Crags", "limestone_karst", 23, -9),
            Node("explore-p11-m1", "Redleaf Westwind Crags", "limestone_karst", 11, -1),
        ], "frontier-v2");
        var names = db.Database.LoadMap("eu").Regions.Select(region => region.Name).OrderBy(name => name).ToList();
        Assert.Equal(["Redleaf Westwind Crags (p11-m1)", "Redleaf Westwind Crags (p23-m9)"], names);
    }

    [Fact]
    public void ImportCanKeepMineOrTakeTheFileWithoutTouchingAnotherRealm()
    {
        using var db = new TempDb();
        db.Database.ApplyWorld([
            Node("explore-p1-m1", "Amber Woods", "forest", 1, -1),
            Node("explore-p2-m1", "Briar Coast", "tropical_coast", 2, -1),
        ], "frontier-v2");
        db.Database.SaveObservation("explore-p1-m1", "eu", 14 * 60, 90, 2);
        db.Database.SaveObservation("explore-p1-m1", "na", 2 * 60, 10, 1);
        db.Database.SaveTileMeta("explore-p1-m1", "home", ["stormy"]);

        var file = new MapSnapshot
        {
            Schema = 1,
            Realm = "eu",
            WorldId = "frontier-v2",
            Tiles =
            [
                new TileSnapshot
                {
                    Id = "explore-p1-m1",
                    Note = "theirs",
                    Tags = ["deep_water"],
                    Observations = [new ReadingSnapshot { GameMinute = 14 * 60, DirectionDeg = 180, Strength = 3, RecordedUtc = "2026-01-01T00:00:00Z" }],
                },
                new TileSnapshot
                {
                    Id = "explore-p2-m1",
                    Note = "",
                    Tags = ["shallow"],
                    Observations = [new ReadingSnapshot { GameMinute = 2 * 60, DirectionDeg = 45, Strength = 1, RecordedUtc = "2026-01-01T00:00:00Z" }],
                },
                new TileSnapshot
                {
                    Id = "explore-missing",
                    Note = "nope",
                    Tags = [],
                    Observations = [],
                },
            ],
        };

        var analysis = db.Database.Analyze(file);
        Assert.Single(analysis.Conflicts);
        Assert.Equal("Amber Woods", analysis.Conflicts[0].DisplayName);
        Assert.Single(analysis.Adds);
        Assert.Equal(["explore-missing"], analysis.UnknownIds);

        var kept = db.Database.ApplyImport(file, new HashSet<string>());
        Assert.Equal(0, kept.Replaced);
        Assert.Equal(1, kept.Kept);
        Assert.Equal(1, kept.Added);
        var eu = db.Database.LoadMap("eu");
        var amber = eu.Regions.Single(region => region.Id == "explore-p1-m1");
        Assert.Equal("home", amber.Note);
        Assert.Equal(["stormy"], amber.Tags);
        Assert.Equal(2, eu.Observations["explore-p1-m1"][0].Strength);
        Assert.Equal(["shallow"], eu.Regions.Single(region => region.Id == "explore-p2-m1").Tags);

        var replaced = db.Database.ApplyImport(file, new HashSet<string> { "explore-p1-m1" });
        Assert.Equal(1, replaced.Replaced);
        eu = db.Database.LoadMap("eu");
        amber = eu.Regions.Single(region => region.Id == "explore-p1-m1");
        Assert.Equal("theirs", amber.Note);
        Assert.Equal(["deep_water"], amber.Tags);
        Assert.Equal(3, eu.Observations["explore-p1-m1"][0].Strength);
        Assert.Equal(180, eu.Observations["explore-p1-m1"][0].DirectionDeg, 0);
        var na = db.Database.LoadMap("na");
        Assert.Equal(1, na.Observations["explore-p1-m1"][0].Strength);
    }

    [Fact]
    public void ExportRoundTripMatchesAnUnchangedImport()
    {
        using var db = new TempDb();
        db.Database.ApplyWorld([Node("explore-p1-m1", "Amber Woods", "forest", 1, -1)], "frontier-v2");
        db.Database.SaveObservation("explore-p1-m1", "eu", 14 * 60, 90, 2);
        db.Database.SaveTileMeta("explore-p1-m1", "camp", ["favorite"]);
        var snapshot = db.Database.Export("eu", "frontier-v2");
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n") + ".json");
        try
        {
            MapExchange.Write(path, snapshot);
            var read = MapExchange.Read(path);
            var analysis = db.Database.Analyze(read);
            Assert.Empty(analysis.Conflicts);
            Assert.Empty(analysis.Adds);
            Assert.Equal(1, analysis.Unchanged);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static WorldNode Node(string id, string name, string biome, int q, int r) => new()
    {
        Id = id,
        Name = name,
        Biome = biome,
        Coord = [q, r],
    };

    private sealed class TempDb : IDisposable
    {
        public WindDatabase Database { get; }

        public TempDb()
        {
            var path = Path.Combine(Path.GetTempPath(), "windmap-tests", Guid.NewGuid().ToString("n") + ".sqlite");
            Database = new WindDatabase(path);
        }

        public void Dispose()
        {
            Database.Dispose();
            SqliteConnection.ClearAllPools();
        }
    }
}
