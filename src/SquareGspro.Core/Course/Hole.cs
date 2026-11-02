namespace SquareGspro.Core.Course;

/// <summary>
/// What the ball is sitting on. <see cref="Fringe"/> is the collar around a green; <see cref="Rough"/> is the first
/// cut beside the fairways, tees and greens, and <see cref="DeepRough"/> everything further out.
/// </summary>
public enum Lie { Tee, Fairway, Fringe, Rough, DeepRough, Green, Bunker, Water, OutOfBounds }

/// <summary>A ground point in metres, in the same frame as <see cref="FlightPoint"/>: X forward from the tee, Z right.</summary>
public readonly record struct P2(double X, double Z)
{
    public static P2 operator +(P2 a, P2 b) => new(a.X + b.X, a.Z + b.Z);
    public static P2 operator -(P2 a, P2 b) => new(a.X - b.X, a.Z - b.Z);
    public static P2 operator *(P2 a, double k) => new(a.X * k, a.Z * k);
    public double Length => Math.Sqrt(X * X + Z * Z);
    public double DistanceTo(P2 o) => (this - o).Length;
}

public abstract record Shape
{
    public abstract bool Contains(P2 p);
    /// <summary>Approximate distance from the shape's edge (0 or negative inside).</summary>
    public abstract double DistanceTo(P2 p);
    /// <summary>Axis-aligned box around the shape (for quick rejection).</summary>
    public abstract Bounds Box { get; }

    /// <summary>True if <paramref name="p"/> is inside the shape or within <paramref name="margin"/> of it.</summary>
    public bool IsNear(P2 p, double margin) => Box.Expand(margin).Contains(p) && DistanceTo(p) <= margin;
}

/// <summary>A (rotated) ellipse, used for greens, bunkers and ponds.</summary>
public sealed record Ellipse(P2 Center, double RadiusX, double RadiusZ, double RotationDeg = 0) : Shape
{
    public override bool Contains(P2 p)
    {
        var (u, v) = Local(p);
        return (u * u) / (RadiusX * RadiusX) + (v * v) / (RadiusZ * RadiusZ) <= 1;
    }

    public override double DistanceTo(P2 p) => p.DistanceTo(Center) - Math.Max(RadiusX, RadiusZ);

    public override Bounds Box => Bounds.Around(Center, Math.Max(RadiusX, RadiusZ));

    /// <summary>Points around the edge, for rendering.</summary>
    public IReadOnlyList<P2> Outline(int segments = 40)
    {
        double r = RotationDeg * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
        var pts = new P2[segments];
        for (int i = 0; i < segments; i++)
        {
            double a = 2 * Math.PI * i / segments, u = Math.Cos(a) * RadiusX, v = Math.Sin(a) * RadiusZ;
            pts[i] = Center + new P2(u * c - v * s, u * s + v * c);
        }
        return pts;
    }

    private (double U, double V) Local(P2 p)
    {
        double r = -RotationDeg * Math.PI / 180, dx = p.X - Center.X, dz = p.Z - Center.Z;
        return (dx * Math.Cos(r) - dz * Math.Sin(r), dx * Math.Sin(r) + dz * Math.Cos(r));
    }
}

/// <summary>A polyline with a width (round ends), used for fairways and creeks.</summary>
public sealed record Strip(IReadOnlyList<P2> Points, double Width) : Shape
{
    public override bool Contains(P2 p) => DistanceTo(p) <= 0;

    public override double DistanceTo(P2 p)
    {
        double best = double.MaxValue;
        for (int i = 0; i < Points.Count - 1; i++) best = Math.Min(best, SegmentDistance(p, Points[i], Points[i + 1]));
        return best - Width / 2;
    }

    public override Bounds Box { get; } = Bounds.Of(Points).Expand(Width / 2);

    internal static double SegmentDistance(P2 p, P2 a, P2 b)
    {
        var ab = b - a;
        double len2 = ab.X * ab.X + ab.Z * ab.Z;
        double t = len2 == 0 ? 0 : Math.Clamp(((p.X - a.X) * ab.X + (p.Z - a.Z) * ab.Z) / len2, 0, 1);
        return p.DistanceTo(a + ab * t);
    }
}

/// <summary>Any simple polygon (concave is fine), used for mapped greens, tees, bunkers, water and boundaries.</summary>
public sealed record Polygon(IReadOnlyList<P2> Points) : Shape
{
    public override Bounds Box { get; } = Bounds.Of(Points);

    public override bool Contains(P2 p)
    {
        if (!Box.Contains(p)) return false;
        bool inside = false;
        for (int i = 0, j = Points.Count - 1; i < Points.Count; j = i++)
        {
            P2 a = Points[i], b = Points[j];
            if ((a.Z > p.Z) != (b.Z > p.Z) && p.X < (b.X - a.X) * (p.Z - a.Z) / (b.Z - a.Z) + a.X) inside = !inside;
        }
        return inside;
    }

    public override double DistanceTo(P2 p)
    {
        double best = double.MaxValue;
        for (int i = 0, j = Points.Count - 1; i < Points.Count; j = i++) best = Math.Min(best, Strip.SegmentDistance(p, Points[j], Points[i]));
        return Contains(p) ? -best : best;
    }

    public P2 Centroid => new(Points.Average(p => p.X), Points.Average(p => p.Z));

    /// <summary>Enclosed area in m².</summary>
    public double Area
    {
        get
        {
            double a = 0;
            for (int i = 0, j = Points.Count - 1; i < Points.Count; j = i++) a += Points[j].X * Points[i].Z - Points[i].X * Points[j].Z;
            return Math.Abs(a) / 2;
        }
    }
}

