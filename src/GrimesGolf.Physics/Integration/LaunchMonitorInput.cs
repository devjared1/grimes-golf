namespace GrimesGolf.Physics;

/// <summary>
/// What a launch monitor reported, in the units monitors usually use. Null = not measured. Device adapters fill
/// this in, converting to these sign conventions: launch direction, club path and face angle positive = right;
/// spin axis and sidespin positive = ball curves right; attack angle positive = up; impact offsets toward the toe
/// and above the face centre positive.
/// </summary>
public sealed record LaunchMonitorInput
{
    /// <summary>Who measured it (device name), for provenance.</summary>
    public string? Device { get; init; }

    public double? BallSpeedMph { get; init; }
    public double? ClubSpeedMph { get; init; }
    public double? LaunchAngleDegrees { get; init; }
    public double? LaunchDirectionDegrees { get; init; }
    public double? BackspinRpm { get; init; }
    public double? SidespinRpm { get; init; }
    public double? TotalSpinRpm { get; init; }
    public double? SpinAxisDegrees { get; init; }
    public double? ClubPathDegrees { get; init; }
    public double? FaceAngleDegrees { get; init; }
    public double? AttackAngleDegrees { get; init; }
    public double? DynamicLoftDegrees { get; init; }
    public double? ImpactToeMillimetres { get; init; }
    public double? ImpactHeightMillimetres { get; init; }
    public bool LeftHanded { get; init; }

    public bool HasBallLaunch => BallSpeedMph is not null && LaunchAngleDegrees is not null &&
                                 (TotalSpinRpm is not null || BackspinRpm is not null);
}

