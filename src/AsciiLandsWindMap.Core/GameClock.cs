namespace AsciiLandsWindMap;

public readonly record struct ClockSettings(DateTime? AnchorUtc, int? AnchorMinute, double? DayLengthMinutes);

public static class GameClock
{
    public static int? CurrentMinute(DateTime utcNow, DateTime? anchorUtc, int? anchorMinute, double? dayLengthMinutes)
    {
        if (anchorMinute is not int anchor)
            return null;
        anchor = WindRules.NormalizeMinute(anchor);
        if (anchorUtc is not DateTime anchorTime || dayLengthMinutes is not double length || length <= 0)
            return anchor;

        var elapsedMinutes = (utcNow.ToUniversalTime() - anchorTime.ToUniversalTime()).TotalMinutes;
        var gameElapsed = elapsedMinutes / length * 1440.0;
        return WindRules.NormalizeMinute((int)Math.Floor(anchor + gameElapsed));
    }

    public static string Format(int minute)
    {
        minute = WindRules.NormalizeMinute(minute);
        return $"{minute / 60:00}:{minute % 60:00}";
    }

    public static bool TryParseTime(string? text, out int minute)
    {
        minute = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var trimmed = text.Trim();
        var split = trimmed.Split(':', '.');
        if (split.Length == 2
            && int.TryParse(split[0], out var hour)
            && int.TryParse(split[1], out var mins))
            return TryBuild(hour, mins, out minute);

        if (split.Length == 1 && int.TryParse(trimmed, out var packed))
        {
            if (trimmed.Length <= 2)
                return TryBuild(packed, 0, out minute);
            if (trimmed.Length == 3)
                return TryBuild(packed / 10, packed % 10, out minute) || TryBuild(packed / 100, packed % 100, out minute);
            if (trimmed.Length == 4)
                return TryBuild(packed / 100, packed % 100, out minute);
        }
        return false;
    }

    public static bool TryParseDayLength(string? text, out double? minutes)
    {
        minutes = null;
        if (string.IsNullOrWhiteSpace(text))
            return true;
        if (!double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
            return false;
        if (value <= 0 || value > 1440)
            return false;
        minutes = value;
        return true;
    }

    private static bool TryBuild(int hour, int minute, out int result)
    {
        result = 0;
        if (hour is < 0 or > 23 || minute is < 0 or > 59)
            return false;
        result = hour * 60 + minute;
        return true;
    }
}

public static class Realms
{
    public static readonly (string Id, string Label)[] All =
    [
        ("eu", "Europe"),
        ("na", "North America"),
        ("ap", "Asia Pacific"),
    ];

    public static string Label(string? id) => id switch
    {
        "eu" => "Europe",
        "na" => "North America",
        "ap" => "Asia Pacific",
        _ => string.IsNullOrWhiteSpace(id) ? "Unknown" : id,
    };

    public static bool IsKnown(string? id) => id is "eu" or "na" or "ap";
}
