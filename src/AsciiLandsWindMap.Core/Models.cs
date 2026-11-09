using System.Text.Json.Serialization;

namespace AsciiLandsWindMap;

public sealed class RegionRow
{
    public required string Id { get; init; }
    public int Q { get; init; }
    public int R { get; init; }
    public required string Name { get; init; }
    public required string Biome { get; init; }
    public string Note { get; set; } = "";
    public string Caption { get; set; } = "";
    public string Icon { get; set; } = "";
    public List<string> Tags { get; set; } = [];
}

public sealed class ObservationRow
{
    public long Id { get; init; }
    public required string RegionId { get; init; }
    public required string Realm { get; init; }
    public int GameMinute { get; init; }
    public double DirectionDeg { get; init; }
    public int Strength { get; init; }
    public required string RecordedUtc { get; init; }

    public ReadingLite ToLite() => new(GameMinute, DirectionDeg, Strength);
}

public readonly record struct ReadingLite(int GameMinute, double DirectionDeg, int Strength);

public sealed class MapData
{
    public required List<RegionRow> Regions { get; init; }
    public required Dictionary<string, List<ObservationRow>> Observations { get; init; }
    public Dictionary<string, ObservationRow> SyncWind { get; init; } = new(StringComparer.Ordinal);
}

public readonly record struct SyncResult(int Added, int Renamed, int Total, string? WorldId);

public sealed class MapSnapshot
{
    [JsonPropertyName("schema")]
    public int Schema { get; set; } = 1;

    [JsonPropertyName("app")]
    public string App { get; set; } = "AsciiLandsWindMap";

    [JsonPropertyName("realm")]
    public string Realm { get; set; } = "";

    [JsonPropertyName("world_id")]
    public string WorldId { get; set; } = "";

    [JsonPropertyName("exported_utc")]
    public string ExportedUtc { get; set; } = "";

    [JsonPropertyName("tiles")]
    public List<TileSnapshot> Tiles { get; set; } = [];
}

public sealed class TileSnapshot
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("note")]
    public string Note { get; set; } = "";

    [JsonPropertyName("caption")]
    public string Caption { get; set; } = "";

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = "";

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    [JsonPropertyName("observations")]
    public List<ReadingSnapshot> Observations { get; set; } = [];
}

public sealed class ReadingSnapshot
{
    [JsonPropertyName("game_minute")]
    public int GameMinute { get; set; }

    [JsonPropertyName("direction_deg")]
    public double DirectionDeg { get; set; }

    [JsonPropertyName("strength")]
    public int Strength { get; set; }

    [JsonPropertyName("recorded_utc")]
    public string RecordedUtc { get; set; } = "";

    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Source { get; set; } = "";
}

public sealed class ImportConflict
{
    public required string RegionId { get; init; }
    public required string DisplayName { get; init; }
    public required string LocalSummary { get; init; }
    public required string TheirSummary { get; init; }
    public required TileSnapshot Theirs { get; init; }
}

public sealed class ImportAnalysis
{
    public required List<TileSnapshot> Adds { get; init; }
    public required List<ImportConflict> Conflicts { get; init; }
    public int Unchanged { get; init; }
    public required List<string> UnknownIds { get; init; }
}

public readonly record struct ImportApplyResult(int Added, int Replaced, int Kept, int Unchanged, int Unknown);
