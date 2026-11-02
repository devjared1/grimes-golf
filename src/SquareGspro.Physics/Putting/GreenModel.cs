namespace SquareGspro.Physics;

/// <summary>
/// Greens and the Stimpmeter. A Stimpmeter releases a ball from a notch 30 in up a 20° ramp; it rolls onto the green
/// at about <see cref="StimpmeterExitSpeed"/> and the distance it rolls on flat green, in feet, is the green speed.
/// <para>
/// The green's rolling resistance is calibrated so the engine's own roll model (rolling resistance plus air drag)
/// reproduces that distance: a ball started at the exit speed on a flat green of that surface rolls the Stimp
/// distance. Ignoring drag this is μ_r = v₀² / (2 g d). The exit speed is an approximation of the ramp physics
/// (energy of a rolling ball from 0.26 m drop, less ramp and landing losses), PROVISIONAL.
/// </para>
/// </summary>
public static class GreenModel
{
    /// <summary>Ball speed (m/s) leaving a Stimpmeter ramp, about 6 ft/s.</summary>
    public const double StimpmeterExitSpeed = 1.83;

    /// <summary>Drag-free closed form: rolling resistance giving a Stimp reading of <paramref name="stimpFeet"/>.</summary>
    public static double RollingResistanceForStimp(double stimpFeet, double exitSpeed = StimpmeterExitSpeed) =>
        exitSpeed * exitSpeed / (2 * PhysicsConstants.Gravity * stimpFeet * UnitConversion.FeetToMetres);

    /// <summary>Default green firmness: slightly receptive, so high-spin wedges check and spin back (firmer greens release them).</summary>
    public const double DefaultFirmness = 0.4;

    /// <summary>
    /// A green surface (Penner 2002 bounce) whose rolling resistance is calibrated to <paramref name="stimpFeet"/>,
    /// optionally with grain. <paramref name="firmness"/> (0 soft … 1 firm) sets how deep landing balls crater, which
    /// is what decides check and spin-back. The friction (0.5), tangential restitution (0.3), skid (5 m/s) and turf drag
    /// (0.3) are PROVISIONAL, chosen so full shots hop forward before the turf grabs them: wedges check or spin back,
    /// mid irons release a few yards, low spinning wedges skip and stop, and chips release.
    /// </summary>
    public static SurfaceProperties Surface(double stimpFeet, Vector3D grainDirection = default, double grainStrength = 0,
                                            double firmness = DefaultFirmness)
    {
        if (!(stimpFeet is >= 4 and <= 16)) throw new ArgumentOutOfRangeException(nameof(stimpFeet), "Stimp readings are 4-16 ft.");
        var template = new SurfaceProperties
        {
            Name = $"Green (stimp {stimpFeet:0.#})",
            CraterAngleDegrees = 15.4, StaticFriction = 0.55, DynamicFriction = 0.50, TangentialRestitution = 0.3,
            ImpactSkidSpeed = 5, ImpactDrag = 0.3,
            Firmness = firmness,
            RollingResistance = RollingResistanceForStimp(stimpFeet),
            GrainDirection = grainDirection.Horizontal.Normalized(), GrainStrength = grainStrength,
        };
        return CalibrateToStimp(template, stimpFeet);
    }

    /// <summary>Stimp reading (feet) the engine gives for <paramref name="surface"/> on flat ground in still, standard air.</summary>
    public static double MeasureStimp(SurfaceProperties surface, BallProperties? ball = null, double exitSpeed = StimpmeterExitSpeed)
    {
        ball ??= BallProperties.Standard;
        // Grain changes the reading with direction; the Stimp is measured both ways and averaged, so measure without it.
        var terrain = new FlatTerrain(surface with { GrainStrength = 0 });
        var c = new GroundContext
        {
            Ball = ball, Air = AirProperties.Standard, Wind = Wind.Calm, Terrain = terrain, Options = GroundOptions.Default,
        };
        var v = new Vector3D(0, exitSpeed, 0);
        var s = new BallState(c.OnSurface(0, 0), v, Vector3D.Cross(Vector3D.UnitZ, v) / ball.RadiusMetres);
        double t = 0, dt = GroundOptions.Default.TimeStepSeconds;
        while (t < 120 && RollModel.Step(ref s, ref c, t, dt) != RollStepOutcome.AtRest) t += dt;
        return s.Position.Y * UnitConversion.MetresToFeet;
    }

    /// <summary>Adjusts <paramref name="template"/>'s rolling resistance (bisection) until <see cref="MeasureStimp"/> reads <paramref name="stimpFeet"/>.</summary>
    public static SurfaceProperties CalibrateToStimp(SurfaceProperties template, double stimpFeet, BallProperties? ball = null)
    {
        double guess = RollingResistanceForStimp(stimpFeet);
        double lo = guess * 0.5, hi = guess * 1.2;
        for (int i = 0; i < 40; i++)
        {
            double mid = (lo + hi) / 2;
            double reading = MeasureStimp(template with { RollingResistance = mid }, ball);
            if (reading > stimpFeet) lo = mid; else hi = mid;
            if (hi - lo < 1e-7) break;
        }
        return template with { RollingResistance = (lo + hi) / 2 };
    }
}
