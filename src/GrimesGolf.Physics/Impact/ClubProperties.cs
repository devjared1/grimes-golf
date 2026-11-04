namespace GrimesGolf.Physics;

/// <summary>
/// A club head for the impact model. The head is treated as a free rigid body during the ~0.5 ms impact (the shaft
/// is ignored). Inertia is given about the head's centre of gravity, along the face axes. PROVISIONAL presets: typical
/// published magnitudes for head mass, MOI and CG depth, with restitution, friction and tangential restitution tuned
/// so the model reproduces TrackMan tour-average ball speed, launch and spin for tour-average club speed and attack
/// angle at dynamic lofts of 12.8° (driver), 23.1° (7-iron) and 34.7° (PW). The driver's negative tangential
/// restitution is an effective value that absorbs face bulge and roll, which the flat-face model doesn't have.
/// </summary>
public sealed record ClubProperties
{
    public string Name { get; init; } = "Club";
    /// <summary>Static loft (degrees). Used as the dynamic loft only when that isn't supplied (flagged as an estimate).</summary>
    public double LoftDegrees { get; init; } = 10.5;
    public double HeadMassKg { get; init; } = 0.200;
    /// <summary>Head mass the ball effectively meets, if different from <see cref="HeadMassKg"/> (e.g. to account for the shaft and hands; the driver preset's 15 g extra is provisional).</summary>
    public double? EffectiveMassKg { get; init; }

    /// <summary>Normal coefficient of restitution between this face and the ball at the face centre.</summary>
    public double Restitution { get; init; } = 0.83;
    /// <summary>Fractional COR loss per (distance from face centre in cm)² (face flex is strongest at the centre).</summary>
    public double RestitutionLossPerSquareCm { get; init; } = 0.004;
    /// <summary>Coulomb friction coefficient between face and ball.</summary>
    public double FaceFriction { get; init; } = 0.4;
    /// <summary>
    /// Tangential restitution: the ball's contact-point slip along the face after impact is −e_t × before. 0 means
    /// the ball leaves rolling on the face; positive values give "overspin" from the ball's tangential compliance
    /// (stored shear energy returned as spin, as measured for oblique ball impacts); negative values leave it with
    /// less spin than rolling. An effective, per-club calibration parameter.
    /// </summary>
    public double TangentialRestitution { get; init; }

    /// <summary>Distance of the CG behind the face (m).</summary>
    public double CgDepthMetres { get; init; } = 0.035;
    /// <summary>Height of the CG relative to the face centre (m; negative = below).</summary>
    public double CgHeightMetres { get; init; }
    /// <summary>MOI about the vertical axis through the CG (heel-toe twisting), kg·m².</summary>
    public double MoiVertical { get; init; } = 4.8e-4;
    /// <summary>MOI about the heel-toe axis through the CG (face rocking up/down), kg·m².</summary>
    public double MoiHorizontal { get; init; } = 2.8e-4;

    public double Mass => EffectiveMassKg ?? HeadMassKg;

    public static ClubProperties Driver { get; } = new()
    {
        Name = "Driver", LoftDegrees = 10.5, HeadMassKg = 0.200, EffectiveMassKg = 0.215, Restitution = 0.83, FaceFriction = 0.4,
        TangentialRestitution = -0.115, CgDepthMetres = 0.038, CgHeightMetres = -0.003, MoiVertical = 4.8e-4, MoiHorizontal = 2.8e-4,
    };

    public static ClubProperties SevenIron { get; } = new()
    {
        Name = "7-iron", LoftDegrees = 34, HeadMassKg = 0.265, Restitution = 0.755, RestitutionLossPerSquareCm = 0.002, FaceFriction = 0.4,
        TangentialRestitution = 0.30, CgDepthMetres = 0.008, CgHeightMetres = -0.002, MoiVertical = 2.6e-4, MoiHorizontal = 0.8e-4,
    };

    public static ClubProperties PitchingWedge { get; } = new()
    {
        Name = "Pitching wedge", LoftDegrees = 46, HeadMassKg = 0.290, Restitution = 0.825, RestitutionLossPerSquareCm = 0.002, FaceFriction = 0.4,
        TangentialRestitution = 0.31, CgDepthMetres = 0.007, CgHeightMetres = -0.002, MoiVertical = 2.8e-4, MoiHorizontal = 0.9e-4,
    };

    public IEnumerable<string> Validate()
    {
        if (!(Mass is > 0.1 and < 0.5)) yield return $"{Name}: head mass {Mass} kg is outside 0.1-0.5 kg.";
        if (!(Restitution is > 0.3 and <= 0.9)) yield return $"{Name}: COR {Restitution} is outside (0.3, 0.9].";
        if (!(FaceFriction is >= 0 and <= 1.5)) yield return $"{Name}: face friction {FaceFriction} is outside 0-1.5.";
        if (!(TangentialRestitution is > -1 and < 1)) yield return $"{Name}: tangential restitution {TangentialRestitution} is outside (-1, 1).";
        if (!(LoftDegrees is >= 0 and <= 70)) yield return $"{Name}: loft {LoftDegrees}° is outside 0-70°.";
        if (!(CgDepthMetres is >= 0 and < 0.08)) yield return $"{Name}: CG depth {CgDepthMetres} m is outside 0-0.08 m.";
        if (!(MoiVertical > 0 && MoiHorizontal > 0)) yield return $"{Name}: moments of inertia must be positive.";
        if (!(RestitutionLossPerSquareCm >= 0)) yield return $"{Name}: off-centre COR loss must be ≥ 0.";
    }
}

/// <summary>
/// How the club met the ball. Angles in degrees: path and face positive = right (path in-to-out for a right-hander),
/// attack positive = up, dynamic loft = angle of the face normal above horizontal at impact. Impact location on the
/// face: toward the toe positive, above the face centre positive (metres).
/// </summary>
public sealed record ClubDelivery
{
    public required double ClubSpeed { get; init; }
    public double PathDegrees { get; init; }
    public double FaceAngleDegrees { get; init; }
    public double AttackAngleDegrees { get; init; }
    public double? DynamicLoftDegrees { get; init; }
    public double ImpactToeMetres { get; init; }
    public double ImpactHeightMetres { get; init; }
    public bool LeftHanded { get; init; }

    public IEnumerable<string> Validate()
    {
        if (!(ClubSpeed is > 0 and <= 80)) yield return $"Club speed {ClubSpeed * UnitConversion.MetresPerSecondToMph:0.#} mph is outside 0-179 mph.";
        if (!(Math.Abs(PathDegrees) <= 30)) yield return $"Club path {PathDegrees}° is outside ±30°.";
        if (!(Math.Abs(FaceAngleDegrees) <= 30)) yield return $"Face angle {FaceAngleDegrees}° is outside ±30°.";
        if (!(Math.Abs(AttackAngleDegrees) <= 20)) yield return $"Attack angle {AttackAngleDegrees}° is outside ±20°.";
        if (DynamicLoftDegrees is { } l && !(l is > -5 and <= 75)) yield return $"Dynamic loft {l}° is outside -5 to 75°.";
        if (!(Math.Abs(ImpactToeMetres) <= 0.04 && Math.Abs(ImpactHeightMetres) <= 0.03)) yield return "Impact location is off the face.";
    }
}
