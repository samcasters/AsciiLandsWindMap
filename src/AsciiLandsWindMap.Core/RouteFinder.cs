namespace AsciiLandsWindMap;

public enum TravelMode
{
    Boat,
    Walk,
}

public readonly record struct RouteHex(string Id, int Q, int R, bool Ocean, byte Water, ulong Touch = 0, ulong Spans = 0)
{
    public int Body(int side)
    {
        if (Ocean || side is < 0 or > 5)
            return Ocean ? 1 : 0;
        if (Touch == 0 && Spans == 0)
            return WaterMask.Has(Water, side) ? 1 : 0;
        return WaterProfile.Body(Touch, side);
    }

    /// <summary>True when blue on this side meets blue on the neighbor's opposite side.</summary>
    public bool Meets(int side, RouteHex other)
    {
        var opposite = Axial.Opposite(side);
        if (Body(side) == 0 || other.Body(opposite) == 0)
            return false;
        var mine = Ocean || Spans == 0 ? 0xFF : WaterProfile.Bins(Spans, side);
        var theirs = other.Ocean || other.Spans == 0 ? 0xFF : WaterProfile.Bins(other.Spans, opposite);
        return (mine & WaterProfile.ReverseBins(theirs)) != 0;
    }
}

public readonly record struct WindAt(double GoingDeg, int Strength);

public readonly record struct RouteStep(string RegionId, int EnterSide, int ExitSide);

public sealed class RoutePlan
{
    public required bool Found { get; init; }
    public required TravelMode Mode { get; init; }
    public required string DestinationId { get; init; }
    public required string EndId { get; init; }
    public required IReadOnlyList<RouteStep> Steps { get; init; }
    public required string Status { get; init; }
    public bool WalkRemainder { get; init; }
    public double Time { get; init; }

    public static RoutePlan None(TravelMode mode, string destinationId, string status) => new()
    {
        Found = false,
        Mode = mode,
        DestinationId = destinationId,
        EndId = "",
        Steps = [],
        Status = status,
    };
}

public static class TravelCost
{
    public const double MinStep = 0.35;

    public static double Ocean(double travelDeg, WindAt? wind)
    {
        if (wind is not WindAt at || at.Strength <= 0)
            return 1;
        var align = Math.Cos((travelDeg - at.GoingDeg) * Math.PI / 180.0);
        var strength = Math.Clamp(at.Strength, 1, 3) / 3.0;
        return Math.Max(MinStep, 1 - 0.45 * align * strength);
    }
}

/// <summary>
/// A* on hexes. A hex is an intersection and a shared side is a road. Wind is traffic:
/// the cost of an ocean crossing is time, and the search minimizes time.
/// </summary>
public static class RouteFinder
{
    public static RoutePlan Find(
        IReadOnlyList<RouteHex> hexes,
        string startId,
        string destinationId,
        TravelMode mode,
        IReadOnlyDictionary<string, WindAt>? wind,
        Func<string, string>? nameOf = null)
    {
        var byId = new Dictionary<string, RouteHex>(hexes.Count, StringComparer.Ordinal);
        var byCoord = new Dictionary<(int Q, int R), RouteHex>(hexes.Count);
        foreach (var hex in hexes)
        {
            byId[hex.Id] = hex;
            byCoord[(hex.Q, hex.R)] = hex;
        }

        if (!byId.TryGetValue(startId, out var start) || !byId.ContainsKey(destinationId))
            return RoutePlan.None(mode, destinationId, "That tile is not on this map.");

        if (startId == destinationId)
        {
            return new RoutePlan
            {
                Found = true,
                Mode = mode,
                DestinationId = destinationId,
                EndId = startId,
                Steps = [new RouteStep(startId, -1, -1)],
                Status = "You are already on that tile.",
            };
        }

        if (mode == TravelMode.Walk)
            return Walk(byId, byCoord, start, destinationId);

        return Boat(byId, byCoord, start, destinationId, wind, nameOf);
    }

    public static int IndexOnRoute(IReadOnlyList<RouteStep> steps, string regionId)
    {
        for (var i = steps.Count - 1; i >= 0; i--)
        {
            if (steps[i].RegionId == regionId)
                return i;
        }
        return -1;
    }

