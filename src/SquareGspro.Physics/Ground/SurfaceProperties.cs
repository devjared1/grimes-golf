namespace SquareGspro.Physics;

/// <summary>
/// How a ground surface treats the ball. All values are PROVISIONAL defaults that need calibrating against measured
/// bounces and roll-outs; only the green restitution and crater model come from published work (Penner 2002, "The run
/// of a golf ball", Can. J. Phys. 80).
/// <para>
/// Bounce: normal restitution e(v_n) = clamp(a + b·v_n + c·v_n², min, max), with v_n the normal impact speed in m/s
/// (Penner's green: a = 0.510, b = −0.0375, c = 0.000903 up to 20 m/s, 0.12 beyond). The turf is pushed into a crater
/// which tilts the effective surface normal back toward the incoming ball by θ_c = θ_ref (v / 18.6 m/s)(θ_in / 44.4°)
/// (Penner: θ_ref = 15.4° on a green). Friction at the contact is Coulomb with static and dynamic coefficients.
/// </para>
/// <para>
/// Roll: once rolling, the ground removes speed at μ_r·g·cos(slope) (rolling resistance, plus
/// <see cref="RollingResistancePerSpeed"/>·v if set).
/// </para>
/// <para>
/// <see cref="Firmness"/> (0 soft … 1 firm, 0.5 nominal) and <see cref="Moisture"/> (0 dry … 1 saturated) adjust the
/// coefficients through the provisional sensitivities in <see cref="Effective"/>; the coefficients given are the
/// values at nominal firmness and dry.
/// </para>
/// </summary>
public sealed record SurfaceProperties
{
    public string Name { get; init; } = "Surface";

    public double RestitutionConstant { get; init; } = 0.510;
    public double RestitutionPerSpeed { get; init; } = -0.0375;
    public double RestitutionPerSpeedSquared { get; init; } = 0.000903;
    /// <summary>Normal speed (m/s) above which <see cref="MinimumRestitution"/> applies (where Penner's quadratic flattens out).</summary>
    public double RestitutionCurveLimitSpeed { get; init; } = 20;
    public double MinimumRestitution { get; init; } = 0.12;
    public double MaximumRestitution { get; init; } = 0.75;
    /// <summary>Multiplies the restitution curve, so other surfaces can reuse Penner's shape.</summary>
    public double RestitutionScale { get; init; } = 1.0;

    /// <summary>Crater angle θ_ref (degrees) at the reference impact (18.6 m/s at 44.4°).</summary>
    public double CraterAngleDegrees { get; init; } = 15.4;
    public double MaximumCraterAngleDegrees { get; init; } = 35;

    /// <summary>
    /// Tangential restitution e_t of the ball-turf contact: when the contact grips, its slip after the impact is
    /// −e_t times the slip before (0 = the ball leaves rolling). Positive values model the ball and turf storing shear
    /// and returning it as backspin, which is what lets high-spin wedges check and spin back on firm greens.
    /// PROVISIONAL; scaled by firmness and moisture in <see cref="Effective"/>.
    /// </summary>
    public double TangentialRestitution { get; init; }

    public double StaticFriction { get; init; } = 0.45;
    public double DynamicFriction { get; init; } = 0.40;

    /// <summary>
    /// Slip speed (m/s) of the contact at which a bounce's friction has fallen to half: μ(u) = μ / (1 + u / this).
    /// Short grass sheared by a fast, spinning ball grips less than it does a slow one, so a full shot skids through its
    /// first, hard landing keeping much of its backspin (the pitch mark still brakes it) and the turf grabs on the
    /// next, slower touch: the ball hops forward, then checks. 0 = friction doesn't fade (the ball grips at once).
    /// Only bounces use it; the ground phase keeps the full friction. PROVISIONAL.
    /// </summary>
    public double ImpactSkidSpeed { get; init; }

