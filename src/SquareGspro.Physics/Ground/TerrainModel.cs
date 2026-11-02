namespace SquareGspro.Physics;

/// <summary>
/// The ground, in the engine's world frame (X right, Y forward, Z up; metres). Implementations must be immutable and
/// thread-safe. Return surface properties as configured (with their firmness and moisture); the engine folds those in
/// with <see cref="SurfaceProperties.Effective"/>. Returning the same instance for the same surface lets the engine
/// cache that.
/// </summary>
public interface ITerrainProvider
{
    double GetElevation(double x, double y);

    /// <summary>Unit upward normal of the ground at (x, y).</summary>
    Vector3D GetSurfaceNormal(double x, double y);

    SurfaceProperties GetSurfaceProperties(double x, double y);
}

public static class TerrainExtensions
{
    /// <summary>Slope angle (radians) at (x, y).</summary>
    public static double GetSlopeAngle(this ITerrainProvider t, double x, double y) => Math.Acos(Math.Clamp(t.GetSurfaceNormal(x, y).Z, -1, 1));

    /// <summary>Unit normal from a height gradient (rise per metre along +X and +Y).</summary>
    public static Vector3D NormalFromGradient(double dx, double dy) => new Vector3D(-dx, -dy, 1).Normalized();
}

/// <summary>Level ground everywhere.</summary>
public sealed class FlatTerrain : ITerrainProvider
{
    public FlatTerrain(SurfaceProperties surface, double elevation = 0) { Surface = surface; Elevation = elevation; }

    public SurfaceProperties Surface { get; }
    public double Elevation { get; }

    public double GetElevation(double x, double y) => Elevation;
    public Vector3D GetSurfaceNormal(double x, double y) => Vector3D.UnitZ;
    public SurfaceProperties GetSurfaceProperties(double x, double y) => Surface;
}

/// <summary>
/// A tilted plane: z = z₀ + <see cref="RiseAlongX"/>·x + <see cref="RiseAlongY"/>·y (rise in metres per metre, e.g.
/// 0.02 = 2 %). Uphill toward +Y has positive RiseAlongY.
/// </summary>
public sealed class InclinedPlaneTerrain : ITerrainProvider
{
    private readonly Vector3D _normal;

    public InclinedPlaneTerrain(SurfaceProperties surface, double riseAlongX, double riseAlongY, double elevationAtOrigin = 0)
    {
        Surface = surface; RiseAlongX = riseAlongX; RiseAlongY = riseAlongY; ElevationAtOrigin = elevationAtOrigin;
        _normal = TerrainExtensions.NormalFromGradient(riseAlongX, riseAlongY);
    }

    public SurfaceProperties Surface { get; }
    public double RiseAlongX { get; }
    public double RiseAlongY { get; }
    public double ElevationAtOrigin { get; }

    public double GetElevation(double x, double y) => ElevationAtOrigin + RiseAlongX * x + RiseAlongY * y;
    public Vector3D GetSurfaceNormal(double x, double y) => _normal;
    public SurfaceProperties GetSurfaceProperties(double x, double y) => Surface;
}

/// <summary>
/// Ground from a height function and a surface map (synthetic greens, double-breakers, tests). Normals come from
/// central differences over <see cref="NormalStep"/> metres.
/// </summary>
public sealed class FunctionTerrain : ITerrainProvider
{
    private readonly Func<double, double, double> _height;
    private readonly Func<double, double, SurfaceProperties> _surface;

    public FunctionTerrain(Func<double, double, double> height, Func<double, double, SurfaceProperties> surface, double normalStep = 0.05)
    {
        _height = height; _surface = surface; NormalStep = normalStep;
    }

    public FunctionTerrain(Func<double, double, double> height, SurfaceProperties surface) : this(height, (_, _) => surface) { }

    public double NormalStep { get; }

    public double GetElevation(double x, double y) => _height(x, y);

    public Vector3D GetSurfaceNormal(double x, double y)
    {
        double h = NormalStep;
        return TerrainExtensions.NormalFromGradient((_height(x + h, y) - _height(x - h, y)) / (2 * h),
                                                    (_height(x, y + h) - _height(x, y - h)) / (2 * h));
    }

    public SurfaceProperties GetSurfaceProperties(double x, double y) => _surface(x, y);
}

/// <summary>
/// A regular grid of heights, bilinearly interpolated, flat (edge value) outside the grid. Point (i, j) is at
/// (<see cref="OriginX"/> + i·<see cref="CellSize"/>, <see cref="OriginY"/> + j·<see cref="CellSize"/>); heights are
/// stored row-major by j: heights[j·CountX + i]. Normals come from central differences of the interpolated surface
/// over half a cell, which keeps them continuous across cell edges.
/// </summary>
public sealed class HeightmapTerrain : ITerrainProvider
{
    private readonly double[] _h;
    private readonly Func<double, double, SurfaceProperties> _surface;

    public HeightmapTerrain(double originX, double originY, double cellSize, int countX, int countY, double[] heights,
                            Func<double, double, SurfaceProperties> surface)
    {
        if (countX < 2 || countY < 2) throw new ArgumentException("A heightmap needs at least 2×2 points.");
        if (heights.Length != countX * countY) throw new ArgumentException("Heights must have countX × countY entries.");
        if (!(cellSize > 0)) throw new ArgumentException("Cell size must be positive.");
        OriginX = originX; OriginY = originY; CellSize = cellSize; CountX = countX; CountY = countY;
        _h = (double[])heights.Clone();
        _surface = surface;
    }

    public double OriginX { get; }
    public double OriginY { get; }
    public double CellSize { get; }
    public int CountX { get; }
    public int CountY { get; }

    public static HeightmapTerrain FromFunction(double originX, double originY, double cellSize, int countX, int countY,
                                                Func<double, double, double> height, Func<double, double, SurfaceProperties> surface)
    {
        var h = new double[countX * countY];
        for (int j = 0; j < countY; j++)
            for (int i = 0; i < countX; i++)
                h[j * countX + i] = height(originX + i * cellSize, originY + j * cellSize);
        return new HeightmapTerrain(originX, originY, cellSize, countX, countY, h, surface);
    }

    public double GetElevation(double x, double y)
    {
        double fx = Math.Clamp((x - OriginX) / CellSize, 0, CountX - 1.000001);
        double fy = Math.Clamp((y - OriginY) / CellSize, 0, CountY - 1.000001);
        int i = (int)fx, j = (int)fy;
        double tx = fx - i, ty = fy - j;
        int k = j * CountX + i;
        double a = _h[k] + (_h[k + 1] - _h[k]) * tx;
        double b = _h[k + CountX] + (_h[k + CountX + 1] - _h[k + CountX]) * tx;
        return a + (b - a) * ty;
    }

    public Vector3D GetSurfaceNormal(double x, double y)
    {
        double h = CellSize / 2;
        return TerrainExtensions.NormalFromGradient((GetElevation(x + h, y) - GetElevation(x - h, y)) / (2 * h),
                                                    (GetElevation(x, y + h) - GetElevation(x, y - h)) / (2 * h));
    }

    public SurfaceProperties GetSurfaceProperties(double x, double y) => _surface(x, y);
}
