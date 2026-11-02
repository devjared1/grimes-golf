namespace SquareGspro.Physics;

/// <summary>What the ball sits on when it's struck.</summary>
public enum LieKind { Tee, Fairway, Fringe, Green, Rough, DeepRough, Sand }

/// <summary>
/// The ball's lie at address. <see cref="UphillDegrees"/>: ground rising along the aim line (negative = downhill).
/// <see cref="BallAboveFeetDegrees"/>: ground tilt across the aim line toward the ball as the golfer stands (negative =
/// ball below the feet). Handedness decides which way a sidehill lie turns the face.
/// </summary>
public sealed record LieCondition(LieKind Kind, double UphillDegrees = 0, double BallAboveFeetDegrees = 0, bool LeftHanded = false)
{
    public static LieCondition Flat(LieKind kind) => new(kind);
    public bool IsLevel => Math.Abs(UphillDegrees) < 0.5 && Math.Abs(BallAboveFeetDegrees) < 0.5;
}

/// <summary>How a lie changes the strike: multipliers on ball speed and spin, and a change in launch angle.</summary>
public sealed record LieResponse(double BallSpeedFactor, double SpinFactor, double LaunchChangeDegrees)
{
    public static LieResponse None { get; } = new(1, 1, 0);
}

/// <summary>What <see cref="LieModel.Apply"/> did to a measured launch.</summary>
public sealed record LieAdjustment(
    LaunchConditions Launch, LieCondition Condition, double BallSpeedFactor, double SpinFactor,
    double LaunchChangeDegrees, double DirectionChangeDegrees, double SpinAxisChangeDegrees)
{
    public bool Changed =>
        Math.Abs(BallSpeedFactor - 1) > 1e-6 || Math.Abs(SpinFactor - 1) > 1e-6 || Math.Abs(LaunchChangeDegrees) > 1e-6 ||
        Math.Abs(DirectionChangeDegrees) > 1e-6 || Math.Abs(SpinAxisChangeDegrees) > 1e-6;

    /// <summary>Short description, e.g. "−5% speed, −30% spin, +1.0° launch". Empty when nothing changed.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Math.Abs(BallSpeedFactor - 1) >= 0.005) parts.Add($"{(BallSpeedFactor - 1) * 100:+0;−0}% speed");
            if (Math.Abs(SpinFactor - 1) >= 0.005) parts.Add($"{(SpinFactor - 1) * 100:+0;−0}% spin");
            if (Math.Abs(LaunchChangeDegrees) >= 0.05) parts.Add($"{LaunchChangeDegrees:+0.0;−0.0}° launch");
            if (Math.Abs(DirectionChangeDegrees) >= 0.05) parts.Add($"starts {Math.Abs(DirectionChangeDegrees):0.0}° {(DirectionChangeDegrees > 0 ? "right" : "left")}");
            return string.Join(", ", parts);
        }
    }
}

/// <summary>
/// Adjusts a measured launch for the ball's lie. A launch monitor reads the ball off a mat, so it can't see the
/// rough, the sand or the slope the ball would have been struck from; this model estimates what they would have done.
/// <list type="bullet">
/// <item>Lie: grass or sand between face and ball cuts spin (the "flier") and some speed, and launches the ball a
///   little higher (<see cref="Responses"/>).</item>
/// <item>Uphill/downhill: the golfer sets up with the slope, adding (uphill) or removing loft: launch changes by
///   <see cref="SlopeLaunchFactor"/> × the slope angle and spin by <see cref="SlopeSpinPerDegree"/> per degree.</item>
/// <item>Sidehill: with the club soled on a tilted lie, a face of loft L tilted by φ points off line by
///   δ = atan(tan L · sin φ) (ball above the feet turns it left for a right-hander). The start line moves by
///   <see cref="SidehillStartFactor"/> × δ (the start line mostly follows the face) and the spin axis by
///   −<see cref="SidehillAxisFactor"/> × δ (a closed face to the path draws).</item>
/// </list>
/// PROVISIONAL game model: the factors are judgement, comparable to what golf simulators apply, not measurements.
/// Putts should not be adjusted.
/// </summary>
public sealed class LieModel
{
    public LieModel(IReadOnlyDictionary<LieKind, LieResponse> responses, double slopeLaunchFactor = 0.8, double slopeSpinPerDegree = 0.015,
                    double sidehillStartFactor = 0.8, double sidehillAxisFactor = 2.0, double maximumSlopeDegrees = 20)
    {
        Responses = responses;
        SlopeLaunchFactor = slopeLaunchFactor;
        SlopeSpinPerDegree = slopeSpinPerDegree;
        SidehillStartFactor = sidehillStartFactor;
        SidehillAxisFactor = sidehillAxisFactor;
        MaximumSlopeDegrees = maximumSlopeDegrees;
    }