/// <summary>Launch monitor input turned into engine inputs, with what was assumed and what was wrong.</summary>
public sealed record LaunchNormalization
{
    public required LaunchMonitorInput Original { get; init; }
    public LaunchConditions? Launch { get; init; }
    public ClubDelivery? Club { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Validates and normalises <see cref="LaunchMonitorInput"/> to SI. Measured values are never replaced: when the
/// spin is over-specified (total + axis and backspin + sidespin), total + axis are used and any disagreement is
/// reported. Values that have to be assumed are marked <see cref="ValueSource.Estimated"/> and reported as warnings.
/// </summary>
public static class LaunchMonitorAdapter
{
    /// <summary>Spin components disagreeing by more than this (rpm, or this fraction of total) are reported.</summary>
    public const double SpinConsistencyRpm = 150, SpinConsistencyFraction = 0.05;

    public static LaunchNormalization NormalizeBallLaunch(LaunchMonitorInput input)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var src = new Dictionary<LaunchParameter, ValueSource>();

        CheckFinite(input, errors);
        if (input.BallSpeedMph is not { } mph) errors.Add("Ball speed is required for a measured-launch simulation.");
        if (input.LaunchAngleDegrees is not { } vla) errors.Add("Launch angle is required for a measured-launch simulation.");
        double hla = 0;
        if (input.LaunchDirectionDegrees is { } h) { hla = h; src[LaunchParameter.LaunchDirection] = ValueSource.Measured; }
        else { src[LaunchParameter.LaunchDirection] = ValueSource.Estimated; warnings.Add("Launch direction not measured; assumed 0° (on the aim line)."); }

        double total = 0, axis = 0;
        var (back, side, tot, ax) = (input.BackspinRpm, input.SidespinRpm, input.TotalSpinRpm, input.SpinAxisDegrees);
        if (tot is { } t1 && ax is { } a1)
        {
            (total, axis) = (t1, a1);
            src[LaunchParameter.TotalSpin] = src[LaunchParameter.SpinAxis] = ValueSource.Measured;
            if (back is { } b && Math.Abs(t1 * Math.Cos(a1 * UnitConversion.DegreesToRadians) - b) > Tolerance(t1))
                warnings.Add($"Backspin {b:0} rpm disagrees with total spin {t1:0} rpm at axis {a1:0.#}°; using total spin and axis.");
            if (side is { } s && Math.Abs(t1 * Math.Sin(a1 * UnitConversion.DegreesToRadians) - s) > Tolerance(t1))
                warnings.Add($"Sidespin {s:0} rpm disagrees with total spin {t1:0} rpm at axis {a1:0.#}°; using total spin and axis.");
        }
        else if (back is { } b2 && side is { } s2)
        {
            total = Math.Sqrt(b2 * b2 + s2 * s2);
            axis = Math.Atan2(s2, b2) * UnitConversion.RadiansToDegrees;
            src[LaunchParameter.TotalSpin] = src[LaunchParameter.SpinAxis] = ValueSource.Derived;
            if (tot is { } t2 && Math.Abs(t2 - total) > Tolerance(t2))
                warnings.Add($"Total spin {t2:0} rpm disagrees with backspin and sidespin ({total:0} rpm); using backspin and sidespin.");
        }
        else if (back is { } b3 && ax is { } a3)
        {
            double c = Math.Cos(a3 * UnitConversion.DegreesToRadians);
            if (c < 0.05) errors.Add("Spin axis too close to ±90° to derive total spin from backspin.");
            else { total = b3 / c; axis = a3; }
            src[LaunchParameter.TotalSpin] = ValueSource.Derived;
            src[LaunchParameter.SpinAxis] = ValueSource.Measured;
        }
        else if (tot is { } t4 && side is { } s4)
        {
            if (t4 <= 0 || Math.Abs(s4) > t4) errors.Add($"Sidespin {s4:0} rpm cannot exceed total spin {t4:0} rpm.");
            else { total = t4; axis = Math.Asin(s4 / t4) * UnitConversion.RadiansToDegrees; }
            src[LaunchParameter.TotalSpin] = ValueSource.Measured;
            src[LaunchParameter.SpinAxis] = ValueSource.Derived;
        }
        else if ((tot ?? back) is { } only)
        {
            total = only;
            src[LaunchParameter.TotalSpin] = ValueSource.Measured;
            src[LaunchParameter.SpinAxis] = ValueSource.Estimated;
            warnings.Add($"Spin axis not measured; assumed 0° (pure backspin) with {(tot is null ? "backspin" : "total spin")} {only:0} rpm.");
        }
        else errors.Add("Spin is required for a measured-launch simulation (total spin or backspin).");

        if (errors.Count > 0) return new LaunchNormalization { Original = input, Errors = errors, Warnings = warnings };

        src[LaunchParameter.BallSpeed] = src[LaunchParameter.LaunchAngle] = ValueSource.Measured;
        var launch = new LaunchConditions
        {
            BallSpeed = input.BallSpeedMph!.Value * UnitConversion.MphToMetresPerSecond,
            LaunchAngle = input.LaunchAngleDegrees!.Value * UnitConversion.DegreesToRadians,
            LaunchDirection = hla * UnitConversion.DegreesToRadians,
            TotalSpin = total * UnitConversion.RpmToRadiansPerSecond,
            SpinAxis = axis * UnitConversion.DegreesToRadians,
            Sources = src,
        };
        errors.AddRange(launch.Validate());
        return new LaunchNormalization { Original = input, Launch = errors.Count == 0 ? launch : null, Errors = errors, Warnings = warnings };
    }

    public static LaunchNormalization NormalizeClubDelivery(LaunchMonitorInput input)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        CheckFinite(input, errors);
        if (input.ClubSpeedMph is null) errors.Add("Club speed is required for a club-impact simulation.");
        double Or0(double? v, string what)
        {
            if (v is { } x) return x;
            warnings.Add($"{what} not measured; assumed 0°.");
            return 0;
        }
        double path = Or0(input.ClubPathDegrees, "Club path"), face = Or0(input.FaceAngleDegrees, "Face angle"),
               attack = Or0(input.AttackAngleDegrees, "Attack angle");
        if (input.ImpactToeMillimetres is null && input.ImpactHeightMillimetres is null) warnings.Add("Impact location not measured; assumed the face centre.");
        if (errors.Count > 0) return new LaunchNormalization { Original = input, Errors = errors, Warnings = warnings };

        var club = new ClubDelivery
        {
            ClubSpeed = input.ClubSpeedMph!.Value * UnitConversion.MphToMetresPerSecond,
            PathDegrees = path, FaceAngleDegrees = face, AttackAngleDegrees = attack,
            DynamicLoftDegrees = input.DynamicLoftDegrees,
            ImpactToeMetres = (input.ImpactToeMillimetres ?? 0) / 1000, ImpactHeightMetres = (input.ImpactHeightMillimetres ?? 0) / 1000,
            LeftHanded = input.LeftHanded,
        };
        errors.AddRange(club.Validate());
        return new LaunchNormalization { Original = input, Club = errors.Count == 0 ? club : null, Errors = errors, Warnings = warnings };
    }

    private static double Tolerance(double total) => Math.Max(SpinConsistencyRpm, SpinConsistencyFraction * Math.Abs(total));

    private static void CheckFinite(LaunchMonitorInput i, List<string> errors)
    {
        foreach (var (name, v) in new (string, double?)[]
        {
            ("Ball speed", i.BallSpeedMph), ("Club speed", i.ClubSpeedMph), ("Launch angle", i.LaunchAngleDegrees),
            ("Launch direction", i.LaunchDirectionDegrees), ("Backspin", i.BackspinRpm), ("Sidespin", i.SidespinRpm),
            ("Total spin", i.TotalSpinRpm), ("Spin axis", i.SpinAxisDegrees), ("Club path", i.ClubPathDegrees),
            ("Face angle", i.FaceAngleDegrees), ("Attack angle", i.AttackAngleDegrees), ("Dynamic loft", i.DynamicLoftDegrees),
            ("Impact toe offset", i.ImpactToeMillimetres), ("Impact height offset", i.ImpactHeightMillimetres),
        })
            if (v is { } x && !double.IsFinite(x)) errors.Add($"{name} is not a number.");
        if (i.TotalSpinRpm < 0) errors.Add("Total spin cannot be negative.");
        if (i.BallSpeedMph <= 0) errors.Add("Ball speed must be positive.");
        if (i.ClubSpeedMph <= 0) errors.Add("Club speed must be positive.");
    }
}