    private static RoutePlan Walk(
        Dictionary<string, RouteHex> byId,
        Dictionary<(int Q, int R), RouteHex> byCoord,
        RouteHex start,
        string destinationId)
    {
        var dest = byId[destinationId];
        if (start.Ocean || dest.Ocean)
        {
            return RoutePlan.None(TravelMode.Walk, destinationId,
                "A walk stays on land. It can cross rivers, and it does not cross the ocean.");
        }

        var ids = Breadth(start.Id, destinationId, byId, byCoord, sail: false, wind: null);
        if (ids == null)
            return RoutePlan.None(TravelMode.Walk, destinationId, "No walk reaches that tile.");

        return Finish(TravelMode.Walk, destinationId, destinationId, ids, byId, byCoord, wind: null,
            $"Walk across {ids.Count} hexes.", walkRemainder: false);
    }

    private static RoutePlan Boat(
        Dictionary<string, RouteHex> byId,
        Dictionary<(int Q, int R), RouteHex> byCoord,
        RouteHex start,
        string destinationId,
        IReadOnlyDictionary<string, WindAt>? wind,
        Func<string, string>? nameOf)
    {
        var dest = byId[destinationId];
        var oceans = new List<RouteHex>();
        foreach (var hex in byId.Values)
        {
            if (hex.Ocean)
                oceans.Add(hex);
        }

        IEnumerable<RouteHex> targets = dest.Ocean
            ? [dest]
            : oceans
                .OrderBy(hex => Axial.Distance(hex.Q, hex.R, dest.Q, dest.R))
                .ThenBy(hex => hex.Id, StringComparer.Ordinal);

        foreach (var target in targets)
        {
            var reach = Reach(target.Id, byId, byCoord, wind);
            if (!reach.Contains(new SailNode(target.Id, 0)))
                continue;
            if (!TryBoard(start, target.Id, reach, byId, byCoord, wind, out var ids))
                continue;

            var endName = nameOf?.Invoke(target.Id) ?? target.Id;
            var walked = ids.Count > 0 && ids[0] == start.Id && !IsWater(start) && ids.Any(id => id != start.Id);
            var status = dest.Ocean
                ? walked ? "Walk to the water, then sail." : "Boat route."
                : walked
                    ? $"Walk to the water, then sail. The sail ends at {endName}. The rest is a walk you do yourself."
                    : $"The sail ends at {endName}. The rest is a walk you do yourself.";
            if (dest.Ocean && !walked)
                status = "Boat route.";
            return Finish(TravelMode.Boat, destinationId, target.Id, ids, byId, byCoord, wind, status, walkRemainder: !dest.Ocean);
        }

        return RoutePlan.None(TravelMode.Boat, destinationId,
            "No water on the map connects your hex to that destination.");
    }

    private static bool TryBoard(
        RouteHex start,
        string targetId,
        HashSet<SailNode> reach,
        Dictionary<string, RouteHex> byId,
        Dictionary<(int Q, int R), RouteHex> byCoord,
        IReadOnlyDictionary<string, WindAt>? wind,
        out List<string> ids)
    {
        ids = [];
        var target = new SailNode(targetId, 0);
        if (IsWater(start))
        {
            List<string>? sailed = null;
            foreach (var node in Bodies(start))
            {
                if (!reach.Contains(node))
                    continue;
                var path = Shortest(node, target, byId, byCoord, wind);
                if (path == null)
                    continue;
                if (sailed == null || path.Count < sailed.Count)
                    sailed = path;
            }
            if (sailed == null)
                return false;
            ids = sailed;
            return true;
        }

        if (start.Ocean)
            return false;

        var walk = WalkParents(start.Id, byId, byCoord);
        SailNode? embark = null;
        var best = int.MaxValue;
        foreach (var node in reach)
        {
            var hex = byId[node.Id];
            if (hex.Ocean || !walk.ContainsKey(node.Id))
                continue;
            var dist = WalkLength(walk, node.Id);
            if (!Better(dist, node.Id, best, embark?.Id))
                continue;
            best = dist;
            embark = node;
        }

        if (embark == null)
            return false;

        var walked = PathFromParents(walk, embark.Value.Id);
        var sail = Shortest(embark.Value, target, byId, byCoord, wind);
        if (sail == null)
            return false;
        if (sail.Count > 0 && walked.Count > 0 && sail[0] == walked[^1])
            walked.AddRange(sail.Skip(1));
        else
            walked.AddRange(sail);
        ids = walked;
        return true;
    }

    private readonly record struct SailNode(string Id, int Body);

