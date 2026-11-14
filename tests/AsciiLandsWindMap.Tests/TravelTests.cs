using Microsoft.Data.Sqlite;
using Xunit;

namespace AsciiLandsWindMap.Tests;

public class WaterChartTests
{
    [Fact]
    public void BlueMeansWaterAndChromeDoesNot()
    {
        Assert.True(WaterColor.IsBlue(38, 76, 108, 255));
        Assert.True(WaterColor.IsBlue(20, 40, 180, 255));
        Assert.False(WaterColor.IsBlue(38, 60, 57, 255));
        Assert.False(WaterColor.IsBlue(200, 213, 161, 255));
        Assert.False(WaterColor.IsBlue(229, 214, 162, 255));
        Assert.False(WaterColor.IsBlue(239, 182, 139, 255));
        Assert.False(WaterColor.IsBlue(24, 40, 54, 255));
    }

    [Fact]
    public void EastBlueSetsOnlyTheEastEdge()
    {
        const int size = 48;
        var pixels = new byte[size * size * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 50;
            pixels[i + 1] = 90;
            pixels[i + 2] = 40;
            pixels[i + 3] = 255;
        }
        var cx = (size - 1) / 2.0;
        var cy = (size - 1) / 2.0;
        var radius = size / 2.0;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                var dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist < radius * 0.72 || dist > radius * 0.98)
                    continue;
                if (WaterEdges.SideOf(dx, dy) != 0)
                    continue;
                var i = (y * size + x) * 4;
                pixels[i] = 180;
                pixels[i + 1] = 70;
                pixels[i + 2] = 30;
                pixels[i + 3] = 255;
            }
        }

        var profile = WaterEdges.FromBgra(size, size, pixels, size * 4);
        Assert.Equal(1, profile.Edges);
        Assert.Equal(1, WaterProfile.Body(profile.Touch, 0));
        Assert.NotEqual(0, WaterProfile.Bins(profile.Spans, 0));
    }

    [Fact]
    public void OneBlueStretchSharesABodyAndTwoPondsDoNot()
    {
        const int size = 96;
        var joined = Blank(size);
        var split = Blank(size);
        var cx = (size - 1) / 2.0;
        var cy = (size - 1) / 2.0;
        var radius = size / 2.0;
        var inner = radius * 0.90;
        var outer = radius * 0.995;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                var dist = Math.Sqrt(dx * dx + dy * dy);
                if (Math.Abs(dy) <= 4 && dist <= outer)
                    PaintBlue(joined, size, x, y);
                if (dist < inner || dist > outer)
                    continue;
                var side = WaterEdges.SideOf(dx, dy);
                if (side is 0 or 3)
                    PaintBlue(split, size, x, y);
            }
        }

        var river = WaterEdges.FromBgra(size, size, joined, size * 4);
        Assert.NotEqual(0, WaterProfile.Body(river.Touch, 0));
        Assert.Equal(WaterProfile.Body(river.Touch, 0), WaterProfile.Body(river.Touch, 3));

        var ponds = WaterEdges.FromBgra(size, size, split, size * 4);
        Assert.NotEqual(0, WaterProfile.Body(ponds.Touch, 0));
        Assert.NotEqual(0, WaterProfile.Body(ponds.Touch, 3));
        Assert.NotEqual(WaterProfile.Body(ponds.Touch, 0), WaterProfile.Body(ponds.Touch, 3));
    }

    private static byte[] Blank(int size)
    {
        var pixels = new byte[size * size * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 50;
            pixels[i + 1] = 90;
            pixels[i + 2] = 40;
            pixels[i + 3] = 255;
        }
        return pixels;
    }

    private static void PaintBlue(byte[] pixels, int size, int x, int y)
    {
        var i = (y * size + x) * 4;
        pixels[i] = 180;
        pixels[i + 1] = 70;
        pixels[i + 2] = 30;
        pixels[i + 3] = 255;
    }

    [Fact]
    public void SideBearingsMatchTheCompass()
    {
        Assert.Equal(90, Axial.SideBearing(0), 0);
        Assert.Equal(30, Axial.SideBearing(1), 0);
        Assert.Equal(3, Axial.Opposite(0));
        var (x, y) = Axial.SideVector(1);
        Assert.True(x > 0);
        Assert.True(y < 0);
    }
}