    /// <summary>
    /// Turf drag on a landing ball (0-1): the share of its speed along the ground it loses ploughing through the grass,
    /// pushed back through its centre so its spin is untouched. The full share on a landing at least
    /// <see cref="HardLandingSpeed"/> into the ground, proportionally less on gentler bounces. The crater tilt
    /// (<see cref="CraterAngleDegrees"/>) brakes steep landings; this brakes shallow ones too, so a low, spinning wedge
    /// loses enough pace on its first hop for its backspin to stop it. PROVISIONAL.
    /// </summary>
    public double ImpactDrag { get; init; }

    /// <summary>Normal impact speed (m/s) from which a landing takes the full <see cref="ImpactDrag"/>.</summary>
    public const double HardLandingSpeed = 12;
    /// <summary>Rolling resistance μ_r (deceleration of a rolling ball in g on flat ground).</summary>
    public double RollingResistance { get; init; } = 0.12;
    /// <summary>Additional rolling resistance per m/s of speed (s/m).</summary>
    public double RollingResistancePerSpeed { get; init; }

    public double Firmness { get; init; } = 0.5;
    public double Moisture { get; init; }
    /// <summary>Typical height of surface irregularities (m). Informational for now: not used by the bounce model.</summary>
    public double RoughnessMetres { get; init; } = 0.005;

    /// <summary>Unit horizontal vector the grass grain grows toward (greens). Zero = no grain.</summary>
    public Vector3D GrainDirection { get; init; } = Vector3D.Zero;
    /// <summary>
    /// Grain strength (dimensionless, PROVISIONAL, uncalibrated, default 0): rolling down-grain lowers rolling
    /// resistance by this fraction and into the grain raises it; across the grain the ball is pushed sideways toward
    /// the grain at this fraction of the rolling resistance.
    /// </summary>
    public double GrainStrength { get; init; }

    /// <summary>Provisional sensitivities of the coefficients to firmness and moisture.</summary>
    public const double RestitutionFirmnessSensitivity = 0.6, CraterFirmnessSensitivity = 1.0,
                        RestitutionMoistureLoss = 0.3, CraterMoistureGain = 0.5, RollingMoistureGain = 0.5, FrictionMoistureLoss = 0.2,
                        TangentialFirmnessSensitivity = 1.0, TangentialMoistureLoss = 0.5;

    /// <summary>The surface with firmness and moisture folded into the coefficients (firmness 0.5, moisture 0 leaves them unchanged).</summary>
    public SurfaceProperties Effective()
    {
        double firm = Firmness - 0.5, wet = Math.Clamp(Moisture, 0, 1);
        return this with
        {
            RestitutionScale = RestitutionScale * (1 + RestitutionFirmnessSensitivity * firm) * (1 - RestitutionMoistureLoss * wet),
            CraterAngleDegrees = CraterAngleDegrees * Math.Max(0, 1 - CraterFirmnessSensitivity * firm) * (1 + CraterMoistureGain * wet),
            RollingResistance = RollingResistance * (1 + RollingMoistureGain * wet),
            StaticFriction = StaticFriction * (1 - FrictionMoistureLoss * wet),
            DynamicFriction = DynamicFriction * (1 - FrictionMoistureLoss * wet),
            TangentialRestitution = TangentialRestitution * Math.Max(0, 1 + TangentialFirmnessSensitivity * firm) * (1 - TangentialMoistureLoss * wet),
            Firmness = 0.5, Moisture = 0,
        };
    }

    /// <summary>Normal restitution at normal impact speed <paramref name="normalSpeed"/> (m/s). Call on an <see cref="Effective"/> surface.</summary>
    public double RestitutionAt(double normalSpeed)
    {
        double v = Math.Abs(normalSpeed);
        double e = v >= RestitutionCurveLimitSpeed
            ? MinimumRestitution
            : RestitutionConstant + RestitutionPerSpeed * v + RestitutionPerSpeedSquared * v * v;
        return Math.Clamp(Math.Max(e, MinimumRestitution) * RestitutionScale, 0, MaximumRestitution);
    }

