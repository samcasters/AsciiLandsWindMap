using System.Text;
using Xunit;

namespace AsciiLandsWindMap.Tests;

public class AtlasTests
{
    [Fact]
    public void OpenSeaHitsSnapToTheHexTheySitOn()
    {
        var nodes = new List<GraphNode>
        {
            Node("explore-p34-m10", "Moss Raven Sea", 34, -10, "assets/a3d/worlds/coastal/expansion-explore-p34-m10_full.a3d"),
            Node("explore-p34-m11", "Amber Briar Sea", 34, -11),
            Node("explore-p22-m6", "Silver Elder Fen", 22, -6),
            Node("explore-p36-m15", "Open Sea", 36, -15, openSea: true, mapRel: "assets/a3d/worlds/hidden/shared.a3d"),
            Node("explore-m80-p80", "Open Sea", -80, 80, openSea: true),
        };
        var hits = new List<AtlasHit>
        {
            new("Moss Raven Sea", 7369.5, 1899),
            new("Amber Briar Sea", 7339.5, 1845),
            new("Silver Elder Fen", 6746.5, 2115),
            new("Open Sea", 7339.5, 1629),
            new("Nope", 10, 10),
        };

        var placed = AtlasPlace.Place(hits, nodes);

        var moss = placed.Tiles.Single(tile => tile.Id == "explore-p34-m10");
        Assert.False(moss.OpenSea);
        Assert.Equal("https://asciilands.io/play/assets/a3d/worlds/coastal/expansion-explore-p34-m10_full.minimap.png", moss.PngUrl);
        var sea = placed.Tiles.Single(tile => tile.Name == "Open Sea");
        Assert.Equal("explore-p36-m15", sea.Id);
        Assert.True(sea.OpenSea);
        Assert.Equal("", sea.PngUrl);
        Assert.Equal(["Nope"], placed.Unresolved);
    }

    [Fact]
    public void ASharedNameWithoutTwoAnchorsStaysUnresolved()
    {
        var nodes = new List<GraphNode>
        {
            Node("land", "Amber", 0, 0),
            Node("sea-a", "Open Sea", 4, 0, openSea: true),
            Node("sea-b", "Open Sea", 5, 0, openSea: true),
        };
        var placed = AtlasPlace.Place(
            [new AtlasHit("Amber", 500, 800), new AtlasHit("Open Sea", 560, 800)],
            nodes);

        Assert.Equal("land", placed.Tiles.Single().Id);
        Assert.Equal(["Open Sea"], placed.Unresolved);
    }

    [Fact]
    public void SeenTilesCountAsWellAsVisitedOnes()
    {
        Assert.Equal("Moss Raven Sea", AtlasLabel.PlaceName("Moss Raven Sea. Visited. Double-click or press Enter to open local map."));
        Assert.Equal("Amber Briar Sea", AtlasLabel.PlaceName("Amber Briar Sea. Seen, not visited."));
        Assert.Null(AtlasLabel.PlaceName("Open Sea"));
        Assert.Null(AtlasLabel.PlaceName("?"));
        Assert.Null(AtlasLabel.PlaceName(". Visited."));
    }

    [Fact]
    public void MinimapUrlReplacesTheModel()
    {
        Assert.Equal(
            "https://asciilands.io/play/assets/tile.minimap.png",
            WorldGraph.MinimapUrl("assets/tile.a3d"));
        Assert.Equal("https://asciilands.io/play/assets/tile.minimap.png", WorldGraph.MinimapUrl("https://asciilands.io/play/assets/tile.minimap.png"));
        var json = """{"nodes":[{"id":"a","name":"Amber","coord":[1,2],"map_rel":"assets/a.a3d"}]}""";
        var nodes = WorldGraph.Read(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        Assert.Equal("a", nodes[0].Id);
        Assert.Equal(2, nodes[0].Coord![1]);
    }

    private static GraphNode Node(string id, string name, int q, int r, string? mapRel = null, bool openSea = false) =>
        new()
        {
            Id = id,
            Name = name,
            Coord = [q, r],
            MapRel = mapRel,
            OpenSea = openSea,
        };
}
