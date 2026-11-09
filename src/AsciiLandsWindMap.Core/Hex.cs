namespace AsciiLandsWindMap;

/// <summary>Axial hex coordinates, pointy top, +q east and +r southeast.</summary>
public static class Axial
{
    public static readonly (int Q, int R)[] NeighborDeltas =
    [
        (1, 0),
        (1, -1),
        (0, -1),
        (-1, 0),
        (-1, 1),
        (0, 1),
    ];

    public static IEnumerable<(int Q, int R)> Neighbors(int q, int r)
    {
        foreach (var (dq, dr) in NeighborDeltas)
            yield return (q + dq, r + dr);
    }

    public static (int Q, int R) Round(double q, double r)
    {
        var x = q;
        var z = r;
        var y = -x - z;
        var rx = (int)Math.Round(x);
        var ry = (int)Math.Round(y);
        var rz = (int)Math.Round(z);
        var dx = Math.Abs(rx - x);
        var dy = Math.Abs(ry - y);
        var dz = Math.Abs(rz - z);
        if (dx > dy && dx > dz)
            rx = -ry - rz;
        else if (dy > dz)
            ry = -rx - rz;
        else
            rz = -rx - ry;
        return (rx, rz);
    }
}

public readonly record struct HexLayout(double Size)
{
    public (double X, double Y) Center(int q, int r)
    {
        var x = Size * (Math.Sqrt(3) * q + Math.Sqrt(3) / 2 * r);
        var y = Size * (3.0 / 2 * r);
        return (x, y);
    }

    public (int Q, int R) AxialAt(double x, double y)
    {
        var q = (Math.Sqrt(3) / 3 * x - 1.0 / 3 * y) / Size;
        var r = (2.0 / 3 * y) / Size;
        return Axial.Round(q, r);
    }

    public (double X, double Y) Corner(int q, int r, int index)
    {
        var angle = Math.PI / 180.0 * (60 * index - 30);
        var (cx, cy) = Center(q, r);
        return (cx + Size * Math.Cos(angle), cy + Size * Math.Sin(angle));
    }
}
