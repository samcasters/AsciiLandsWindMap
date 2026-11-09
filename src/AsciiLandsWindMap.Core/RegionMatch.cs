using System.Text.RegularExpressions;

namespace AsciiLandsWindMap;

/// <summary>
/// Picks the region the game is showing. The on-screen title often repeats,
/// so a unique neighbor name from the minimap breaks the tie.
/// </summary>
public static partial class RegionMatch
{
    private static readonly (int Q, int R)[] Sides =
    [
        (1, 0), (1, -1), (0, -1), (-1, 0), (-1, 1), (0, 1),
    ];

    public static string BaseName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";
        var trimmed = name.Trim();
        var match = Suffix().Match(trimmed);
        return match.Success ? match.Groups[1].Value.Trim() : trimmed;
    }

    public static string? Find(IReadOnlyList<RegionRow> regions, string? title, IReadOnlyList<string>? nearby)
    {
        var wanted = BaseName(title);
        if (wanted.Length == 0)
            return null;

        var candidates = regions
            .Where(region => BaseName(region.Name).Equals(wanted, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count == 1)
            return candidates[0].Id;
        if (candidates.Count == 0)
            return null;

        var clues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in nearby ?? [])
        {
            var clue = BaseName(name);
            if (clue.Length > 1 && !clue.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                clues.Add(clue);
        }
        if (clues.Count == 0)
            return null;

        var byCoord = new Dictionary<(int Q, int R), RegionRow>();
        foreach (var region in regions)
            byCoord[(region.Q, region.R)] = region;

        var bestId = "";
        var best = 0;
        var tied = false;
        foreach (var candidate in candidates)
        {
            var score = 0;
            foreach (var (dq, dr) in Sides)
            {
                if (!byCoord.TryGetValue((candidate.Q + dq, candidate.R + dr), out var neighbor))
                    continue;
                if (clues.Contains(BaseName(neighbor.Name)))
                    score++;
            }
            if (score > best)
            {
                best = score;
                bestId = candidate.Id;
                tied = false;
            }
            else if (score == best && score > 0)
            {
                tied = true;
            }
        }

        return best > 0 && !tied ? bestId : null;
    }

    [GeneratedRegex(@"^(.*)\s+\([pm]\d+-[pm]\d+\)$", RegexOptions.IgnoreCase)]
    private static partial Regex Suffix();
}