    public IReadOnlyDictionary<LieKind, LieResponse> Responses { get; }
    public double SlopeLaunchFactor { get; }
    public double SlopeSpinPerDegree { get; }
    public double SidehillStartFactor { get; }
    public double SidehillAxisFactor { get; }
    /// <summary>Slopes beyond this are treated as this steep (no one takes a stance on a cliff).</summary>
    public double MaximumSlopeDegrees { get; }

    public static LieModel Default { get; } = new(new Dictionary<LieKind, LieResponse>
    {
        [LieKind.Tee] = LieResponse.None,
        [LieKind.Fairway] = LieResponse.None,
        [LieKind.Green] = LieResponse.None,
        [LieKind.Fringe] = new(0.99, 0.95, 0),
        [LieKind.Rough] = new(0.95, 0.70, 1.0),
        [LieKind.DeepRough] = new(0.80, 0.50, 3.0),
        [LieKind.Sand] = new(0.88, 0.70, 1.5),
    });

    public LieResponse ResponseFor(LieKind kind) => Responses.GetValueOrDefault(kind, LieResponse.None);

    /// <param name="clubLoftDegrees">The club's loft, for the sidehill face-tilt effect.</param>
    public LieAdjustment Apply(LaunchConditions measured, LieCondition lie, double clubLoftDegrees)
    {
        var r = ResponseFor(lie.Kind);
        double up = Math.Clamp(lie.UphillDegrees, -MaximumSlopeDegrees, MaximumSlopeDegrees);
        double side = Math.Clamp(lie.BallAboveFeetDegrees, -MaximumSlopeDegrees, MaximumSlopeDegrees);

        double speedFactor = r.BallSpeedFactor;
        double spinFactor = r.SpinFactor * Math.Max(0.5, 1 + SlopeSpinPerDegree * up);
        double launchChange = r.LaunchChangeDegrees + SlopeLaunchFactor * up;

        // Face turned by the tilted lie: left (negative) for a right-hander with the ball above the feet.
        double loft = Math.Clamp(clubLoftDegrees, 0, 70) * UnitConversion.DegreesToRadians;
        double faceTurn = Math.Atan(Math.Tan(loft) * Math.Sin(side * UnitConversion.DegreesToRadians)) * UnitConversion.RadiansToDegrees;
        if (!lie.LeftHanded) faceTurn = -faceTurn;
        double directionChange = SidehillStartFactor * faceTurn;
        double axisChange = SidehillAxisFactor * faceTurn;

        const double d = UnitConversion.DegreesToRadians;
        var sources = new Dictionary<LaunchParameter, ValueSource>(measured.Sources);
        void Mark(LaunchParameter p, bool changed) { if (changed) sources[p] = ValueSource.Adjusted; }
        Mark(LaunchParameter.BallSpeed, speedFactor != 1);
        Mark(LaunchParameter.TotalSpin, spinFactor != 1);
        Mark(LaunchParameter.LaunchAngle, launchChange != 0);
        Mark(LaunchParameter.LaunchDirection, directionChange != 0);
        Mark(LaunchParameter.SpinAxis, axisChange != 0);

        var launch = measured with
        {
            BallSpeed = measured.BallSpeed * speedFactor,
            TotalSpin = measured.TotalSpin * spinFactor,
            LaunchAngle = Math.Clamp(measured.LaunchAngle + launchChange * d, -89 * d, 89 * d),
            LaunchDirection = Math.Clamp(measured.LaunchDirection + directionChange * d, -89 * d, 89 * d),
            SpinAxis = Math.Clamp(measured.SpinAxis + axisChange * d, -89 * d, 89 * d),
            Sources = sources,
        };
        return new LieAdjustment(launch, lie, speedFactor, spinFactor, launchChange, directionChange, axisChange);
    }
}
