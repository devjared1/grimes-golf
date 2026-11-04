namespace GrimesGolf.Physics;

/// <summary>
/// A 3D vector in the engine's world frame: X lateral (positive = right of the +Y axis), Y forward, Z up. Metres,
/// metres per second, radians per second, etc. depending on what it holds.
/// </summary>
public readonly record struct Vector3D(double X, double Y, double Z)
{
    public static readonly Vector3D Zero = new(0, 0, 0);
    public static readonly Vector3D UnitX = new(1, 0, 0);
    public static readonly Vector3D UnitY = new(0, 1, 0);
    public static readonly Vector3D UnitZ = new(0, 0, 1);

    public double LengthSquared => X * X + Y * Y + Z * Z;
    public double Length => Math.Sqrt(LengthSquared);

    /// <summary>Length of the horizontal (X, Y) part.</summary>
    public double HorizontalLength => Math.Sqrt(X * X + Y * Y);

    /// <summary>The vector with Z set to 0.</summary>
    public Vector3D Horizontal => new(X, Y, 0);

    /// <summary>Unit vector in the same direction, or <see cref="Zero"/> if the length is (almost) 0.</summary>
    public Vector3D Normalized()
    {
        double l = Length;
        return l > 1e-12 ? new Vector3D(X / l, Y / l, Z / l) : Zero;
    }

    public static double Dot(in Vector3D a, in Vector3D b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static Vector3D Cross(in Vector3D a, in Vector3D b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    /// <summary>The part of the vector perpendicular to unit vector <paramref name="n"/>.</summary>
    public Vector3D ProjectOnPlane(in Vector3D n) => this - n * Dot(this, n);

    /// <summary>
    /// Rotates about +Z by <paramref name="headingRad"/> measured clockwise seen from above (from +Y toward +X), the
    /// same sense as a compass heading.
    /// </summary>
    public Vector3D RotateHeading(double headingRad)
    {
        double c = Math.Cos(headingRad), s = Math.Sin(headingRad);
        return new Vector3D(X * c + Y * s, -X * s + Y * c, Z);
    }

    public double DistanceTo(in Vector3D o) => (this - o).Length;
    public double HorizontalDistanceTo(in Vector3D o) => (this - o).HorizontalLength;

    public static Vector3D operator +(in Vector3D a, in Vector3D b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector3D operator -(in Vector3D a, in Vector3D b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vector3D operator -(in Vector3D a) => new(-a.X, -a.Y, -a.Z);
    public static Vector3D operator *(in Vector3D a, double k) => new(a.X * k, a.Y * k, a.Z * k);
    public static Vector3D operator *(double k, in Vector3D a) => new(a.X * k, a.Y * k, a.Z * k);
    public static Vector3D operator /(in Vector3D a, double k) => new(a.X / k, a.Y / k, a.Z / k);

    public override string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###})";
}