    private static IEnumerable<SailNode> Bodies(RouteHex hex)
    {
        if (hex.Ocean)
        {
            yield return new SailNode(hex.Id, 0);
            yield break;
        }
        var seen = new HashSet<int>();
        for (var side = 0; side < 6; side++)
        {
            var body = hex.Body(side);
            if (body != 0 && seen.Add(body))
                yield return new SailNode(hex.Id, body);
        }
    }

    private static bool Better(int dist, string id, int best, string? embark) =>
        dist < best || (dist == best && (embark == null || string.Compare(id, embark, StringComparison.Ordinal) < 0));

    private static RoutePlan Finish(
        TravelMode mode,
        string destinationId,
        string endId,
        List<string> ids,
        Dictionary<string, RouteHex> byId,
        Dictionary<(int Q, int R), RouteHex> byCoord,
        IReadOnlyDictionary<string, WindAt>? wind,
        string status,
        bool walkRemainder)
    {
        var steps = new List<RouteStep>(ids.Count);
        double time = 0;
        for (var i = 0; i < ids.Count; i++)
        {
            var hex = byId[ids[i]];
            var enter = -1;
            var exit = -1;
            if (i > 0)
            {
                var prev = byId[ids[i - 1]];
                enter = Axial.SideTo(hex.Q, hex.R, prev.Q, prev.R);
                var side = Axial.SideTo(prev.Q, prev.R, hex.Q, hex.R);
                if (side >= 0 && prev.Ocean && hex.Ocean)
                {
                    WindAt? at = wind != null && wind.TryGetValue(prev.Id, out var found) ? found : null;
                    time += TravelCost.Ocean(Axial.SideBearing(side), at);
                }
                else
                    time += 1;
            }
            if (i + 1 < ids.Count)
            {
                var next = byId[ids[i + 1]];
                exit = Axial.SideTo(hex.Q, hex.R, next.Q, next.R);
            }
            steps.Add(new RouteStep(hex.Id, enter, exit));
        }

        return new RoutePlan
        {
            Found = true,
            Mode = mode,
            DestinationId = destinationId,
            EndId = endId,
            Steps = steps,
            Status = status,
            WalkRemainder = walkRemainder,
            Time = time,
        };
    }

    private static bool IsWater(RouteHex hex) => hex.Ocean || hex.Water != 0;

