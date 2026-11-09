using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AsciiLandsWindMap;

public readonly record struct ServerClock(
    double ServerTimeMs,
    double EpochMs,
    double CycleMs,
    double DayFraction,
    int Version,
    DateTime ReadUtc);

public readonly record struct SeaWindArrow(double DirectionDeg, int Strength);

/// <summary>
/// Sea wind from the Asciilands client (browserlands/sailing.js and visual-environment.js).
/// Direction stored here is where the wind is going: 0 is north, clockwise.
/// The in-game compass shows the opposite bearing.
/// </summary>
public static class SeaWind
{
    public const double GaleFloor = 0.95;

    private static readonly (int Q, int R)[] Sides =
    [
        (1, 0), (1, -1), (0, -1), (-1, 0), (-1, 1), (0, 1),
    ];

    private static readonly HashSet<string> DryBiomes = new(StringComparer.OrdinalIgnoreCase)
    {
        "desert", "hot_sand_desert", "red_rock_badlands", "salt_flat", "volcanic", "volcanic_wastes", "oasis", "thorn_scrub",
    };

    private static readonly HashSet<string> SnowBiomes = new(StringComparer.OrdinalIgnoreCase)
    {
        "tundra", "snow_alpine", "taiga", "boreal_taiga",
    };

    private static readonly (double Edge, string Kind)[] WeatherSteps =
    [
        (0.885, "storm"),
        (0.75, "rain"),
        (0.55, "cloudy"),
    ];

    public static int Mark(double strength)
    {
        if (strength > 0.75)
            return 3;
        if (strength > 0.45)
            return 2;
        return 1;
    }

    public static double GoingDegrees(double gameDirection)
    {
        var mirrored = 360.0 - CompassText.Normalize(gameDirection);
        return CompassText.Normalize(mirrored);
    }

    public static double? Hour(ServerClock clock, DateTime utcNow)
    {
        var elapsed = Math.Max(0, (utcNow - clock.ReadUtc).TotalMilliseconds);
        if (clock.CycleMs is < 300_000 or > 86_400_000 || !IsFinite(clock.ServerTimeMs) || !IsFinite(clock.EpochMs))
            return null;
        var phase = Mod(clock.ServerTimeMs + elapsed - clock.EpochMs, clock.CycleMs) / clock.CycleMs;
        if (clock.Version != 2)
            return phase * 24.0;
        var day = Math.Clamp(clock.DayFraction == 0 ? 2.0 / 3.0 : clock.DayFraction, 0.1, 0.9);
        var hour = phase < day
            ? 6.0 + phase / day * 12.0
            : 18.0 + (phase - day) / (1.0 - day) * 12.0;
        return Mod(hour, 24.0);
    }

    public static int MinuteOf(double hour) => (int)Math.Floor(Mod(hour, 24.0) * 60.0) % 1440;

    public static double Breeze(double hour)
    {
        hour = Mod(hour, 24.0);
        if (hour >= 10 && hour < 19)
            return 0.45 * Math.Sin(Math.PI * (hour - 10) / 9.0);
        var night = Mod(hour - 23.0, 24.0);
        return night < 10 ? -0.4 * Math.Sin(Math.PI * night / 10.0) : 0;
    }

    public static Dictionary<string, SeaWindArrow> Oceans(
        IReadOnlyList<RegionRow> regions,
        double worldMs,
        double hour,
        string? playerZoneId = null,
        bool playerEdge = false,
        string? forcedWeather = null)
    {
        var result = new Dictionary<string, SeaWindArrow>(StringComparer.Ordinal);
        SeaField.Build(regions).Fill(result, worldMs, hour, playerZoneId, playerEdge, forcedWeather);
        return result;
    }

    /// <summary>
    /// Coast, lane center, and per-hex seeds. Those do not change with the clock,
    /// so a live update only reruns the time-varying part.
    /// </summary>
    public sealed class SeaField
    {
        private readonly Prepared[] _places;

        private SeaField(Prepared[] places) => _places = places;

        public static SeaField Build(IReadOnlyList<RegionRow> regions)
        {
            var byCoord = new Dictionary<(int Q, int R), RegionRow>(regions.Count);
            foreach (var region in regions)
                byCoord[(region.Q, region.R)] = region;
            var places = new List<Prepared>();
            foreach (var region in regions)
            {
                if (!region.Biome.Equals("ocean", StringComparison.OrdinalIgnoreCase))
                    continue;
                places.Add(Prepare(region, byCoord));
            }
            return new SeaField(places.ToArray());
        }

