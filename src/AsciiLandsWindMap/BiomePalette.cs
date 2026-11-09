using System.Windows.Media;

namespace AsciiLandsWindMap;

public static class BiomePalette
{
    private static readonly Dictionary<string, Color> Colors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ocean"] = Rgb(27, 79, 114),
        ["forest"] = Rgb(46, 125, 79),
        ["plains"] = Rgb(184, 168, 84),
        ["highlands"] = Rgb(125, 107, 88),
        ["limestone_karst"] = Rgb(142, 154, 175),
        ["bamboo_forest"] = Rgb(62, 140, 90),
        ["cypress_swamp"] = Rgb(62, 92, 74),
        ["hot_sand_desert"] = Rgb(214, 170, 84),
        ["desert"] = Rgb(214, 170, 84),
        ["savanna"] = Rgb(186, 154, 78),
        ["snow_alpine"] = Rgb(214, 224, 230),
        ["snowy_alpine"] = Rgb(214, 224, 230),
        ["volcanic_wastes"] = Rgb(122, 59, 50),
        ["volcanic"] = Rgb(122, 59, 50),
        ["tropical_coast"] = Rgb(42, 150, 140),
        ["mangrove_swamp"] = Rgb(30, 77, 58),
        ["mangrove"] = Rgb(30, 77, 58),
        ["tundra"] = Rgb(176, 190, 196),
        ["boreal_taiga"] = Rgb(61, 92, 73),
        ["boreal"] = Rgb(61, 92, 73),
        ["red_rock_badlands"] = Rgb(184, 112, 74),
        ["red_rock"] = Rgb(184, 112, 74),
        ["salt_flat"] = Rgb(214, 208, 196),
        ["thorn_scrub"] = Rgb(156, 122, 86),
        ["oasis"] = Rgb(70, 148, 102),
        ["tropical_rainforest"] = Rgb(27, 67, 50),
        ["rainforest"] = Rgb(27, 67, 50),
        ["unknown"] = Rgb(120, 114, 104),
        ["unknown_land"] = Rgb(120, 114, 104),
    };

    private static readonly Dictionary<(string Biome, bool Night), SolidColorBrush> Brushes = new();

    public static SolidColorBrush BrushFor(string? biome, bool night)
    {
        var name = string.IsNullOrWhiteSpace(biome) ? "unknown" : biome;
        var key = (name.ToLowerInvariant(), night);
        if (Brushes.TryGetValue(key, out var cached))
            return cached;
        var color = ColorFor(name);
        if (night)
            color = Darken(color);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Brushes[key] = brush;
        return brush;
    }

    private static Color ColorFor(string biome)
    {
        if (Colors.TryGetValue(biome, out var color))
            return color;
        unchecked
        {
            var hash = 17;
            foreach (var ch in biome)
                hash = hash * 31 + ch;
            return Rgb(
                (byte)(90 + (hash & 0x5F)),
                (byte)(90 + ((hash >> 8) & 0x5F)),
                (byte)(80 + ((hash >> 16) & 0x5F)));
        }
    }

    private static Color Darken(Color color)
    {
        const double keep = 0.58;
        byte Mix(byte value) => (byte)(value * keep + 16 * (1 - keep));
        return Rgb(Mix(color.R), Mix(color.G), Mix(color.B));
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
}
