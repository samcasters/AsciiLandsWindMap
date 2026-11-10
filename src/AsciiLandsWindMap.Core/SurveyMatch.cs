namespace AsciiLandsWindMap;

public static class SurveyMatch
{
    public static bool TryMatch(IReadOnlyList<RegionRow> regions, string gameId, string gameName, out string regionId, out string? problem)
    {
        regionId = "";
        problem = null;
        if (regions.Any(region => region.Id == gameId))
        {
            regionId = gameId;
            return true;
        }

        var name = gameName.Trim();
        if (name.Length == 0)
        {
            problem = gameId.Length == 0 ? "A visited tile had no name." : gameId;
            return false;
        }

        var named = regions.Where(region => region.Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (named.Count == 1)
        {
            regionId = named[0].Id;
            return true;
        }

        problem = named.Count == 0 ? name : $"{name} matches {named.Count} tiles";
        return false;
    }
}
