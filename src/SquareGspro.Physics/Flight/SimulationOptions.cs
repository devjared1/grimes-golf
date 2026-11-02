namespace SquareGspro.Physics;

/// <summary>How to run a shot simulation. Immutable; defaults are the recommended values.</summary>
public sealed record SimulationOptions
{
    /// <summary>Flight integration step (s). 1/240 s by default.</summary>
    public double TimeStepSeconds { get; init; } = 1.0 / 240;
    /// <summary>Longest a single flight (launch or bounce) may last before the simulation gives up.</summary>
    public double MaximumFlightTimeSeconds { get; init; } = 20;
    /// <summary>Longest the ball may move along the ground.</summary>
    public double MaximumGroundTimeSeconds { get; init; } = 60;
    public bool EnableBounce { get; init; } = true;
    public bool EnableRoll { get; init; } = true;

    /// <summary>
    /// Interval (s) between recorded trajectory samples, independent of the physics step; 0 records every step.
    /// Launch, landing, every ground contact and the final position are always recorded.
    /// </summary>
    public double TrajectorySampleIntervalSeconds { get; init; } = 1.0 / 60;

    /// <summary>Ball start (x, y) in world coordinates; the ball is placed on the terrain there.</summary>
    public double StartX { get; init; }
    public double StartY { get; init; }
    /// <summary>Height of the bottom of the ball above the ground (tee height), m.</summary>
    public double TeeHeightMetres { get; init; }
    /// <summary>Aim line heading, degrees clockwise from world +Y. Launch direction is relative to this.</summary>
    public double AimHeadingDegrees { get; init; }

    /// <summary>A bounce whose outgoing normal speed is below this (m/s) turns into rolling contact (a hop of under ~1 cm).</summary>
    public double RollTransitionNormalSpeed { get; init; } = 0.4;
    /// <summary>Safety stop on the number of ground impacts (a diagnostic is reported if hit). Not a modelled bounce count.</summary>
    public int MaximumGroundImpacts { get; init; } = 200;

    public GroundOptions Ground { get; init; } = GroundOptions.Default;

    /// <summary>
    /// Optional hook for game rules (hazards, the hole): told about every ground impact and every ground step, and
    /// can stop the ball there. It may hold per-shot state, so don't share one between concurrent simulations.
    /// </summary>
    public IGroundObserver? Observer { get; init; }

    public static SimulationOptions Default { get; } = new();

    public IEnumerable<string> Validate()
    {
        if (!(TimeStepSeconds is > 0 and <= 0.05)) yield return $"Time step {TimeStepSeconds} s is outside (0, 0.05].";
        if (!(MaximumFlightTimeSeconds is > 0 and <= 120)) yield return $"Maximum flight time {MaximumFlightTimeSeconds} s is outside (0, 120].";
        if (!(MaximumGroundTimeSeconds is > 0 and <= 600)) yield return $"Maximum ground time {MaximumGroundTimeSeconds} s is outside (0, 600].";
        if (!(TrajectorySampleIntervalSeconds >= 0)) yield return "Trajectory sample interval must be ≥ 0.";
        if (!(TeeHeightMetres is >= 0 and <= 0.15)) yield return $"Tee height {TeeHeightMetres} m is outside 0-0.15 m.";
        if (!double.IsFinite(StartX) || !double.IsFinite(StartY) || !double.IsFinite(AimHeadingDegrees)) yield return "Start position and aim must be finite.";
        if (!(RollTransitionNormalSpeed is > 0 and < 3)) yield return $"Roll transition speed {RollTransitionNormalSpeed} m/s is outside (0, 3).";
        foreach (var p in Ground.Validate()) yield return p;
    }
}
