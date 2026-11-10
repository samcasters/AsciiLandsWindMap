using System.Text.Json;
using System.Text.Json.Serialization;

namespace AsciiLandsWindMap;

public sealed class GraphNode
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("coord")]
    public int[]? Coord { get; set; }

    [JsonPropertyName("open_sea")]
    public bool OpenSea { get; set; }

    [JsonPropertyName("map_rel")]
    public string? MapRel { get; set; }
}

public static class WorldGraph
{
    public const string Url = "https://asciilands.io/play/world_graph.v1.json";

    public static async Task<List<GraphNode>> DownloadAsync(HttpClient http, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(Url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return Read(stream);
    }

    public static List<GraphNode> Read(Stream stream)
    {
        var file = JsonSerializer.Deserialize<GraphFile>(stream, WorldClient.JsonOptions);
        if (file?.Nodes == null || file.Nodes.Count == 0)
            throw new InvalidDataException("The world graph had no regions.");
        return file.Nodes;
    }

    public static string MinimapUrl(string? mapRel)
    {
        if (string.IsNullOrWhiteSpace(mapRel))
            return "";
        var rel = mapRel.Trim().Replace('\\', '/');
        if (rel.EndsWith(".a3d", StringComparison.OrdinalIgnoreCase))
            rel = string.Concat(rel.AsSpan(0, rel.Length - 4), ".minimap.png");
        if (rel.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return rel;
        return "https://asciilands.io/play/" + rel.TrimStart('/');
    }

    private sealed class GraphFile
    {
        [JsonPropertyName("nodes")]
        public List<GraphNode>? Nodes { get; set; }
    }
}