        public void Fill(
            Dictionary<string, SeaWindArrow> into,
            double worldMs,
            double hour,
            string? playerZoneId,
            bool playerEdge,
            string? forcedWeather)
        {
            into.Clear();
            for (var i = 0; i < _places.Length; i++)
            {
                ref readonly var place = ref _places[i];
                var here = playerZoneId != null && place.Id.Equals(playerZoneId, StringComparison.Ordinal);
                var raw = Evaluate(place, worldMs, hour, here ? playerEdge : place.Edge, here ? forcedWeather : null);
                into[place.Id] = new SeaWindArrow(GoingDegrees(raw.Direction), Mark(raw.Strength));
            }
        }
    }

    public static (double Direction, double Strength) WindAt(
        RegionRow region,
        IReadOnlyDictionary<(int Q, int R), RegionRow> byCoord,
        double worldMs,
        double hour,
        bool edge,
        string? forcedWeather)
    {
        var prepared = Prepare(region, byCoord);
        return Evaluate(prepared, worldMs, hour, edge, forcedWeather);
    }

    private readonly struct Prepared
    {
        public string Id { get; init; }
        public string Biome { get; init; }
        public double X { get; init; }
        public double Y { get; init; }
        public double Lx { get; init; }
        public double Ly { get; init; }
        public double Reach { get; init; }
        public double Coast { get; init; }
        public double Dx { get; init; }
        public double Dy { get; init; }
        public double Radius { get; init; }
        public double Angle { get; init; }
        public double First { get; init; }
        public double Second { get; init; }
        public bool Cold { get; init; }
        public bool Edge { get; init; }
    }

    private static Prepared Prepare(RegionRow region, IReadOnlyDictionary<(int Q, int R), RegionRow> byCoord)
    {
        var (x, y) = HexCenter(region.Q, region.R);
        double lx = 0;
        double ly = 0;
        var land = 0;
        var cold = SnowBiomes.Contains(region.Biome);
        foreach (var (dq, dr) in Sides)
        {
            if (!byCoord.TryGetValue((region.Q + dq, region.R + dr), out var near))
                continue;
            cold = cold || SnowBiomes.Contains(near.Biome);
            if (near.Biome.Equals("ocean", StringComparison.OrdinalIgnoreCase))
                continue;
            var (nx, ny) = HexCenter(region.Q + dq, region.R + dr);
            lx += nx - x;
            ly += ny - y;
            land += 1;
        }

        var seed = Fnv1a(region.Id);
        const double tau = Math.PI * 2;
        const double centerX = 16.0;
        const double centerY = 9.1;
        var dx = x - centerX;
        var dy = y - centerY;
        var radius = Hypot(dx, dy);
        if (radius == 0)
            radius = 1;
        return new Prepared
        {
            Id = region.Id,
            Biome = region.Biome,
            X = x,
            Y = y,
            Lx = lx,
            Ly = ly,
            Reach = Hypot(lx, ly),
            Coast = Math.Min(1.0, land / 2.0),
            Dx = dx,
            Dy = dy,
            Radius = radius,
            Angle = Math.Atan2(dy, dx),
            First = (seed >> 9) % 1000 / 1000.0 * tau,
            Second = (seed >> 19) % 1000 / 1000.0 * tau,
            Cold = cold,
            Edge = NeighborCount(byCoord, region.Q, region.R) < 6,
        };
    }

    private static (double Direction, double Strength) Evaluate(
        in Prepared place,
        double worldMs,
        double hour,
        bool edge,
        string? forcedWeather)
    {
        var kind = WeatherKind(place.X, place.Y, place.Biome, place.Cold, place.Coast, worldMs, forcedWeather);
        var storm = kind == "storm";
        var gust = storm ? 2.0 : 1.0;
        const double tau = Math.PI * 2;
        var t = worldMs / 1000.0;
        var swing = tau * Mod(worldMs, 3 * 86_400_000.0) / (3 * 86_400_000.0);
        var lane = 0.62 + 0.3 * Math.Cos(place.Angle - swing) + 0.06 * gust * Math.Sin(tau * t / 1260.0 + place.First);
        var keep = lane * (1 - 0.6 * place.Coast);
        var vx = place.Dy / place.Radius * keep;
        var vy = -place.Dx / place.Radius * keep;
        if (place.Reach > 0.01)
        {
            var push = Breeze(hour) * place.Coast;
            vx += place.Lx / place.Reach * push;
            vy += place.Ly / place.Reach * push;
        }

        var strength = Hypot(vx, vy) * (storm ? 1.25 : 1);
        if (edge)
            strength = Math.Max(strength, GaleFloor);
        var direction = Mod(Math.Atan2(-vx, vy) * 180.0 / Math.PI + 8 * gust * Math.Sin(tau * t / 2820.0 + place.Second), 360.0);
        strength = Math.Max(0.12, Math.Min(1, strength));
        return (direction, strength);
    }