    private static HashSet<SailNode> Reach(
        string targetId,
        Dictionary<string, RouteHex> byId,
        Dictionary<(int Q, int R), RouteHex> byCoord,
        IReadOnlyDictionary<string, WindAt>? wind)
    {
        var seen = new HashSet<SailNode>();
        var queue = new Queue<SailNode>();
        var start = new SailNode(targetId, 0);
        seen.Add(start);
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            foreach (var (next, _, _) in SailLinks(node, byId, byCoord, wind))
            {
                if (seen.Add(next))
                    queue.Enqueue(next);
            }
        }
        return seen;
    }

    private static List<string>? Shortest(
        SailNode start,
        SailNode goal,
        Dictionary<string, RouteHex> byId,
        Dictionary<(int Q, int R), RouteHex> byCoord,
        IReadOnlyDictionary<string, WindAt>? wind)
    {
        if (start == goal)
            return [start.Id];

        var best = new Dictionary<SailNode, double> { [start] = 0 };
        var prev = new Dictionary<SailNode, SailNode>();
        var queue = new PriorityQueue<(SailNode Node, double G), double>();
        var goalHex = byId[goal.Id];
        queue.Enqueue((start, 0), 0);
        while (queue.Count > 0)
        {
            queue.TryDequeue(out var state, out _);
            if (state.G > best[state.Node] + 1e-9)
                continue;
            if (state.Node == goal)
                return HexPath(prev, start, goal);

            foreach (var (next, _, cost) in SailLinks(state.Node, byId, byCoord, wind))
            {
                var ng = state.G + cost;
                if (best.TryGetValue(next, out var old) && ng >= old - 1e-9)
                    continue;
                best[next] = ng;
                prev[next] = state.Node;
                var nhex = byId[next.Id];
                var h = Axial.Distance(nhex.Q, nhex.R, goalHex.Q, goalHex.R) * TravelCost.MinStep;
                queue.Enqueue((next, ng), ng + h);
            }
        }
        return null;
    }

    private static List<string> HexPath(Dictionary<SailNode, SailNode> prev, SailNode start, SailNode goal)
    {
        var nodes = new List<SailNode>();
        var cursor = goal;
        nodes.Add(cursor);
        while (cursor != start)
        {
            cursor = prev[cursor];
            nodes.Add(cursor);
        }
        nodes.Reverse();
        var ids = new List<string>(nodes.Count);
        foreach (var node in nodes)
        {
            if (ids.Count == 0 || ids[^1] != node.Id)
                ids.Add(node.Id);
        }
        return ids;
    }

    private static List<string>? Breadth(
        string startId,
        string goalId,
        Dictionary<string, RouteHex> byId,
        Dictionary<(int Q, int R), RouteHex> byCoord,
        bool sail,
        IReadOnlyDictionary<string, WindAt>? wind)
    {
        var parents = new Dictionary<string, string?>(StringComparer.Ordinal) { [startId] = null };
        var queue = new Queue<string>();
        queue.Enqueue(startId);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (id == goalId)
                return PathFromParents(parents, goalId);
            foreach (var (next, _, _) in Links(byId[id], byId, byCoord, sail, wind))
            {
                if (parents.ContainsKey(next))
                    continue;
                parents[next] = id;
                queue.Enqueue(next);
            }
        }
        return null;
    }

    private static Dictionary<string, string?> WalkParents(
        string startId,
        Dictionary<string, RouteHex> byId,
        Dictionary<(int Q, int R), RouteHex> byCoord)
    {
        var parents = new Dictionary<string, string?>(StringComparer.Ordinal) { [startId] = null };
        var queue = new Queue<string>();
        queue.Enqueue(startId);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var (next, _, _) in Links(byId[id], byId, byCoord, sail: false, wind: null))
            {
                if (parents.ContainsKey(next))
                    continue;
                parents[next] = id;
                queue.Enqueue(next);
            }
        }
        return parents;
    }

    private static int WalkLength(Dictionary<string, string?> parents, string id)
    {
        var count = 0;
        while (parents.TryGetValue(id, out var prev) && prev != null)
        {
            count++;
            id = prev;
        }
        return count;
    }

    private static List<string> PathFromParents(Dictionary<string, string?> parents, string goalId)
    {
        var ids = new List<string>();
        var cursor = goalId;
        while (true)
        {
            ids.Add(cursor);
            if (!parents.TryGetValue(cursor, out var prev) || prev == null)
                break;
            cursor = prev;
        }
        ids.Reverse();
        return ids;
    }

    private static List<string> PathFromPrev(Dictionary<string, string> prev, string startId, string goalId)
    {
        var ids = new List<string>();
        var cursor = goalId;
        ids.Add(cursor);
        while (cursor != startId)
        {
            cursor = prev[cursor];
            ids.Add(cursor);
        }
        ids.Reverse();
        return ids;
    }

    private static IEnumerable<(SailNode Next, int Side, double Cost)> SailLinks(
        SailNode node,
        Dictionary<string, RouteHex> byId,
        Dictionary<(int Q, int R), RouteHex> byCoord,
        IReadOnlyDictionary<string, WindAt>? wind)
    {
        var hex = byId[node.Id];
        for (var side = 0; side < 6; side++)
        {
            if (!hex.Ocean && hex.Body(side) != node.Body)
                continue;
            var (dq, dr) = Axial.NeighborDeltas[side];
            if (!byCoord.TryGetValue((hex.Q + dq, hex.R + dr), out var next))
                continue;
            if (!hex.Meets(side, next))
                continue;
            var body = next.Ocean ? 0 : next.Body(Axial.Opposite(side));
            double cost = 1;
            if (hex.Ocean && next.Ocean)
            {
                WindAt? at = wind != null && wind.TryGetValue(hex.Id, out var found) ? found : null;
                cost = TravelCost.Ocean(Axial.SideBearing(side), at);
            }
            yield return (new SailNode(next.Id, body), side, cost);
        }
    }

    private static IEnumerable<(string Next, int Side, double Cost)> Links(
        RouteHex hex,
        Dictionary<string, RouteHex> byId,
        Dictionary<(int Q, int R), RouteHex> byCoord,
        bool sail,
        IReadOnlyDictionary<string, WindAt>? wind)
    {
        for (var side = 0; side < 6; side++)
        {
            var (dq, dr) = Axial.NeighborDeltas[side];
            if (!byCoord.TryGetValue((hex.Q + dq, hex.R + dr), out var next))
                continue;
            if (sail || hex.Ocean || next.Ocean)
                continue;
            yield return (next.Id, side, 1);
        }
    }
}
