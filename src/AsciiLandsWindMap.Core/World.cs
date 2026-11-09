using System.Text.Json;
using System.Text.Json.Serialization;

namespace AsciiLandsWindMap;

public sealed class WorldFile
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("world_id")]
    public string? WorldId { get; set; }

    [JsonPropertyName("topology")]
    public string? Topology { get; set; }

    [JsonPropertyName("nodes")]
    public List<WorldNode> Nodes { get; set; } = [];
}

public sealed class WorldNode
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("biome")]
    public string Biome { get; set; } = "";

    [JsonPropertyName("coord")]
    public int[]? Coord { get; set; }
}

public static class WorldNames
{
    public static string Display(string? rawName, string id, bool duplicate)
    {
        var name = string.IsNullOrWhiteSpace(rawName) ? "Unknown" : rawName.Trim();
        if (!duplicate)
            return name;
        var slug = id.StartsWith("explore-", StringComparison.Ordinal) ? id[8..] : id;
        return $"{name} ({slug})";
    }

    public static bool IsPlaceholder(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0 || trimmed == "?")
            return true;
        if (trimmed.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            return true;
        if (trimmed.StartsWith("Unknown (", StringComparison.OrdinalIgnoreCase))
            return true;
        return trimmed.StartsWith("? (", StringComparison.Ordinal);
    }

    public static string Slug(string id) =>
        id.StartsWith("explore-", StringComparison.Ordinal) ? id[8..] : id;
}

public static class WorldClient
{
    public const string MapUrl = "https://wiki.asciilands.io/world.json";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<WorldFile> DownloadAsync(HttpClient http, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(MapUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var file = await JsonSerializer.DeserializeAsync<WorldFile>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        if (file?.Nodes == null || file.Nodes.Count == 0)
            throw new InvalidDataException("The wiki map file had no regions.");
        return file;
    }
}

public static class RegionSearch
{
    public static List<RegionRow> Search(IEnumerable<RegionRow> regions, string? query, int limit = 20)
    {
        var result = new List<RegionRow>();
        if (string.IsNullOrWhiteSpace(query))
            return result;
        var text = query.Trim();
        var starts = new List<RegionRow>();
        var contains = new List<RegionRow>();
        foreach (var region in regions)
        {
            if (region.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase)
                || region.Id.Contains(text, StringComparison.OrdinalIgnoreCase))
                starts.Add(region);
            else if (region.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
                contains.Add(region);
        }
        starts.Sort(Compare);
        contains.Sort(Compare);
        foreach (var region in starts)
        {
            if (result.Count == limit)
                return result;
            result.Add(region);
        }
        foreach (var region in contains)
        {
            if (result.Count == limit)
                break;
            result.Add(region);
        }
        return result;
    }

    private static int Compare(RegionRow a, RegionRow b) =>
        string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
}