public class SurveyMatchTests
{
    [Fact]
    public void IdWinsAndAUniqueNameOverlaysTheWikiTile()
    {
        var regions = new List<RegionRow>
        {
            new() { Id = "a", Q = 0, R = 0, Name = "Amber", Biome = "forest" },
            new() { Id = "b", Q = 1, R = 0, Name = "Bree", Biome = "plains" },
        };
        Assert.True(SurveyMatch.TryMatch(regions, "a", "Something else", out var id, out var problem));
        Assert.Equal("a", id);
        Assert.Null(problem);
        Assert.True(SurveyMatch.TryMatch(regions, "missing", "bree", out id, out problem));
        Assert.Equal("b", id);
        Assert.False(SurveyMatch.TryMatch(regions, "missing", "Nowhere", out _, out problem));
        Assert.Equal("Nowhere", problem);
    }
}

public class RouteTests
{
    [Fact]
    public void OceanTimeTreatsWindAsTraffic()
    {
        Assert.Equal(1, TravelCost.Ocean(90, null), 3);
        Assert.Equal(0.55, TravelCost.Ocean(90, new WindAt(90, 3)), 3);
        Assert.Equal(1.45, TravelCost.Ocean(90, new WindAt(270, 3)), 3);
    }

    [Fact]
    public void WalkCrossesLandAndRiversAndStopsAtTheOcean()
    {
        var blocked = RouteFinder.Find(
        [
            new("land-a", 0, 0, false, 0),
            new("sea", 1, 0, true, 0),
            new("land-b", 2, 0, false, 0),
        ], "land-a", "land-b", TravelMode.Walk, null);
        Assert.False(blocked.Found);

        var open = RouteFinder.Find(
        [
            new("land-a", 0, 0, false, 0),
            new("river", 1, 0, false, 0),
            new("land-b", 2, 0, false, 0),
        ], "land-a", "land-b", TravelMode.Walk, null);
        Assert.True(open.Found);
        Assert.Equal(["land-a", "river", "land-b"], open.Steps.Select(step => step.RegionId));
        Assert.Equal(-1, open.Steps[0].EnterSide);
        Assert.Equal(0, open.Steps[0].ExitSide);
        Assert.Equal(3, open.Steps[1].EnterSide);
        Assert.Equal(-1, open.Steps[^1].ExitSide);
    }

    [Fact]
    public void BoatOnLandWalksToWaterThenSailsAndLandsOnTheShore()
    {
        var plan = RouteFinder.Find(
        [
            new("dry", 0, 0, false, 0),
            new("shore", 1, 0, false, 1),
            new("sea", 2, 0, true, 0),
            new("far", 3, 0, true, 0),
            new("camp", 3, 1, false, 0),
        ], "dry", "camp", TravelMode.Boat, null, id => id);
        Assert.True(plan.Found);
        Assert.False(plan.WalkRemainder);
        Assert.Equal("camp", plan.EndId);
        Assert.Equal(["dry", "shore", "sea", "far", "camp"], plan.Steps.Select(step => step.RegionId));
        Assert.Equal("Walk to the water, then sail.", plan.Status);
        Assert.Equal(2, plan.Steps[^1].EnterSide);
        Assert.Equal(-1, plan.Steps[^1].ExitSide);
    }

