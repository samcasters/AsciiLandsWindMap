namespace AsciiLandsWindMap;

/// <summary>
/// Blue on a minimap chart counts as water. The numbers come from the play client's own colors:
/// the chart backing is #263c39, exit dots are #c8d5a1, buildings #e5d6a2, wildlife #efb68b,
/// and unexplored fog is rgb(24, 40, 54). None of those pass. Open-sea fill rgb(38, 76, 108) does.
/// </summary>
public static class WaterColor
{
    public static bool IsBlue(byte r, byte g, byte b, byte a)
    {
        if (a < 200 || b < 48)
            return false;
        var other = r > g ? r : g;
        return b > other && b - other >= 18;
    }
}

public static class WaterMask
{
    public const byte All = 0b111111;

    public static bool Has(byte mask, int side) => side >= 0 && side < 6 && (mask & (1 << side)) != 0;
}

/// <summary>
/// Water on one chart. Touch stores the blue body on each hex side (4 bits, 1-based).
/// Spans stores which part of that side the blue reaches (8 bits). Two tiles connect
/// only when the same stretch of blue meets across the shared side.
/// </summary>
public readonly record struct WaterProfile(byte Edges, ulong Touch, ulong Spans)
{
    public static WaterProfile Open { get; } = AllOpen();

    public static int Body(ulong touch, int side) => side is >= 0 and < 6 ? (int)((touch >> (side * 4)) & 0xF) : 0;

    public static int Bins(ulong spans, int side) => side is >= 0 and < 6 ? (int)((spans >> (side * 8)) & 0xFF) : 0;

    public static int ReverseBins(int bins)
    {
        var reversed = 0;
        for (var i = 0; i < 8; i++)
        {
            if ((bins & (1 << i)) != 0)
                reversed |= 1 << (7 - i);
        }
        return reversed;
    }

    private static WaterProfile AllOpen()
    {
        ulong touch = 0;
        ulong spans = 0;
        for (var side = 0; side < 6; side++)
        {
            touch |= 1UL << (side * 4);
            spans |= 0xFFUL << (side * 8);
        }
        return new WaterProfile(WaterMask.All, touch, spans);
    }
}

public static class WaterEdges
{
    public static WaterProfile FromBgra(int width, int height, ReadOnlySpan<byte> pixels, int stride)
    {
        if (width < 2 || height < 2 || stride < width * 4)
            return default;

        var count = width * height;
        var labels = new int[count];
        var blues = new List<int>();
        var cx = (width - 1) / 2.0;
        var cy = (height - 1) / 2.0;
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                if (i + 3 >= pixels.Length)
                    continue;
                if (!WaterColor.IsBlue(pixels[i + 2], pixels[i + 1], pixels[i], pixels[i + 3]))
                    continue;
                var index = y * width + x;
                labels[index] = -1;
                blues.Add(index);
            }
        }

        var next = 1;
        var queue = new Queue<int>();
        foreach (var start in blues)
        {
            if (labels[start] != -1)
                continue;
            if (next > 15)
                break;
            var id = next++;
            labels[start] = id;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var index = queue.Dequeue();
                var x = index % width;
                var y = index / width;
                TryEnqueue(labels, queue, width, height, x - 1, y, id);
                TryEnqueue(labels, queue, width, height, x + 1, y, id);
                TryEnqueue(labels, queue, width, height, x, y - 1, id);
                TryEnqueue(labels, queue, width, height, x, y + 1, id);
            }
        }

        var radius = Math.Min(width, height) / 2.0;
        var inner = radius * 0.90;
        var outer = radius * 0.995;
        Span<int> counts = stackalloc int[16 * 6];
        Span<int> bins = stackalloc int[16 * 6];
        foreach (var index in blues)
        {
            var id = labels[index];
            if (id <= 0)
                continue;
            var x = index % width;
            var y = index / width;
            var dx = x - cx;
            var dy = y - cy;
            var dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist < inner || dist > outer)
                continue;
            var side = SideOf(dx, dy);
            var (sx, sy) = Axial.SideVector(side);
            var along = (dx * -sy + dy * sx) / dist;
            var unit = along + 0.5;
            if (unit < 0 || unit >= 1)
                continue;
            var slot = id * 6 + side;
            counts[slot]++;
            bins[slot] |= 1 << (int)(unit * 8);
        }

        byte edges = 0;
        ulong touch = 0;
        ulong spans = 0;
        Span<int> best = stackalloc int[6];
        for (var id = 1; id < next; id++)
        {
            for (var side = 0; side < 6; side++)
            {
                var hit = counts[id * 6 + side];
                if (hit < 3 || hit <= best[side])
                    continue;
                best[side] = hit;
                edges |= (byte)(1 << side);
                touch &= ~(0xFUL << (side * 4));
                touch |= (ulong)id << (side * 4);
                spans &= ~(0xFFUL << (side * 8));
                spans |= (ulong)(bins[id * 6 + side] & 0xFF) << (side * 8);
            }
        }
        return new WaterProfile(edges, touch, spans);
    }

    private static void TryEnqueue(int[] labels, Queue<int> queue, int width, int height, int x, int y, int id)
    {
        if (x < 0 || y < 0 || x >= width || y >= height)
            return;
        var index = y * width + x;
        if (labels[index] != -1)
            return;
        labels[index] = id;
        queue.Enqueue(index);
    }

    public static int SideOf(double dx, double dy)
    {
        var best = 0;
        var bestDot = double.NegativeInfinity;
        for (var side = 0; side < 6; side++)
        {
            var (sx, sy) = Axial.SideVector(side);
            var dot = dx * sx + dy * sy;
            if (dot > bestDot)
            {
                bestDot = dot;
                best = side;
            }
        }
        return best;
    }
}
