namespace GrimesGolf.Physics;

/// <summary>
/// A golf ball model: size, mass, rotational inertia and aerodynamic behaviour. Immutable, so one instance can be
/// shared by concurrent simulations.
/// </summary>
public sealed record BallProperties
{
    public string Name { get; init; } = "Standard golf ball";

    /// <summary>Mass (kg). Rules maximum 45.93 g.</summary>
    public double MassKg { get; init; } = 0.04593;

    /// <summary>Diameter (m). Rules minimum 42.67 mm.</summary>
    public double DiameterMetres { get; init; } = 0.04267;

    /// <summary>
    /// Moment of inertia as a fraction k of m·r² (I = k·m·r²). 2/5 is a uniform solid sphere; multi-layer balls are
    /// typically a little below or above that depending on construction.
    /// </summary>
    public double InertiaFactor { get; init; } = 0.4;

    public IAerodynamicModel Aerodynamics { get; init; } = DefaultAerodynamics.Provisional;

    public double RadiusMetres => DiameterMetres / 2;
    public double CrossSectionAreaSquareMetres => Math.PI * RadiusMetres * RadiusMetres;
    public double MomentOfInertia => InertiaFactor * MassKg * RadiusMetres * RadiusMetres;

    public static BallProperties Standard { get; } = new();

    /// <summary>Problems with the values (empty if usable).</summary>
    public IEnumerable<string> Validate()
    {
        if (!(MassKg is > 0.03 and < 0.06)) yield return $"Ball mass {MassKg} kg is not a plausible golf ball.";
        if (!(DiameterMetres is > 0.035 and < 0.05)) yield return $"Ball diameter {DiameterMetres} m is not a plausible golf ball.";
        if (!(InertiaFactor is > 0.2 and <= 0.6)) yield return $"Ball inertia factor {InertiaFactor} is outside 0.2-0.6.";
    }
}
