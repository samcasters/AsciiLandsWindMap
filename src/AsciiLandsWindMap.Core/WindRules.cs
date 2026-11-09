namespace AsciiLandsWindMap;

public enum WindWindow
{
    OntoLand,
    OutToSea,
    Turning,
}

public readonly record struct WindSample(double DirectionDeg, int Strength, int Count, bool Cancelled);

/// <summary>
/// Wind windows from the Asciilands sailing notes.
/// Onto land is 10:00 inclusive through 19:00 exclusive.
/// Out to sea is 23:00 inclusive through 09:00 exclusive.
/// The hours in between are turning, because the wiki does not name them.
/// </summary>
public static class WindRules
{
    public const int DayViewMinute = 14 * 60;
    public const int NightViewMinute = 2 * 60;

    public static int NormalizeMinute(int minute)
    {
        var mod = minute % 1440;
        return mod < 0 ? mod + 1440 : mod;
    }

    public static WindWindow WindowOf(int minute)
    {
        minute = NormalizeMinute(minute);
        if (minute >= 10 * 60 && minute < 19 * 60)
            return WindWindow.OntoLand;
        if (minute >= 23 * 60 || minute < 9 * 60)
            return WindWindow.OutToSea;
        return WindWindow.Turning;
    }

    public static string WindowLabel(WindWindow window) => window switch
    {
        WindWindow.OntoLand => "Onto land",
        WindWindow.OutToSea => "Out to sea",
        _ => "Turning",
    };

    public static string Phase(int minute)
    {
        minute = NormalizeMinute(minute);
        if (minute >= 5 * 60 && minute < 7 * 60)
            return "Dawn";
        if (minute >= 7 * 60 && minute < 17 * 60)
            return "Day";
        if (minute >= 17 * 60 && minute < 19 * 60)
            return "Dusk";
        return "Night";
    }

    public static bool IsDarkPhase(int minute)
    {
        var phase = Phase(minute);
        return phase is "Dusk" or "Night";
    }

    public static double ArrowLength(int strength, double hexSize) => strength switch
    {
        1 => hexSize * 0.34,
        2 => hexSize * 0.55,
        3 => hexSize * 0.78,
        _ => 0,
    };

    public static WindSample? Average(IReadOnlyList<ReadingLite> readings)
    {
        if (readings.Count == 0)
            return null;

        double east = 0;
        double north = 0;
        double strengthSum = 0;
        foreach (var reading in readings)
        {
            var strength = Math.Clamp(reading.Strength, 0, 3);
            strengthSum += strength;
            if (strength == 0)
                continue;
            var rad = CompassText.Normalize(reading.DirectionDeg) * Math.PI / 180.0;
            east += Math.Sin(rad) * strength;
            north += Math.Cos(rad) * strength;
        }

        var strengthAvg = (int)Math.Round(strengthSum / readings.Count, MidpointRounding.AwayFromZero);
        strengthAvg = Math.Clamp(strengthAvg, 0, 3);
        var magnitude = Math.Sqrt(east * east + north * north);
        if (strengthAvg == 0 || magnitude < 0.001)
        {
            var cancelled = strengthSum > 0 && magnitude < 0.001;
            return new WindSample(0, cancelled ? 0 : strengthAvg, readings.Count, cancelled);
        }

        var degrees = Math.Atan2(east, north) * 180.0 / Math.PI;
        return new WindSample(CompassText.Normalize(degrees), strengthAvg, readings.Count, false);
    }
}

public static class CompassText
{
    private static readonly string[] Points = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];

    public static double Normalize(double degrees)
    {
        var mod = degrees % 360.0;
        return mod < 0 ? mod + 360.0 : mod;
    }

    public static string Point(double degrees)
    {
        var index = (int)Math.Round(Normalize(degrees) / 45.0, MidpointRounding.AwayFromZero) % 8;
        return Points[index];
    }

    public static string Mark(int strength) => strength switch
    {
        1 => "~",
        2 => "≈",
        3 => "≋",
        _ => "calm",
    };

    public static string StrengthWord(int strength) => strength switch
    {
        1 => "light",
        2 => "fresh",
        3 => "strong",
        _ => "calm",
    };

    public static string Going(double degrees)
    {
        var shown = (int)Math.Round(Normalize(degrees), MidpointRounding.AwayFromZero) % 360;
        return $"toward {Point(degrees)} ({shown}°)";
    }

    public static string BlowsFrom(double goingDegrees) =>
        $"from {Point(Normalize(goingDegrees + 180))}";
}

public static class TileTags
{
    public static readonly (string Id, string Label)[] BuiltIn =
    [
        ("deep_water", "Deep water"),
        ("shallow", "Shallow"),
        ("stormy", "Stormy"),
        ("river_lake", "River or lake"),
        ("gale_edge", "Gale edge"),
        ("favorite", "Favorite"),
    ];

    public static bool IsBuiltIn(string tag) =>
        BuiltIn.Any(item => string.Equals(item.Id, tag, StringComparison.OrdinalIgnoreCase));

    public static string? Canonical(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;
        var trimmed = tag.Trim();
        foreach (var built in BuiltIn)
        {
            if (string.Equals(built.Id, trimmed, StringComparison.OrdinalIgnoreCase))
                return built.Id;
        }
        if (trimmed.Length > 40)
            trimmed = trimmed[..40];
        return trimmed;
    }

    public static List<string> CanonicalSet(IEnumerable<string> tags)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tags)
        {
            var canonical = Canonical(tag);
            if (canonical == null || !seen.Add(canonical))
                continue;
            list.Add(canonical);
            if (list.Count == 24)
                break;
        }
        return list;
    }
}

public static class BiomeNames
{
    public static string Pretty(string? biome)
    {
        if (string.IsNullOrWhiteSpace(biome))
            return "Unknown";
        var parts = biome.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts.Select(Title));
    }

    private static string Title(string part) =>
        part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..];
}

public static class TileText
{
    public static string Summary(string note, IReadOnlyList<string> tags, IReadOnlyList<ReadingLite> readings)
    {
        var parts = new List<string>();
        if (readings.Count == 0)
            parts.Add("no wind readings");
        else
        {
            var shown = readings.OrderBy(reading => reading.GameMinute).Take(6).Select(FormatReading);
            var text = string.Join(", ", shown);
            if (readings.Count > 6)
                text += $", +{readings.Count - 6} more";
            parts.Add(text);
        }
        if (tags.Count > 0)
            parts.Add("tags: " + string.Join(", ", tags));
        if (!string.IsNullOrWhiteSpace(note))
            parts.Add("has a note");
        return string.Join(" · ", parts);
    }

    public static string FormatReading(ReadingLite reading)
    {
        var time = GameClock.Format(reading.GameMinute);
        if (reading.Strength <= 0)
            return $"{time} calm";
        return $"{time} {CompassText.Mark(reading.Strength)} {CompassText.Going(reading.DirectionDeg)}";
    }
}
