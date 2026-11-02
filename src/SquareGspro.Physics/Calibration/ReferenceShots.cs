namespace SquareGspro.Physics;

/// <summary>
/// A measured shot to compare the simulation with: the launch as measured, the conditions, and the measured outcome
/// (any of which may be missing). Display units, as such data is usually published.
/// </summary>
public sealed record ReferenceShot
{
    public required string Name { get; init; }
    public required string Source { get; init; }
    public required double BallSpeedMph { get; init; }
    public required double LaunchAngleDegrees { get; init; }
    public double LaunchDirectionDegrees { get; init; }
    public required double TotalSpinRpm { get; init; }
    public double SpinAxisDegrees { get; init; }
    public EnvironmentConditions Environment { get; init; } = EnvironmentConditions.Standard;

    public double? CarryYards { get; init; }
    public double? TotalYards { get; init; }
    public double? ApexYards { get; init; }
    public double? FlightTimeSeconds { get; init; }
    public double? LandingAngleDegrees { get; init; }

    public LaunchConditions Launch =>
        LaunchConditions.FromDisplayUnits(BallSpeedMph, LaunchAngleDegrees, LaunchDirectionDegrees, TotalSpinRpm, SpinAxisDegrees);
}

/// <summary>Built-in reference data sets.</summary>
public static class ReferenceShots
{
    /// <summary>
    /// TrackMan PGA Tour averages (ball speed, launch, spin, carry, apex height, land angle) as widely published by
    /// TrackMan around 2014. CAUTION: these are averages over many players and shots, not individual measured shots;
    /// the air conditions are not stated (standard air assumed here); apex and land angle are TrackMan's radar
    /// measurements and carry is partly extrapolated by TrackMan's own model for long shots. Good for a first
    /// calibration, not a substitute for validation against individually measured shots. Verify the figures against
    /// the source before relying on them.
    /// </summary>
    public static IReadOnlyList<ReferenceShot> TrackManTourAverages2014 { get; } = Tour(new (string, double, double, double, double, double, double)[]
    {
        ("Driver", 167, 10.9, 2686, 275, 32, 37),
        ("3-wood", 158, 9.2, 3655, 243, 30, 43),
        ("5-wood", 152, 9.4, 4350, 230, 31, 47),
        ("Hybrid", 146, 10.2, 4437, 225, 29, 47),
        ("3-iron", 142, 10.4, 4630, 212, 27, 46),
        ("4-iron", 137, 11.0, 4836, 203, 28, 48),
        ("5-iron", 132, 12.1, 5361, 194, 31, 49),
        ("6-iron", 127, 14.1, 6231, 183, 30, 50),
        ("7-iron", 120, 16.3, 7097, 172, 32, 50),
        ("8-iron", 115, 18.1, 7998, 160, 31, 50),
        ("9-iron", 109, 20.4, 8647, 148, 30, 51),
        ("PW", 102, 24.2, 9304, 136, 29, 52),
    });

    private static ReferenceShot[] Tour((string Name, double Mph, double Launch, double Spin, double Carry, double Apex, double Land)[] rows) =>
        rows.Select(r => new ReferenceShot
        {
            Name = r.Name, Source = "TrackMan PGA Tour averages (published c. 2014)",
            BallSpeedMph = r.Mph, LaunchAngleDegrees = r.Launch, TotalSpinRpm = r.Spin,
            CarryYards = r.Carry, ApexYards = r.Apex, LandingAngleDegrees = r.Land,
        }).ToArray();

    /// <summary>Splits a data set into training (even positions) and validation (odd positions) shots.</summary>
    public static (IReadOnlyList<ReferenceShot> Training, IReadOnlyList<ReferenceShot> Validation) SplitAlternating(IReadOnlyList<ReferenceShot> shots) =>
        (shots.Where((_, i) => i % 2 == 0).ToArray(), shots.Where((_, i) => i % 2 == 1).ToArray());
}
