namespace SquareGspro.Physics;

/// <summary>Where a value came from.</summary>
public enum ValueSource
{
    /// <summary>Reported by the launch monitor.</summary>
    Measured,
    /// <summary>Computed exactly from measured values (e.g. spin axis from backspin and sidespin).</summary>
    Derived,
    /// <summary>Calculated by the club impact model.</summary>
    Calculated,
    /// <summary>Assumed, because nothing better was available (always reported as a warning).</summary>
    Estimated,
    /// <summary>A measured value changed by a game model (e.g. <see cref="LieModel"/> for the ball's lie and stance).</summary>
    Adjusted,
}

public enum LaunchParameter
{
    BallSpeed, LaunchAngle, LaunchDirection, TotalSpin, SpinAxis, Backspin, Sidespin, RifleSpin,
}

/// <summary>
/// Initial ball conditions in SI (m/s, radians, rad/s), relative to the aim line: launch direction and spin axis are
/// positive to the right. Spin axis is the tilt of the spin axis from horizontal: positive tilts it so the ball curves
/// right. Backspin = total × cos(axis), sidespin = total × sin(axis) (positive = curves right).
/// </summary>
public sealed record LaunchConditions
{
    public required double BallSpeed { get; init; }
    public required double LaunchAngle { get; init; }
    public required double LaunchDirection { get; init; }
    /// <summary>Spin perpendicular to the launch direction (rad/s).</summary>
    public required double TotalSpin { get; init; }
    public required double SpinAxis { get; init; }
    /// <summary>Spin about the direction of travel ("rifle" spin), rad/s, right-hand rule. Launch monitors don't report it; the impact model does.</summary>
    public double RifleSpin { get; init; }

    public IReadOnlyDictionary<LaunchParameter, ValueSource> Sources { get; init; } = new Dictionary<LaunchParameter, ValueSource>();

    public double Backspin => TotalSpin * Math.Cos(SpinAxis);
    public double Sidespin => TotalSpin * Math.Sin(SpinAxis);

    public double BallSpeedMph => BallSpeed * UnitConversion.MetresPerSecondToMph;
    public double LaunchAngleDegrees => LaunchAngle * UnitConversion.RadiansToDegrees;
    public double LaunchDirectionDegrees => LaunchDirection * UnitConversion.RadiansToDegrees;
    public double TotalSpinRpm => TotalSpin * UnitConversion.RadiansPerSecondToRpm;
    public double SpinAxisDegrees => SpinAxis * UnitConversion.RadiansToDegrees;
    public double BackspinRpm => Backspin * UnitConversion.RadiansPerSecondToRpm;
    public double SidespinRpm => Sidespin * UnitConversion.RadiansPerSecondToRpm;

    /// <summary>Builds launch conditions from display units, all marked <paramref name="source"/>.</summary>
    public static LaunchConditions FromDisplayUnits(double ballSpeedMph, double launchAngleDeg, double launchDirectionDeg,
                                                    double totalSpinRpm, double spinAxisDeg, ValueSource source = ValueSource.Measured) => new()
    {
        BallSpeed = ballSpeedMph * UnitConversion.MphToMetresPerSecond,
        LaunchAngle = launchAngleDeg * UnitConversion.DegreesToRadians,
        LaunchDirection = launchDirectionDeg * UnitConversion.DegreesToRadians,
        TotalSpin = totalSpinRpm * UnitConversion.RpmToRadiansPerSecond,
        SpinAxis = spinAxisDeg * UnitConversion.DegreesToRadians,
        Sources = new Dictionary<LaunchParameter, ValueSource>
        {
            [LaunchParameter.BallSpeed] = source, [LaunchParameter.LaunchAngle] = source, [LaunchParameter.LaunchDirection] = source,
            [LaunchParameter.TotalSpin] = source, [LaunchParameter.SpinAxis] = source,
        },
    };

    /// <summary>Unit vector of the initial velocity in the aim frame (aim line = +Y).</summary>
    public Vector3D Direction => new(
        Math.Cos(LaunchAngle) * Math.Sin(LaunchDirection), Math.Cos(LaunchAngle) * Math.Cos(LaunchDirection), Math.Sin(LaunchAngle));

    /// <summary>
    /// Angular velocity vector in the aim frame. Pure backspin points along b = d̂ × Z (for a ball going +Y that is
    /// +X, which by ω × v gives lift upward); the spin axis tilts it about the direction of travel toward −u, u = b × d̂,
    /// which makes positive axis curve right.
    /// </summary>
    public Vector3D SpinVector
    {
        get
        {
            var d = Direction;
            var b = Vector3D.Cross(d, Vector3D.UnitZ).Normalized();
            if (b == Vector3D.Zero) b = Vector3D.UnitX;   // launched straight up: backspin about +X by convention
            var u = Vector3D.Cross(b, d);
            return (b * Math.Cos(SpinAxis) - u * Math.Sin(SpinAxis)) * TotalSpin + d * RifleSpin;
        }
    }

    /// <summary>Inverse of <see cref="SpinVector"/>: splits a spin vector into total (perpendicular), axis and rifle spin for a direction of travel.</summary>
    public static (double Total, double Axis, double Rifle) DecomposeSpin(in Vector3D spin, in Vector3D direction)
    {
        var d = direction.Normalized();
        var b = Vector3D.Cross(d, Vector3D.UnitZ).Normalized();
        if (b == Vector3D.Zero) b = Vector3D.UnitX;
        var u = Vector3D.Cross(b, d);
        double rifle = Vector3D.Dot(spin, d);
        double sb = Vector3D.Dot(spin, b), su = Vector3D.Dot(spin, u);
        return (Math.Sqrt(sb * sb + su * su), Math.Atan2(-su, sb), rifle);
    }

    public IEnumerable<string> Validate()
    {
        if (!(BallSpeed is > 0 and <= 110)) yield return $"Ball speed {BallSpeedMph:0.#} mph is outside 0-246 mph.";
        if (!(Math.Abs(LaunchAngleDegrees) <= 90)) yield return $"Launch angle {LaunchAngleDegrees:0.#}° is outside ±90°.";
        if (!(Math.Abs(LaunchDirectionDegrees) <= 90)) yield return $"Launch direction {LaunchDirectionDegrees:0.#}° is outside ±90°.";
        if (!(TotalSpinRpm is >= 0 and <= 20000)) yield return $"Total spin {TotalSpinRpm:0} rpm is outside 0-20000 rpm.";
        if (!(Math.Abs(SpinAxisDegrees) <= 90)) yield return $"Spin axis {SpinAxisDegrees:0.#}° is outside ±90°.";
        if (!double.IsFinite(RifleSpin)) yield return "Rifle spin is not a number.";
    }
}