    /// <summary>Crater angle (radians) for an impact at <paramref name="speed"/> m/s and <paramref name="impactAngleRad"/> below the surface tangent.</summary>
    public double CraterAngle(double speed, double impactAngleRad) =>
        Math.Min(CraterAngleDegrees * (speed / 18.6) * (impactAngleRad * UnitConversion.RadiansToDegrees / 44.4), MaximumCraterAngleDegrees)
        * UnitConversion.DegreesToRadians;

    public IEnumerable<string> Validate()
    {
        if (!(RestitutionScale >= 0)) yield return $"{Name}: restitution scale must be ≥ 0.";
        if (!(MaximumRestitution is >= 0 and < 1)) yield return $"{Name}: maximum restitution must be in [0, 1).";
        if (!(StaticFriction >= 0 && DynamicFriction >= 0)) yield return $"{Name}: friction must be ≥ 0.";
        if (!(RollingResistance >= 0 && RollingResistancePerSpeed >= 0)) yield return $"{Name}: rolling resistance must be ≥ 0.";
        if (!(Firmness is >= 0 and <= 1)) yield return $"{Name}: firmness must be 0-1.";
        if (!(Moisture is >= 0 and <= 1)) yield return $"{Name}: moisture must be 0-1.";
        if (!(CraterAngleDegrees >= 0)) yield return $"{Name}: crater angle must be ≥ 0.";
        if (!(TangentialRestitution is >= 0 and < 1)) yield return $"{Name}: tangential restitution must be in [0, 1).";
    }
}

/// <summary>
/// Provisional surface presets. Green follows Penner's (2002) restitution and crater model; its friction and
/// tangential restitution, and all the other presets, are judgement chosen for the expected behaviour (hard ground
/// livelier than fairway, rough and sand deader, wedges checking on greens) and must be calibrated before being relied on.
/// </summary>
public static class Surfaces
{
    public static SurfaceProperties Green { get; } = GreenModel.Surface(10);

    public static SurfaceProperties Fairway { get; } = new()
    {
        Name = "Fairway", CraterAngleDegrees = 13, StaticFriction = 0.45, DynamicFriction = 0.40, RollingResistance = 0.12,
        TangentialRestitution = 0.1, ImpactSkidSpeed = 7,
    };

    // Turf drag only around the greens: on fairways it would also cut short a drive's roll.
    public static SurfaceProperties Fringe { get; } = Fairway with
    {
        Name = "Fringe", CraterAngleDegrees = 14, RollingResistance = 0.09, ImpactSkidSpeed = 5, ImpactDrag = 0.3,
    };

    public static SurfaceProperties Rough { get; } = new()
    {
        Name = "Rough", RestitutionScale = 0.7, CraterAngleDegrees = 20, StaticFriction = 0.65, DynamicFriction = 0.6,
        RollingResistance = 0.30, Firmness = 0.4, RoughnessMetres = 0.04,
    };

    public static SurfaceProperties DeepRough { get; } = new()
    {
        Name = "Deep rough", RestitutionScale = 0.45, CraterAngleDegrees = 28, StaticFriction = 0.85, DynamicFriction = 0.8,
        RollingResistance = 0.6, Firmness = 0.35, RoughnessMetres = 0.1,
    };

    public static SurfaceProperties Sand { get; } = new()
    {
        Name = "Sand", RestitutionScale = 0.15, CraterAngleDegrees = 35, MaximumCraterAngleDegrees = 45, StaticFriction = 0.9,
        DynamicFriction = 0.85, RollingResistance = 1.0, Firmness = 0.2, RoughnessMetres = 0.01,
    };

    public static SurfaceProperties HardGround { get; } = new()
    {
        Name = "Hard ground", RestitutionScale = 1.25, CraterAngleDegrees = 6, StaticFriction = 0.4, DynamicFriction = 0.35,
        RollingResistance = 0.09, Firmness = 0.9,
    };

    public static SurfaceProperties SoftGround { get; } = Fairway with { Name = "Soft ground", Firmness = 0.15 };

    public static SurfaceProperties WetFairway { get; } = Fairway with { Name = "Wet fairway", Moisture = 0.8 };
}