    private static int NeighborCount(IReadOnlyDictionary<(int Q, int R), RegionRow> byCoord, int q, int r)
    {
        var count = 0;
        foreach (var (dq, dr) in Sides)
        {
            if (byCoord.ContainsKey((q + dq, r + dr)))
                count++;
        }
        return count;
    }

    private static string WeatherKind(double x, double y, string biome, bool cold, double coast, double worldMs, string? forced)
    {
        if (forced is "clear" or "cloudy" or "rain" or "snow" or "storm")
            return forced;
        var dx = x - 16.0;
        var dy = y - 9.1;
        var angle = Math.Atan2(dy, dx);
        var radius = Hypot(dx, dy);
        var wet = 0.5
            + 0.26 * Math.Cos(3 * angle + Turn(worldMs, 7_200_000))
            + 0.16 * Math.Cos(5 * angle + Turn(worldMs, 11_100_000) + 0.9 * radius)
            + 0.12 * Math.Cos(angle - Turn(worldMs, 3 * 86_400_000.0));
        wet = Math.Max(0, Math.Min(1, wet));
        var kind = "clear";
        foreach (var (edge, name) in WeatherSteps)
        {
            if (wet >= edge)
            {
                kind = name;
                break;
            }
        }
        if (kind == "storm" && coast >= 1)
            kind = "rain";
        if ((kind == "rain" || kind == "storm") && DryBiomes.Contains(biome))
            kind = "cloudy";
        if (kind == "rain" && cold)
            kind = "snow";
        return kind;
    }

    public static (double X, double Y) HexCenter(int q, int r) => (q + r / 2.0, -r * Math.Sqrt(3) / 2.0);

    private static double Turn(double ms, double period) => Math.PI * 2 * Mod(ms, period) / period;

    private static uint Fnv1a(string text)
    {
        uint hash = 2166136261;
        foreach (var bite in Encoding.UTF8.GetBytes(text))
            hash = (hash ^ bite) * 16777619;
        return hash;
    }

    private static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);

    private static double Mod(double value, double period)
    {
        var remainder = value % period;
        return remainder < 0 ? remainder + period : remainder;
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}

public static class SyncPace
{
    public const int MinimumSeconds = 1;
    public const int MaximumSeconds = 600;

    public static int Clamp(int seconds) => Math.Clamp(seconds, MinimumSeconds, MaximumSeconds);

    public static int ParseOrClamp(string? text, int fallback = 5)
    {
        if (!int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            return Clamp(fallback);
        return Clamp(seconds);
    }
}

public static partial class GameHud
{
    [GeneratedRegex(@"(\d{1,2})\s*:\s*(\d{2})")]
    private static partial Regex ClockRegex();

    [GeneratedRegex(@"explore-[pm]\d+-[pm]\d+", RegexOptions.IgnoreCase)]
    private static partial Regex ZoneRegex();

    public static int? ClockMinute(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var match = ClockRegex().Match(text);
        if (!match.Success)
            return null;
        var hour = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minute = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        if (hour is < 0 or > 23 || minute is < 0 or > 59)
            return null;
        return hour * 60 + minute;
    }

    public static string? ZoneId(params string?[] texts)
    {
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text))
                continue;
            var match = ZoneRegex().Match(text);
            if (match.Success)
                return match.Value.ToLowerInvariant();
        }
        return null;
    }

    public static bool TryWindFrom(string? label, string? glyph, out double goingDegrees, out int strength)
    {
        goingDegrees = 0;
        strength = 0;
        if (string.IsNullOrWhiteSpace(label))
            return false;
        var from = label.Trim();
        const string prefix = "Wind from ";
        if (from.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            from = from[prefix.Length..].Trim();
        var index = Array.FindIndex(Points, point => point.Equals(from, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return false;
        goingDegrees = CompassText.Normalize(index * 45.0 + 180.0);
        strength = glyph switch
        {
            "≋" => 3,
            "≈" => 2,
            "~" => 1,
            _ => 1,
        };
        return true;
    }

    private static readonly string[] Points = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
}
