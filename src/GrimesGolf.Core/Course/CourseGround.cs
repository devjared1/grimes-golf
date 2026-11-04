using GrimesGolf.Physics;

namespace GrimesGolf.Core.Course;

/// <summary>
/// A hole's ground as the physics engine sees it. Frames: the course is X forward/north, Z right/east, Y up; the
/// engine is x right, y forward, z up, so engine (x, y, z) = course (Z, X, height). Both are right-handed, and a
/// course heading (radians from +X toward +Z) is the same angle as an engine heading (clockwise from +y).
/// Lies map to engine surfaces; water and out of bounds play as fairway because <see cref="HoleRound"/> stops the
/// ball as soon as it reaches them.
/// </summary>
public sealed class CourseGround : ITerrainProvider
{
    private readonly Hole _hole;
    private readonly Terrain? _terrain;
    private readonly SurfaceProperties? _fixed;

    /// <param name="fixedSurface">Use this surface everywhere instead of looking up lies (fast, for putt reads).</param>
    public CourseGround(Hole hole, Terrain? terrain, SurfaceProperties? fixedSurface = null)
    {
        _hole = hole;
        _terrain = terrain;
        _fixed = fixedSurface;
    }

    private static readonly SurfaceProperties DefaultGreen = GreenModel.Surface(10);

    /// <summary>The green in play (stimp 10 unless tuned).</summary>
    public static SurfaceProperties Green => ActivePhysics.SurfaceFor(Lie.Green);

    /// <summary>The engine surface for a lie, with the current tuning (<see cref="ActivePhysics"/>).</summary>
    public static SurfaceProperties SurfaceFor(Lie lie) => ActivePhysics.SurfaceFor(lie);

    /// <summary>The untuned engine surface for a lie (provisional presets; the green is calibrated to stimp 10).</summary>
    public static SurfaceProperties DefaultSurfaceFor(Lie lie) => lie switch
    {
        Lie.Green => DefaultGreen,
        Lie.Fringe => Surfaces.Fringe,
        Lie.Rough => Surfaces.Rough,
        Lie.DeepRough => Surfaces.DeepRough,
        Lie.Bunker => Surfaces.Sand,
        Lie.CartPath => Surfaces.CartPath,
        _ => Surfaces.Fairway,
    };

    /// <summary>
    /// The engine's lie kind for a course lie (for <see cref="LieModel"/>). Water and out of bounds never get struck,
    /// nor do cart paths (relief), but a clean lie is the closest if one is.
    /// </summary>
    public static LieKind ToLieKind(Lie lie) => lie switch
    {
        Lie.Tee => LieKind.Tee,
        Lie.Fairway or Lie.CartPath => LieKind.Fairway,
        Lie.Fringe => LieKind.Fringe,
        Lie.Green => LieKind.Green,
        Lie.DeepRough => LieKind.DeepRough,
        Lie.Bunker => LieKind.Sand,
        _ => LieKind.Rough,
    };

    public static Vector3D ToEngine(P2 p, double height = 0) => new(p.Z, p.X, height);
    public static P2 ToCourse(in Vector3D v) => new(v.Y, v.X);

    /// <summary>A path point in the app's convention: Y is the bottom of the ball (the ground under a resting ball).</summary>
    public static FlightPoint ToFlightPoint(in TrajectoryPoint p, double timeOffset = 0) =>
        new(p.Time + timeOffset, p.Position.Y, p.Position.Z - BallProperties.Standard.RadiusMetres, p.Position.X);

    public double GetElevation(double x, double y) => _terrain?.HeightAt(new P2(y, x)) ?? 0;

    public Vector3D GetSurfaceNormal(double x, double y)
    {
        if (_terrain == null) return Vector3D.UnitZ;
        var g = _terrain.Gradient(new P2(y, x));
        return TerrainExtensions.NormalFromGradient(g.Z, g.X);
    }

    public SurfaceProperties GetSurfaceProperties(double x, double y) => _fixed ?? SurfaceFor(_hole.LieAt(new P2(y, x)));
}
