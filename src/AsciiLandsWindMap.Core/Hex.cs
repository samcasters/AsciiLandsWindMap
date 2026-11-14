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

    public static int Distance(int q1, int r1, int q2, int r2)
    {
        var dq = q1 - q2;
        var dr = r1 - r2;
        var ds = (-q1 - r1) - (-q2 - r2);
        return (Math.Abs(dq) + Math.Abs(dr) + Math.Abs(ds)) / 2;
    }

    /// <summary>
    /// Outward unit vector of a flat side, in the same screen space as <see cref="HexLayout"/> (y grows downward).
    /// Side 0 is east, then northeast, northwest, west, southwest, southeast.
    /// </summary>
    public static (double X, double Y) SideVector(int side)
    {
        var index = ((side % 6) + 6) % 6;
        var geo = index == 0 ? 0 : (6 - index) % 6;
        var angle = Math.PI / 180.0 * (60 * geo);
        return (Math.Cos(angle), Math.Sin(angle));
    }

    /// <summary>Compass degrees of travel across a side. 0 is north and degrees grow clockwise, matching sea wind.</summary>
    public static double SideBearing(int side)
    {
        var (x, y) = SideVector(side);
        var degrees = Math.Atan2(x, -y) * 180.0 / Math.PI;
        var mod = degrees % 360.0;
        return mod < 0 ? mod + 360.0 : mod;
    }

    public static int SideTo(int q, int r, int nq, int nr)
    {
        var dq = nq - q;
        var dr = nr - r;
        for (var side = 0; side < NeighborDeltas.Length; side++)
        {
            if (NeighborDeltas[side].Q == dq && NeighborDeltas[side].R == dr)
                return side;
        }
        return -1;
    }

    public static int Opposite(int side) => (side + 3) % 6;

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

    /// <summary>World half-extent covered by a minimap picture. Play bounds are ±1024.</summary>
    public const double PicturePlayHalf = 1024;

    /// <summary>World distance from a hex center to a flat side inside that picture.</summary>
    public const double PictureSeam = 736;

    /// <summary>
    /// Side of the square a minimap is drawn into before the hex clips it.
    /// The seam then sits on the edge shared with the next hex.
    /// </summary>
    public double PictureSide() => Size * Math.Sqrt(3) * (PicturePlayHalf / PictureSeam);
}
