namespace GrimesGolf.Physics;

/// <summary>
/// Air velocity (m/s, world frame) at a point and time. <c>heightAboveLaunch</c> is the height above the
/// launch surface, used for wind profiles. Implementations must be immutable and deterministic.
/// </summary>
public interface IWindField
{
    Vector3D VelocityAt(in Vector3D position, double heightAboveLaunch, double time);
}

/// <summary>
/// Wind directions follow the meteorological convention: the direction the wind blows FROM, in degrees clockwise
/// (seen from above) from the world +Y axis. With +Y as the target line, 0° is a headwind, 180° a tailwind, 90°
/// blows from the right (+X) toward the left.
/// </summary>
public static class Wind
{
    public static readonly IWindField Calm = new UniformWind(Vector3D.Zero);

    /// <summary>Air velocity vector for a wind of <paramref name="speedMs"/> from <paramref name="fromDegrees"/>.</summary>
    public static Vector3D FromDirection(double speedMs, double fromDegrees)
    {
        double a = fromDegrees * UnitConversion.DegreesToRadians;
        // The wind comes from (sin a, cos a) and blows the opposite way.
        return new Vector3D(-speedMs * Math.Sin(a), -speedMs * Math.Cos(a), 0);
    }

    public static IWindField Uniform(double speedMs, double fromDegrees) => new UniformWind(FromDirection(speedMs, fromDegrees));
}

/// <summary>The same wind everywhere and always.</summary>
public sealed record UniformWind(Vector3D Velocity) : IWindField
{
    public Vector3D VelocityAt(in Vector3D position, double heightAboveLaunch, double time) => Velocity;
}

/// <summary>
/// Wind that grows with height by a power law, v(h) = v_ref (h / h_ref)^α, from a wind measured at
/// <see cref="ReferenceHeightMetres"/>. α ≈ 0.14 for open, short grass (the "1/7 law"); larger over rougher ground.
/// Below <see cref="MinimumHeightMetres"/> the speed is held at its value there.
/// </summary>
public sealed record PowerLawWindProfile(double ReferenceSpeedMs, double FromDegrees, double ReferenceHeightMetres = 10,
                                         double Exponent = 0.143, double MinimumHeightMetres = 0.5) : IWindField
{
    private readonly Vector3D _dir = Wind.FromDirection(1, FromDegrees);

    public double SpeedAt(double height) =>
        ReferenceSpeedMs * Math.Pow(Math.Max(height, MinimumHeightMetres) / ReferenceHeightMetres, Exponent);

    public Vector3D VelocityAt(in Vector3D position, double heightAboveLaunch, double time) => _dir * SpeedAt(heightAboveLaunch);
}

/// <summary>A layer of a <see cref="TabulatedWindProfile"/>: wind speed and direction at a height.</summary>
public readonly record struct WindLayer(double HeightMetres, double SpeedMs, double FromDegrees);

/// <summary>
/// Wind given at a list of heights; the velocity vector is interpolated linearly between layers and held constant
/// above and below the table.
/// </summary>
public sealed class TabulatedWindProfile : IWindField
{
    private readonly double[] _h;
    private readonly Vector3D[] _v;

    public TabulatedWindProfile(IEnumerable<WindLayer> layers)
    {
        var sorted = layers.OrderBy(l => l.HeightMetres).ToArray();
        if (sorted.Length == 0) throw new ArgumentException("A wind profile needs at least one layer.");
        _h = sorted.Select(l => l.HeightMetres).ToArray();
        _v = sorted.Select(l => Wind.FromDirection(l.SpeedMs, l.FromDegrees)).ToArray();
    }

    public Vector3D VelocityAt(in Vector3D position, double heightAboveLaunch, double time)
    {
        double h = heightAboveLaunch;
        if (h <= _h[0]) return _v[0];
        for (int i = 1; i < _h.Length; i++)
            if (h <= _h[i])
            {
                double f = (h - _h[i - 1]) / (_h[i] - _h[i - 1]);
                return _v[i - 1] + (_v[i] - _v[i - 1]) * f;
            }
        return _v[^1];
    }
}

/// <summary>
/// Adds deterministic gusts to a base wind: the speed along the base wind direction varies between the mean and
/// <see cref="GustSpeedMs"/> as a sum of three sinusoids with periods of a few seconds and phases fixed by
/// <see cref="Seed"/>, plus a smaller crosswise part (<see cref="LateralFraction"/> of the gust amplitude). The same
/// seed always gives the same gusts. This is a simple stand-in for turbulence, not a spectral turbulence model.
/// </summary>
public sealed class GustingWind : IWindField
{
    private static readonly double[] Periods = { 7.3, 3.1, 1.7 };
    private static readonly double[] Weights = { 0.5, 0.3, 0.2 };
    private readonly double[] _phaseAlong = new double[3], _phaseAcross = new double[3];

    public GustingWind(IWindField baseWind, double meanSpeedMs, double gustSpeedMs, double fromDegrees, int seed = 1,
                       double lateralFraction = 0.3)
    {
        if (gustSpeedMs < meanSpeedMs) throw new ArgumentException("Gust speed must be at least the mean speed.");
        Base = baseWind;
        GustSpeedMs = gustSpeedMs;
        MeanSpeedMs = meanSpeedMs;
        Seed = seed;
        LateralFraction = lateralFraction;
        _along = Wind.FromDirection(1, fromDegrees);
        _across = Vector3D.Cross(Vector3D.UnitZ, _along);
        var rng = new Random(seed);
        for (int i = 0; i < 3; i++) { _phaseAlong[i] = rng.NextDouble() * 2 * Math.PI; _phaseAcross[i] = rng.NextDouble() * 2 * Math.PI; }
    }

    private readonly Vector3D _along, _across;
    public IWindField Base { get; }
    public double MeanSpeedMs { get; }
    public double GustSpeedMs { get; }
    public int Seed { get; }
    public double LateralFraction { get; }

    public Vector3D VelocityAt(in Vector3D position, double heightAboveLaunch, double time)
    {
        double amplitude = GustSpeedMs - MeanSpeedMs, along = 0, across = 0;
        for (int i = 0; i < 3; i++)
        {
            // Each term is 0..1, so the gust adds 0..amplitude along the wind.
            along += Weights[i] * 0.5 * (1 + Math.Sin(2 * Math.PI * time / Periods[i] + _phaseAlong[i]));
            across += Weights[i] * Math.Sin(2 * Math.PI * time / Periods[i] + _phaseAcross[i]);
        }
        return Base.VelocityAt(position, heightAboveLaunch, time) + _along * (amplitude * along) + _across * (amplitude * LateralFraction * across);
    }
}

/// <summary>Everything about the surroundings a simulation needs, passed in explicitly (no globals).</summary>
public sealed record EnvironmentConditions
{
    public AtmosphericConditions Atmosphere { get; init; } = AtmosphericConditions.Standard;
    public IWindField Wind { get; init; } = Physics.Wind.Calm;

    public static EnvironmentConditions Standard { get; } = new();
}
