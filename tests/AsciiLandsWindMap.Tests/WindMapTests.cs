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

    [Fact]
    public void MarksUseTheKnownIconsAndCaptionsStayShort()
    {
        Assert.Equal("home", TileIcons.Canonical("HOME"));
        Assert.Null(TileIcons.Canonical("nope"));
        Assert.Equal("Home", TileIcons.LabelOf("home"));
        Assert.Equal("", TileIcons.CleanCaption("  "));
        Assert.Equal(80, TileIcons.CleanCaption(new string('a', 90)).Length);
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
    public void MissingDatabaseFileIsCreatedWithEmptyTables()
    {
        var folder = Path.Combine(Path.GetTempPath(), "windmap-tests", Guid.NewGuid().ToString("n"));
        var path = Path.Combine(folder, "wind.sqlite");
        Assert.False(File.Exists(path));
        using (var database = new WindDatabase(path))
        {
            Assert.True(database.CreatedNew);
            Assert.True(File.Exists(path));
            var map = database.LoadMap("eu");
            Assert.Empty(map.Regions);
            Assert.Empty(map.Observations);
        }
        SqliteConnection.ClearAllPools();
        Directory.Delete(folder, true);
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

    [Fact]
    public void SyncWindStaysApartFromHandReadings()
    {
        using var db = new TempDb();
        db.Database.ApplyWorld([
            Node("explore-p1-m1", "Amber Woods", "forest", 1, -1),
            Node("explore-p2-p0", "Open Sea", "ocean", 2, 0),
        ], "frontier-v2");
        db.Database.SaveObservation("explore-p2-p0", "eu", 14 * 60, 90, 2);
        db.Database.ReplaceSyncWind("eu", 14 * 60, new Dictionary<string, SeaWindArrow>
        {
            ["explore-p2-p0"] = new(270, 3),
        });

        var map = db.Database.LoadMap("eu");
        Assert.Equal(2, map.Observations["explore-p2-p0"][0].Strength);
        Assert.Equal(90, map.Observations["explore-p2-p0"][0].DirectionDeg, 0);
        Assert.Equal(3, map.SyncWind["explore-p2-p0"].Strength);
        Assert.Equal(270, map.SyncWind["explore-p2-p0"].DirectionDeg, 0);

        var exported = db.Database.Export("eu", "frontier-v2");
        var sea = exported.Tiles.Single(tile => tile.Id == "explore-p2-p0");
        Assert.Contains(sea.Observations, reading => reading.Source == "sync" && reading.Strength == 3);
        Assert.Contains(sea.Observations, reading => reading.Source == "" && reading.Strength == 2);

        using var other = new TempDb();
        other.Database.ApplyWorld([Node("explore-p2-p0", "Open Sea", "ocean", 2, 0)], "frontier-v2");
        other.Database.SaveObservation("explore-p2-p0", "eu", 14 * 60, 45, 1);
        var syncOnly = new MapSnapshot
        {
            Schema = 1,
            Realm = "eu",
            WorldId = "frontier-v2",
            Tiles =
            [
                new TileSnapshot
                {
                    Id = "explore-p2-p0",
                    Observations =
                    [
                        new ReadingSnapshot
                        {
                            GameMinute = 14 * 60,
                            DirectionDeg = 180,
                            Strength = 3,
                            RecordedUtc = "2026-01-01T00:00:00Z",
                            Source = "sync",
                        },
                    ],
                },
            ],
        };
        var analysis = other.Database.Analyze(syncOnly);
        Assert.Empty(analysis.Adds);
        Assert.Empty(analysis.Conflicts);
        other.Database.ApplyImport(syncOnly, new HashSet<string>());
        var loaded = other.Database.LoadMap("eu");
        Assert.Equal(1, loaded.Observations["explore-p2-p0"][0].Strength);
        Assert.Equal(45, loaded.Observations["explore-p2-p0"][0].DirectionDeg, 0);
        Assert.Equal(3, loaded.SyncWind["explore-p2-p0"].Strength);
        Assert.Equal(180, loaded.SyncWind["explore-p2-p0"].DirectionDeg, 0);
    }

    [Fact]
    public void DescriptionAndMarkRoundTripAndStayThroughAMapSync()
    {
        using var db = new TempDb();
        db.Database.ApplyWorld([Node("explore-p1-m1", "Amber Woods", "forest", 1, -1)], "frontier-v2");
        db.Database.SaveTileMeta("explore-p1-m1", "home", ["favorite"], "north camp", "CAMP");
        db.Database.ApplyWorld([Node("explore-p1-m1", "Amber Woods", "plains", 1, -1)], "frontier-v2");
        var region = db.Database.LoadMap("eu").Regions.Single();
        Assert.Equal("north camp", region.Caption);
        Assert.Equal("camp", region.Icon);
        Assert.Equal("home", region.Note);
        Assert.Equal(["favorite"], region.Tags);
        Assert.Equal("plains", region.Biome);

        db.Database.SaveTileMeta("explore-p1-m1", "home", ["favorite"], new string('a', 90), "nope");
        region = db.Database.LoadMap("eu").Regions.Single();
        Assert.Equal(80, region.Caption.Length);
        Assert.Equal("", region.Icon);

        db.Database.SaveTileMeta("explore-p1-m1", "", [], "dock", "port");
        var snapshot = db.Database.Export("eu", "frontier-v2");
        var tile = Assert.Single(snapshot.Tiles);
        Assert.Equal("dock", tile.Caption);
        Assert.Equal("port", tile.Icon);

        using var other = new TempDb();
        other.Database.ApplyWorld([Node("explore-p1-m1", "Amber Woods", "forest", 1, -1)], "frontier-v2");
        var analysis = other.Database.Analyze(snapshot);
        Assert.Single(analysis.Adds);
        other.Database.ApplyImport(snapshot, new HashSet<string>());
        region = other.Database.LoadMap("eu").Regions.Single();
        Assert.Equal("dock", region.Caption);
        Assert.Equal("port", region.Icon);

        other.Database.SaveTileMeta("explore-p1-m1", "", [], "mine", "home");
        analysis = other.Database.Analyze(snapshot);
        Assert.Single(analysis.Conflicts);
        Assert.Contains("description: dock", analysis.Conflicts[0].TheirSummary, StringComparison.Ordinal);
        Assert.Contains("description: mine", analysis.Conflicts[0].LocalSummary, StringComparison.Ordinal);
        Assert.Contains("mark: Port", analysis.Conflicts[0].TheirSummary, StringComparison.Ordinal);
        Assert.Contains("mark: Home", analysis.Conflicts[0].LocalSummary, StringComparison.Ordinal);
        var kept = other.Database.ApplyImport(snapshot, new HashSet<string>());
        Assert.Equal(1, kept.Kept);
        region = other.Database.LoadMap("eu").Regions.Single();
        Assert.Equal("mine", region.Caption);
        Assert.Equal("home", region.Icon);
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

public class SeaWindTests
{
    private static readonly DateTime ReadAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void VersionTwoClockStartsTheDayAtSix()
    {
        var clock = new ServerClock(0, 0, 3_600_000, 2.0 / 3.0, 2, ReadAt);
        var hour = SeaWind.Hour(clock, ReadAt);
        Assert.NotNull(hour);
        Assert.Equal(6, hour!.Value, 5);
    }

    [Fact]
    public void OlderClockUsesAFlatDay()
    {
        var clock = new ServerClock(1_800_000, 0, 3_600_000, 0, 1, ReadAt);
        var hour = SeaWind.Hour(clock, ReadAt);
        Assert.NotNull(hour);
        Assert.Equal(12, hour!.Value, 5);
        Assert.Null(SeaWind.Hour(new ServerClock(0, 0, 1_000, 2.0 / 3.0, 2, ReadAt), ReadAt));
    }

    [Fact]
    public void BreezeFollowsTheClientHours()
    {
        Assert.Equal(0, SeaWind.Breeze(10), 6);
        Assert.Equal(0.45, SeaWind.Breeze(14.5), 6);
        Assert.Equal(0, SeaWind.Breeze(19), 6);
    }

    [Theory]
    [InlineData(0.45, 1)]
    [InlineData(0.450001, 2)]
    [InlineData(0.75, 2)]
    [InlineData(0.750001, 3)]
    public void MarkUsesTheCompassCuts(double strength, int mark) =>
        Assert.Equal(mark, SeaWind.Mark(strength));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 270)]
    [InlineData(270, 90)]
    public void GoingMirrorsTheGameBearing(double gameDirection, double going) =>
        Assert.Equal(going, SeaWind.GoingDegrees(gameDirection), 5);

    [Fact]
    public void WindAtIsStableAndInsideTheClientRange()
    {
        var region = Ocean("explore-p30-m10", 30, -10);
        var map = new Dictionary<(int Q, int R), RegionRow> { [(30, -10)] = region };
        var raw = SeaWind.WindAt(region, map, 1_700_000_000_000d, 14, false, null);
        var again = SeaWind.WindAt(region, map, 1_700_000_000_000d, 14, false, null);
        Assert.Equal(raw.Direction, again.Direction, 9);
        Assert.Equal(raw.Strength, again.Strength, 9);
        Assert.InRange(raw.Strength, 0.12, 1);
        Assert.InRange(raw.Direction, 0, 360);
        Assert.Equal(173.3737, Math.Round(raw.Direction, 4));
        Assert.Equal(0.3845, Math.Round(raw.Strength, 4));
    }

    [Theory]
    [InlineData("2026-11-09T14:03:48.2021614Z", 11, 21, 171.60, 0.48)]
    [InlineData("2026-11-09T14:06:32.3160364Z", 13, 47, 160.83, 0.68)]
    public void StandingHexMatchedTheLiveCompass(string utc, int hour, int minute, double direction, double strength)
    {
        var standing = new RegionRow
        {
            Id = "explore-p33-m14",
            Q = 33,
            R = -14,
            Name = "Longshadow Briar Ashlands",
            Biome = "volcanic_wastes",
        };
        var map = new Dictionary<(int Q, int R), RegionRow>
        {
            [(33, -14)] = standing,
            [(32, -14)] = Land("explore-p32-m14", 32, -14, "volcanic_wastes"),
            [(32, -13)] = Land("explore-p32-m13", 32, -13, "limestone_karst"),
            [(33, -13)] = Land("explore-p33-m13", 33, -13, "limestone_karst"),
        };
        var worldMs = DateTimeOffset.Parse(utc).ToUnixTimeMilliseconds();
        var raw = SeaWind.WindAt(standing, map, worldMs, hour + minute / 60.0, false, null);
        Assert.Equal(direction, Math.Round(raw.Direction, 2));
        Assert.Equal(strength, Math.Round(raw.Strength, 2));
        var going = SeaWind.GoingDegrees(raw.Direction);
        Assert.Equal("N", CompassText.Point(going + 180));
        Assert.Equal("S", CompassText.Point(going));
    }

    private static RegionRow Land(string id, int q, int r, string biome) => new()
    {
        Id = id,
        Q = q,
        R = r,
        Name = id,
        Biome = biome,
    };

    [Fact]
    public void OceansSkipLandAndKeepTheGaleOnTheRightTile()
    {
        var player = Ocean("explore-p0-p0", 0, 0);
        var edge = Ocean("explore-p10-p0", 10, 0);
        var land = new RegionRow { Id = "explore-p1-p0", Q = 1, R = 0, Name = "Shore", Biome = "forest" };
        var regions = new List<RegionRow> { player, edge, land };
        var byCoord = regions.ToDictionary(region => (region.Q, region.R));
        const double worldMs = 1_700_000_000_000d;
        var arrows = SeaWind.Oceans(regions, worldMs, 14, player.Id, false, null);
        Assert.False(arrows.ContainsKey(land.Id));
        var rawPlayer = SeaWind.WindAt(player, byCoord, worldMs, 14, false, null);
        var rawEdge = SeaWind.WindAt(edge, byCoord, worldMs, 14, true, null);
        Assert.Equal(SeaWind.Mark(rawPlayer.Strength), arrows[player.Id].Strength);
        Assert.Equal(SeaWind.GoingDegrees(rawPlayer.Direction), arrows[player.Id].DirectionDeg, 5);
        Assert.Equal(SeaWind.Mark(rawEdge.Strength), arrows[edge.Id].Strength);
        Assert.True(rawEdge.Strength >= SeaWind.GaleFloor);
    }

    [Fact]
    public void PreparedFieldMatchesWindAt()
    {
        var player = Ocean("explore-p0-p0", 0, 0);
        var edge = Ocean("explore-p10-p0", 10, 0);
        var land = new RegionRow { Id = "explore-p1-p0", Q = 1, R = 0, Name = "Shore", Biome = "forest" };
        var regions = new List<RegionRow> { player, edge, land };
        var byCoord = regions.ToDictionary(region => (region.Q, region.R));
        var actual = new Dictionary<string, SeaWindArrow>(StringComparer.Ordinal);
        SeaWind.SeaField.Build(regions).Fill(actual, 1_700_000_000_000d, 14, player.Id, false, "storm");
        foreach (var region in regions.Where(region => region.Biome == "ocean"))
        {
            var here = region.Id == player.Id;
            var neighbors = Axial.Neighbors(region.Q, region.R).Count(hex => byCoord.ContainsKey(hex));
            var raw = SeaWind.WindAt(region, byCoord, 1_700_000_000_000d, 14, here ? false : neighbors < 6, here ? "storm" : null);
            Assert.Equal(SeaWind.Mark(raw.Strength), actual[region.Id].Strength);
            Assert.Equal(SeaWind.GoingDegrees(raw.Direction), actual[region.Id].DirectionDeg, 5);
        }
    }

    private static RegionRow Ocean(string id, int q, int r) => new()
    {
        Id = id,
        Q = q,
        R = r,
        Name = id,
        Biome = "ocean",
    };
}

public class RegionMatchTests
{
    [Fact]
    public void UniqueTitleIsEnough()
    {
        var regions = new List<RegionRow>
        {
            Sea("explore-p34-m15", "Redleaf Whitefall Sea", 34, -15),
            Sea("explore-p34-m16", "Open Sea (p34-m16)", 34, -16),
        };
        Assert.Equal("explore-p34-m15", RegionMatch.Find(regions, "Redleaf Whitefall Sea", null));
    }

    [Fact]
    public void MinimapNamesPickOneOpenSea()
    {
        var regions = new List<RegionRow>
        {
            Sea("explore-p34-m15", "Redleaf Whitefall Sea", 34, -15),
            Sea("explore-p33-m15", "Amber Silver Sea", 33, -15),
            Sea("explore-p33-m16", "Poppy Waters", 33, -16),
            Sea("explore-p34-m16", "Open Sea (p34-m16)", 34, -16),
            Sea("explore-p35-m16", "Open Sea (p35-m16)", 35, -16),
            Sea("explore-p35-m17", "Open Sea (p35-m17)", 35, -17),
            Sea("explore-p34-m17", "Open Sea (p34-m17)", 34, -17),
            Sea("explore-p0-p0", "Open Sea (p0-p0)", 0, 0),
        };
        var id = RegionMatch.Find(regions, "Open Sea", ["Open Sea", "Redleaf Whitefall Sea", "Amber Silver Sea", "Poppy Waters", "You", "Sailboat"]);
        Assert.Equal("explore-p34-m16", id);
        Assert.Null(RegionMatch.Find(regions, "Open Sea", ["Open Sea"]));
    }

    private static RegionRow Sea(string id, string name, int q, int r) => new()
    {
        Id = id,
        Name = name,
        Q = q,
        R = r,
        Biome = "ocean",
    };
}

public class SyncPaceTests
{
    [Theory]
    [InlineData(null, 5)]
    [InlineData("", 5)]
    [InlineData("0", 1)]
    [InlineData("1", 1)]
    [InlineData("10", 10)]
    [InlineData("601", 600)]
    [InlineData("  3 ", 3)]
    public void SecondsStayBetweenOneAndTenMinutes(string? text, int expected) =>
        Assert.Equal(expected, SyncPace.ParseOrClamp(text, 5));
}

public class GameHudTests
{
    [Theory]
    [InlineData("14:05", 14 * 60 + 5)]
    [InlineData("dawn 9:00", 9 * 60)]
    [InlineData("24:00", null)]
    [InlineData("3:60", null)]
    public void ClockMinuteReadsTheHud(string text, int? minute) =>
        Assert.Equal(minute, GameHud.ClockMinute(text));

    [Fact]
    public void ZoneIdIsLowercased() =>
        Assert.Equal("explore-p21-m10", GameHud.ZoneId("explore-P21-M10 | ocean"));

    [Fact]
    public void WindFromIsTheOppositeOfWhereItIsGoing()
    {
        Assert.True(GameHud.TryWindFrom("Wind from NE", "≈", out var going, out var strength));
        Assert.Equal(225, going, 5);
        Assert.Equal(2, strength);
        Assert.True(GameHud.TryWindFrom("Wind from N", "~", out going, out strength));
        Assert.Equal(180, going, 5);
        Assert.Equal(1, strength);
        Assert.True(GameHud.TryWindFrom("Wind from S", "≋", out going, out strength));
        Assert.Equal(0, going, 5);
        Assert.Equal(3, strength);
    }
}