public readonly record struct Bounds(P2 Min, P2 Max)
{
    public bool Contains(P2 p) => p.X >= Min.X && p.X <= Max.X && p.Z >= Min.Z && p.Z <= Max.Z;
    public P2 Center => new((Min.X + Max.X) / 2, (Min.Z + Max.Z) / 2);
    public Bounds Expand(double d) => new(Min - new P2(d, d), Max + new P2(d, d));
    public Bounds Union(Bounds o) => new(new P2(Math.Min(Min.X, o.Min.X), Math.Min(Min.Z, o.Min.Z)), new P2(Math.Max(Max.X, o.Max.X), Math.Max(Max.Z, o.Max.Z)));

    public static Bounds Around(P2 c, double r) => new(c - new P2(r, r), c + new P2(r, r));

    public static Bounds Of(IEnumerable<P2> points)
    {
        double x0 = double.MaxValue, z0 = double.MaxValue, x1 = double.MinValue, z1 = double.MinValue;
        foreach (var p in points) { x0 = Math.Min(x0, p.X); z0 = Math.Min(z0, p.Z); x1 = Math.Max(x1, p.X); z1 = Math.Max(z1, p.Z); }
        return new(new P2(x0, z0), new P2(x1, z1));
    }
}

/// <summary>
/// One hole. Coordinates are metres; for the test holes the tee is at the origin with X toward the hole and Z right,
/// for a mapped course every hole shares one frame (X north, Z east).
/// <see cref="TeeAim"/> is where the tee shot is aimed by default (the landing area on longer holes).
/// <see cref="Bounds"/> frames the hole; it is also the out-of-bounds line unless <see cref="Boundary"/> is set.
/// </summary>
public sealed record Hole(
    int Number, string Name, int Par, P2 Tee, P2 Pin, P2 TeeAim,
    IReadOnlyList<Shape> Fairways, IReadOnlyList<Shape> Greens, IReadOnlyList<Shape> Bunkers, IReadOnlyList<Shape> Water,
    Bounds Bounds)
{
    public const double TeeBoxRadius = 5;
    /// <summary>Width of the fringe (collar) around greens, m.</summary>
    public const double FringeWidth = 1.8;
    /// <summary>Width of the first cut of rough around fairways, tees and greens (with fringe), m; beyond it is deep rough.</summary>
    public const double FirstCutWidth = 9;

    /// <summary>Mapped tee boxes (count as the tee lie). Empty for the test holes, which use a radius around <see cref="Tee"/>.</summary>
    public IReadOnlyList<Shape> Tees { get; init; } = Array.Empty<Shape>();

    /// <summary>The in-bounds areas (e.g. the course property). When null, out of bounds is outside <see cref="Bounds"/>.</summary>
    public IReadOnlyList<Shape>? Boundary { get; init; }

    /// <summary>The mapped hole line, tee to pin (dog-leg points included), if known.</summary>
    public IReadOnlyList<P2>? Route { get; init; }

    public bool InBounds(P2 p) => Boundary == null ? Bounds.Contains(p) : Boundary.Any(b => b.Contains(p));

    public Lie LieAt(P2 p)
    {
        if (!InBounds(p)) return Lie.OutOfBounds;
        if (Water.Any(w => w.Contains(p))) return Lie.Water;
        if (Bunkers.Any(b => b.Contains(p))) return Lie.Bunker;
        if (Greens.Any(g => g.Contains(p))) return Lie.Green;
        if (Greens.Any(g => Within(g, p, FringeWidth))) return Lie.Fringe;
        if (p.DistanceTo(Tee) <= TeeBoxRadius || Tees.Any(t => t.Contains(p))) return Lie.Tee;
        if (Fairways.Any(f => f.Contains(p))) return Lie.Fairway;
        return InFirstCut(p) ? Lie.Rough : Lie.DeepRough;
    }

    private bool InFirstCut(P2 p) =>
        p.DistanceTo(Tee) <= TeeBoxRadius + FirstCutWidth
        || Fairways.Any(f => Within(f, p, FirstCutWidth)) || Tees.Any(t => Within(t, p, FirstCutWidth))
        || Greens.Any(g => Within(g, p, FringeWidth + FirstCutWidth));

    /// <summary>
    /// True if <paramref name="p"/> is inside <paramref name="shape"/> grown by <paramref name="margin"/> metres, the same
    /// growth the renderer uses (ellipses grow both radii; polygons and strips by distance from the edge).
    /// </summary>
    public static bool Within(Shape shape, P2 p, double margin) => shape switch
    {
        Ellipse e => (e with { RadiusX = e.RadiusX + margin, RadiusZ = e.RadiusZ + margin }).Contains(p),
        _ => shape.IsNear(p, margin),
    };

    /// <summary>Playing length along the hole line, or via the tee aim point when there is none.</summary>
    public double LengthYards => (Route != null ? OsmCourse.Length(Route) : Tee.DistanceTo(TeeAim) + TeeAim.DistanceTo(Pin)) * Units.MetresToYards;

    private IEnumerable<Shape> PlayAreas => Fairways.Concat(Greens).Concat(Bunkers).Concat(Water).Concat(Tees);

    /// <summary>Distance to the nearest mown/playing area (used to keep scenery off the hole).</summary>
    public double DistanceToPlayArea(P2 p) =>
        PlayAreas.Select(s => s.DistanceTo(p)).Append(p.DistanceTo(Tee) - TeeBoxRadius).Min();

    /// <summary>Faster than <see cref="DistanceToPlayArea"/> when only "is it within this margin" matters.</summary>
    public bool IsNearPlayArea(P2 p, double margin) =>
        p.DistanceTo(Tee) - TeeBoxRadius <= margin || PlayAreas.Any(s => s.IsNear(p, margin));
}

public static class Units
{
    public const double MetresToYards = 1.0936133, YardsToMetres = 0.9144;
}
