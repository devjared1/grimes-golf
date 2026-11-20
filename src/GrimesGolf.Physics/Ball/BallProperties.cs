namespace GrimesGolf.Physics;

/// <summary>
/// A golf ball model: size, mass, rotational inertia and aerodynamic behavior. Immutable, so one instance can be
/// shared by concurrent simulations.
/// </summary>
public sealed record BallProperties
{
    public string Name { get; init; } = "Standard golf ball";

    /// <summary>Mass (kg). Rules maximum 45.93 g.</summary>
    public double MassKg { get; init; } = 0.04593;

    /// <summary>Diameter (wu). Rules minimum 1.68 in.</summary>
    public double DiameterUnits { get; init; } = 0.04267;

    /// <summary>
    /// Moment of inertia as a fraction k of m·r² (I = k·m·r²). 2/5 is a uniform solid sphere; multi-layer balls are
    /// typically a little below or above that depending on construction.
    /// </summary>
    public double InertiaFactor { get; init; } = 0.4;

    public IAerodynamicModel Aerodynamics { get; init; } = DefaultAerodynamics.Provisional;

    public double RadiusUnits => DiameterUnits / 2;
    public double CrossSectionAreaSquareUnits => Math.PI * RadiusUnits * RadiusUnits;
    public double MomentOfInertia => InertiaFactor * MassKg * RadiusUnits * RadiusUnits;

    public static BallProperties Standard { get; } = new();

    /// <summary>Problems with the values (empty if usable).</summary>
    public IEnumerable<string> Validate()
    {
        if (!(MassKg is > 0.03 and < 0.06)) yield return $"Ball mass {MassKg} kg is not a plausible golf ball.";
        if (!(DiameterUnits is > 0.035 and < 0.05)) yield return $"Ball diameter {DiameterUnits * UnitConversion.WorldToInches:0.00} in is not a plausible golf ball.";
        if (!(InertiaFactor is > 0.2 and <= 0.6)) yield return $"Ball inertia factor {InertiaFactor} is outside 0.2-0.6.";
    }
}