    [Fact]
    public void BoatToInlandLandStopsOnTheShoreline()
    {
        var plan = RouteFinder.Find(
        [
            new("sea", -1, 0, true, 0),
            new("beach", 0, 0, false, 8),
            new("inland", 1, 0, false, 0),
        ], "sea", "inland", TravelMode.Boat, null, id => id);
        Assert.True(plan.Found);
        Assert.True(plan.WalkRemainder);
        Assert.Equal("beach", plan.EndId);
        Assert.Equal(["sea", "beach"], plan.Steps.Select(step => step.RegionId));
        Assert.Equal(3, plan.Steps[^1].EnterSide);
        Assert.Contains("beach", plan.Status, StringComparison.Ordinal);
        Assert.Contains("walk you do yourself", plan.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BoatLandsOnTheShorelineInsteadOfACloserOcean()
    {
        var plan = RouteFinder.Find(
        [
            new("sea", 0, 0, true, 0),
            new("east", 1, 0, true, 0),
            new("south", 2, 0, true, 0),
            new("rise", 2, 1, true, 0),
            new("near", 2, 2, true, 0),
            new("beach", 0, 1, false, 4),
            new("mid", 0, 2, false, 0),
            new("camp", 0, 3, false, 0),
        ], "sea", "camp", TravelMode.Boat, null, id => id);
        Assert.True(plan.Found);
        Assert.True(plan.WalkRemainder);
        Assert.Equal("beach", plan.EndId);
        Assert.Equal(["sea", "beach"], plan.Steps.Select(step => step.RegionId));
        Assert.DoesNotContain("near", plan.Steps.Select(step => step.RegionId));
        Assert.Contains("walk you do yourself", plan.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BoatStopsOnTheRiverMouthAndDoesNotSailPastIt()
    {
        var plan = RouteFinder.Find(
        [
            new("dry", -1, 0, false, 0),
            new("mouth", 0, 0, false, (byte)(1 | 8)),
            new("sea", 1, 0, true, 0),
            new("camp", 0, -1, false, 0),
        ], "dry", "camp", TravelMode.Boat, null, id => id);
        Assert.True(plan.Found);
        Assert.True(plan.WalkRemainder);
        Assert.Equal("mouth", plan.EndId);
        Assert.Equal(["dry", "mouth"], plan.Steps.Select(step => step.RegionId));
        Assert.Contains("mouth", plan.Status, StringComparison.Ordinal);
        Assert.Contains("walk you do yourself", plan.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BoatFallsBackToTheNearestOceanWhenNoShoreTouchesIt()
    {
        var plan = RouteFinder.Find(
        [
            new("sea", 0, 0, true, 0),
            new("camp", 0, 2, false, 0),
        ], "sea", "camp", TravelMode.Boat, null, id => id);
        Assert.True(plan.Found);
        Assert.True(plan.WalkRemainder);
        Assert.Equal("sea", plan.EndId);
        Assert.Equal(["sea"], plan.Steps.Select(step => step.RegionId));
        Assert.Contains("walk you do yourself", plan.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DryShoreDoesNotStepOntoTheOcean()
    {
        var plan = RouteFinder.Find(
        [
            new("dry", 0, 0, false, 0),
            new("sea", 1, 0, true, 0),
            new("far", 2, 0, true, 0),
        ], "dry", "far", TravelMode.Boat, null);
        Assert.False(plan.Found);
    }

    [Fact]
    public void RiverOnTheInlandSideDoesNotBoard()
    {
        var plan = RouteFinder.Find(
        [
            new("bank", 0, 0, false, 8),
            new("sea", 1, 0, true, 0),
        ], "bank", "sea", TravelMode.Boat, null);
        Assert.False(plan.Found);
    }

    [Fact]
    public void SeparatePondsDoNotBridgeAndASharedBodyDoes()
    {
        static ulong Touch(int side, int body) => (ulong)body << (side * 4);
        static ulong Span(int side, int bins) => (ulong)bins << (side * 8);
        var west = new RouteHex("west", 0, 0, false, 1, Touch(0, 1), Span(0, 0xFF));
        var sea = new RouteHex("sea", 2, 0, true, 0);
        var split = new RouteHex("mid", 1, 0, false, 1 | 8, Touch(3, 2) | Touch(0, 1), Span(3, 0xFF) | Span(0, 0xFF));
        var joined = new RouteHex("mid", 1, 0, false, 1 | 8, Touch(3, 1) | Touch(0, 1), Span(3, 0xFF) | Span(0, 0xFF));
        Assert.False(RouteFinder.Find([west, split, sea], "west", "sea", TravelMode.Boat, null).Found);
        var open = RouteFinder.Find([west, joined, sea], "west", "sea", TravelMode.Boat, null);
        Assert.True(open.Found);
        Assert.Equal(["west", "mid", "sea"], open.Steps.Select(step => step.RegionId));
    }

    [Fact]
    public void BlueThatMissesAlongTheSharedEdgeDoesNotConnect()
    {
        static ulong Touch(int side, int body) => (ulong)body << (side * 4);
        static ulong Span(int side, int bins) => (ulong)bins << (side * 8);
        var west = new RouteHex("west", 0, 0, false, 1, Touch(0, 1), Span(0, 0x01));
        var sea = new RouteHex("sea", 2, 0, true, 0);
        var miss = new RouteHex("mid", 1, 0, false, 1 | 8, Touch(3, 1) | Touch(0, 1), Span(3, 0x01) | Span(0, 0xFF));
        var hit = new RouteHex("mid", 1, 0, false, 1 | 8, Touch(3, 1) | Touch(0, 1), Span(3, 0x80) | Span(0, 0xFF));
        Assert.False(RouteFinder.Find([west, miss, sea], "west", "sea", TravelMode.Boat, null).Found);
        Assert.True(RouteFinder.Find([west, hit, sea], "west", "sea", TravelMode.Boat, null).Found);
    }

    [Fact]
    public void RiverMouthJoinsTheOcean()
    {
        var plan = RouteFinder.Find(
        [
            new("dry", -1, 0, false, 0),
            new("mouth-west", 0, 0, false, 1),
            new("mouth", 1, 0, false, (byte)(1 | 8)),
            new("sea", 2, 0, true, 0),
        ], "dry", "sea", TravelMode.Boat, null);
        Assert.True(plan.Found);
        Assert.Equal(["dry", "mouth-west", "mouth", "sea"], plan.Steps.Select(step => step.RegionId));
    }

    [Fact]
    public void TailwindCorridorBeatsTheHeadwindOne()
    {
        var hexes = new List<RouteHex>
        {
            new("s", 0, 0, true, 0),
            new("n0", 0, -1, true, 0),
            new("n1", 1, -1, true, 0),
            new("n2", 2, -1, true, 0),
            new("g", 2, 0, true, 0),
            new("s0", 0, 1, true, 0),
            new("s1", 1, 1, true, 0),
            new("s2", 2, 1, true, 0),
        };
        var wind = new Dictionary<string, WindAt>
        {
            ["n0"] = new(90, 3),
            ["n1"] = new(90, 3),
            ["n2"] = new(150, 3),
            ["s0"] = new(270, 3),
            ["s1"] = new(270, 3),
            ["s2"] = new(150, 3),
        };
        var plan = RouteFinder.Find(hexes, "s", "g", TravelMode.Boat, wind);
        Assert.True(plan.Found);
        Assert.Contains("n1", plan.Steps.Select(step => step.RegionId));
        Assert.DoesNotContain("s1", plan.Steps.Select(step => step.RegionId));
        Assert.True(plan.Time < 3);
    }

    [Fact]
    public void PassedIndexStaysOnTheLine()
    {
        var plan = RouteFinder.Find(
        [
            new("a", 0, 0, true, 0),
            new("b", 1, 0, true, 0),
            new("c", 2, 0, true, 0),
        ], "a", "c", TravelMode.Boat, null);
        Assert.Equal(0, RouteFinder.IndexOnRoute(plan.Steps, "a"));
        Assert.Equal(1, RouteFinder.IndexOnRoute(plan.Steps, "b"));
        Assert.Equal(-1, RouteFinder.IndexOnRoute(plan.Steps, "elsewhere"));
    }
}

public class OverlayTests
{
    [Fact]
    public void OverlayPointsTheWindAndDarkensTheEntryHand()
    {
        var east = MinimapOverlay.Arrow(new WindSample(90, 3, 1, false));
        var north = MinimapOverlay.Arrow(new WindSample(0, 1, 1, false));
        var fresh = MinimapOverlay.Arrow(new WindSample(45, 2, 1, false));
        Assert.True(east.Shown);
        Assert.True(east.TipX > east.TailX + 10);
        Assert.InRange(east.TipY, east.TailY - 0.01, east.TailY + 0.01);
        Assert.True(east.BaseX > east.TailX);
        Assert.True(east.BaseX < east.TipX);
        Assert.True(north.TipY < north.TailY - 5);
        Assert.InRange(north.TipX, north.TailX - 0.01, north.TailX + 0.01);
        Assert.True(Span(east) > Span(fresh));
        Assert.True(Span(fresh) > Span(north));
        Assert.False(MinimapOverlay.Arrow(null).Shown);
        Assert.False(MinimapOverlay.Arrow(new WindSample(180, 2, 1, true)).Shown);
        Assert.False(MinimapOverlay.Arrow(new WindSample(180, 0, 1, false)).Shown);

        var markup = MinimapOverlay.Markup(east, enterSide: 3, exitSide: 0);
        Assert.Contains("GPS", markup, StringComparison.Ordinal);
        Assert.Contains("#6a7560", markup, StringComparison.Ordinal);
        Assert.Contains("#eef5d8", markup, StringComparison.Ordinal);
        Assert.Contains("<polygon", WindGroup(markup), StringComparison.Ordinal);
        Assert.DoesNotContain("toward", markup, StringComparison.Ordinal);
        var calm = MinimapOverlay.Markup(WindArrow.Calm, 3, 0);
        Assert.Contains("<circle", WindGroup(calm), StringComparison.Ordinal);
        Assert.DoesNotContain("<polygon", WindGroup(calm), StringComparison.Ordinal);
        var script = MinimapOverlay.InstallScript(east, 3, 0);
        Assert.Contains("minimap-svg", script, StringComparison.Ordinal);
        Assert.Contains(MinimapOverlay.ElementId, MinimapOverlay.RemoveScript, StringComparison.Ordinal);
    }

    private static double Span(WindArrow arrow)
    {
        var dx = arrow.TipX - arrow.TailX;
        var dy = arrow.TipY - arrow.TailY;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static string WindGroup(string markup)
    {
        var mark = $"id=\"{MinimapOverlay.WindId}\"";
        var start = markup.IndexOf(mark, StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = markup.IndexOf("</g>", start, StringComparison.Ordinal);
        Assert.True(end > start);
        return markup[start..end];
    }
}

public class SurveyStoreTests
{
    [Fact]
    public void ALaterReadReplacesThePicture()
    {
        var folder = Path.Combine(Path.GetTempPath(), "windmap-tests", Guid.NewGuid().ToString("n"));
        var path = Path.Combine(folder, "wind.sqlite");
        using (var database = new WindDatabase(path))
        {
            database.SaveSurvey("explore-p0-p0", 1, [1, 2, 3], false);
            database.SaveSurvey("explore-p0-p0", 63, [9], true);
            var row = Assert.Single(database.LoadSurveys());
            Assert.Equal(63, row.Edges);
            Assert.Equal(new byte[] { 9 }, row.Png);
            Assert.True(row.OpenSea);
        }
        SqliteConnection.ClearAllPools();
        Directory.Delete(folder, true);
    }
}
