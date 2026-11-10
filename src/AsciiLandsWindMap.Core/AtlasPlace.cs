namespace AsciiLandsWindMap;

/// <summary>One revealed hex as it sits on the open world map. X and Y are the center, in screen pixels.</summary>
public readonly record struct AtlasHit(string Name, double X, double Y);

/// <summary>Reads a world-map hex label. Visited and seen tiles both count. Plain text does not.</summary>
public static class AtlasLabel
{
    public static string? PlaceName(string? accessibleName)
    {
        if (string.IsNullOrWhiteSpace(accessibleName))
            return null;
        var seen = accessibleName.IndexOf(". Seen, not visited.", StringComparison.Ordinal);
        var visited = accessibleName.IndexOf(". Visited.", StringComparison.Ordinal);
        var index = -1;
        if (seen > 0)
            index = seen;
        if (visited > 0 && (index < 0 || visited < index))
            index = visited;
        if (index <= 0)
            return null;
        var place = accessibleName[..index].Trim();
        return place.Length == 0 ? null : place;
    }
}

public readonly record struct PlacedTile(string Id, string Name, bool OpenSea, string PngUrl);

public sealed class AtlasPlacement
{
    public List<PlacedTile> Tiles { get; init; } = [];
    public List<string> Unresolved { get; init; } = [];
}

/// <summary>
/// Turns on-screen visited tiles into wiki region ids.
/// A name that belongs to one region is that region.
/// A shared name, such as Open Sea, is the region whose hex sits under that tile.
/// </summary>
public static class AtlasPlace
{
    public static AtlasPlacement Place(IReadOnlyList<AtlasHit> hits, IReadOnlyList<GraphNode> nodes)
    {
        var byName = new Dictionary<string, List<ChartTile>>(StringComparer.OrdinalIgnoreCase);
        var byCoord = new Dictionary<(int Q, int R), ChartTile>();
        foreach (var node in nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id) || node.Coord is not { Length: >= 2 })
                continue;
            var name = (node.Name ?? "").Trim();
            if (name.Length == 0)
                continue;
            var tile = new ChartTile(
                node.Id.Trim(),
                name,
                node.Coord[0],
                node.Coord[1],
                node.OpenSea,
                node.OpenSea ? "" : WorldGraph.MinimapUrl(node.MapRel));
            if (!byCoord.ContainsKey((tile.Q, tile.R)))
                byCoord[(tile.Q, tile.R)] = tile;
            if (!byName.TryGetValue(name, out var list))
            {
                list = [];
                byName[name] = list;
            }
            list.Add(tile);
        }

        var groups = new Dictionary<string, List<AtlasHit>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var hit in hits)
        {
            var name = hit.Name.Trim();
            if (name.Length == 0)
                continue;
            if (!groups.TryGetValue(name, out var list))
            {
                list = [];
                groups[name] = list;
                order.Add(name);
            }
            list.Add(hit);
        }

        var placedIds = new HashSet<string>(StringComparer.Ordinal);
        var placedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tiles = new List<PlacedTile>();
        var anchors = new List<Anchor>();
        var ambiguous = new List<string>();

        foreach (var name in order)
        {
            if (!byName.TryGetValue(name, out var named) || named.Count == 0)
                continue;
            if (named.Count == 1)
            {
                Add(tiles, placedIds, placedNames, named[0]);
                var cluster = Cluster(groups[name]);
                if (cluster != null)
                {
                    var (plotX, plotY) = new HexLayout(1).Center(named[0].Q, named[0].R);
                    anchors.Add(new Anchor(cluster.Value.X, cluster.Value.Y, plotX, plotY, named[0].Id));
                }
                continue;
            }
            ambiguous.Add(name);
        }

        if (ambiguous.Count > 0 && TryFit(anchors, out var originX, out var originY, out var scale) && AnchorsHold(anchors, byCoord, originX, originY, scale))
        {
            foreach (var name in ambiguous)
            {
                foreach (var hit in groups[name])
                {
                    if (TrySnap(hit.X, hit.Y, originX, originY, scale, byCoord, name, out var tile))
                        Add(tiles, placedIds, placedNames, tile);
                }
            }
        }

        var unresolved = new List<string>();
        foreach (var name in order)
        {
            if (!placedNames.Contains(name))
                unresolved.Add(name);
        }

        return new AtlasPlacement { Tiles = tiles, Unresolved = unresolved };
    }

    private static void Add(List<PlacedTile> tiles, HashSet<string> placedIds, HashSet<string> placedNames, ChartTile tile)
    {
        if (!placedIds.Add(tile.Id))
            return;
        placedNames.Add(tile.Name);
        tiles.Add(new PlacedTile(tile.Id, tile.Name, tile.OpenSea, tile.PngUrl));
    }

    private static (double X, double Y)? Cluster(IReadOnlyList<AtlasHit> hits)
    {
        if (hits.Count == 0)
            return null;
        double x = 0, y = 0;
        foreach (var hit in hits)
        {
            var dx = hit.X - hits[0].X;
            var dy = hit.Y - hits[0].Y;
            if (Math.Sqrt(dx * dx + dy * dy) > 12)
                return null;
            x += hit.X;
            y += hit.Y;
        }
        return (x / hits.Count, y / hits.Count);
    }

    private static bool TryFit(IReadOnlyList<Anchor> anchors, out double originX, out double originY, out double scale)
    {
        originX = 0;
        originY = 0;
        scale = 0;
        if (anchors.Count < 2)
            return false;
        double meanX = 0, meanY = 0, meanPlotX = 0, meanPlotY = 0;
        foreach (var anchor in anchors)
        {
            meanX += anchor.X;
            meanY += anchor.Y;
            meanPlotX += anchor.PlotX;
            meanPlotY += anchor.PlotY;
        }
        var count = anchors.Count;
        meanX /= count;
        meanY /= count;
        meanPlotX /= count;
        meanPlotY /= count;
        double numerator = 0, denominator = 0;
        foreach (var anchor in anchors)
        {
            var dx = anchor.X - meanX;
            var dy = anchor.Y - meanY;
            var plotX = anchor.PlotX - meanPlotX;
            var plotY = anchor.PlotY - meanPlotY;
            numerator += dx * plotX + dy * plotY;
            denominator += plotX * plotX + plotY * plotY;
        }
        if (denominator < 1e-6)
            return false;
        scale = numerator / denominator;
        if (scale < 8 || scale > 400)
            return false;
        originX = meanX - scale * meanPlotX;
        originY = meanY - scale * meanPlotY;
        return true;
    }

    private static bool AnchorsHold(IReadOnlyList<Anchor> anchors, Dictionary<(int Q, int R), ChartTile> byCoord, double originX, double originY, double scale)
    {
        foreach (var anchor in anchors)
        {
            var name = "";
            foreach (var tile in byCoord.Values)
            {
                if (tile.Id == anchor.Id)
                {
                    name = tile.Name;
                    break;
                }
            }
            if (!TrySnap(anchor.X, anchor.Y, originX, originY, scale, byCoord, name, out var snapped) || snapped.Id != anchor.Id)
                return false;
        }
        return true;
    }

    private static bool TrySnap(double x, double y, double originX, double originY, double scale, Dictionary<(int Q, int R), ChartTile> byCoord, string tileName, out ChartTile tile)
    {
        tile = default;
        var plotX = (x - originX) / scale;
        var plotY = (y - originY) / scale;
        var fractionalQ = Math.Sqrt(3) / 3 * plotX - plotY / 3;
        var fractionalR = 2.0 / 3 * plotY;
        var (q, r) = Axial.Round(fractionalQ, fractionalR);
        var dq = fractionalQ - q;
        var dr = fractionalR - r;
        var ds = -dq - dr;
        var residual = (Math.Abs(dq) + Math.Abs(dr) + Math.Abs(ds)) / 2;
        if (residual > 0.35)
            return false;
        if (!byCoord.TryGetValue((q, r), out tile))
            return false;
        return tileName.Length == 0 || tile.Name.Equals(tileName, StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct ChartTile(string Id, string Name, int Q, int R, bool OpenSea, string PngUrl);

    private readonly record struct Anchor(double X, double Y, double PlotX, double PlotY, string Id);
}
