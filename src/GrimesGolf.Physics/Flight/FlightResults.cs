namespace GrimesGolf.Physics;

public enum BallPhase { Flight, Bounce, Sliding, Rolling, Rest }

/// <summary>A sample of the ball's path (world frame, SI). <see cref="SpinAxis"/> is the unit angular-velocity direction.</summary>
public readonly record struct TrajectoryPoint(double Time, Vector3D Position, Vector3D Velocity, double SpinRate, Vector3D SpinAxis, BallPhase Phase)
{
    public double Speed => Velocity.Length;
    public double SpinRateRpm => SpinRate * UnitConversion.RadiansPerSecondToRpm;
}

public enum GroundContactKind { Landing, Bounce, StartedRolling, CameToRest }

/// <summary>A moment where the ball met or settled on the ground.</summary>
public sealed record GroundContactEvent(
    GroundContactKind Kind, double Time, Vector3D Position, string Surface,
    double NormalSpeedIn, double NormalSpeedOut, double Restitution, double CraterAngleDegrees, bool Slid);

public enum SimulationEnd
{
    /// <summary>The ball came to rest.</summary>
    Rest,
    /// <summary>Bounce disabled: stopped at the first landing.</summary>
    Landed,
    /// <summary>Roll disabled: stopped when the ball stopped bouncing.</summary>
    StoppedBouncing,
    /// <summary>Hit the flight or ground time limit (see diagnostics).</summary>
    TimeLimit,
    /// <summary>An <see cref="IGroundObserver"/> stopped the ball (e.g. it reached a hazard or the hole).</summary>
    Stopped,
}

public enum GroundEventKind
{
    /// <summary>The ball has just met the ground from the air (state is the incoming state, before the bounce).</summary>
    Impact,
    /// <summary>The ball has just moved one ground step (sliding or rolling).</summary>
    Moving,
}

/// <summary>
/// Watches the ball on the ground and can stop it, so callers can apply rules the physics doesn't know about
/// (water, out of bounds, the cup) at the exact step they happen.
/// </summary>
public interface IGroundObserver
{
    /// <summary>Return true to stop the simulation with the ball where it is.</summary>
    bool ShouldStop(in BallState state, double time, GroundEventKind kind);
}

/// <summary>
/// The result of a shot. Distances are metres, horizontal, from the start position; "lateral" is signed distance
/// right (+) / left (−) of the aim line. Use the <c>…Yards</c> helpers or <see cref="UnitConversion"/> for display.
/// </summary>
public sealed record FlightResults
{
    public required LaunchConditions Launch { get; init; }
    /// <summary>The launch monitor data the shot came from, unchanged (null if launch conditions were given directly).</summary>
    public LaunchMonitorInput? Input { get; init; }
    /// <summary>The impact model's result, when the launch was calculated from club delivery.</summary>
    public ImpactResult? Impact { get; init; }
    public required IReadOnlyList<TrajectoryPoint> Trajectory { get; init; }
    /// <summary>Index in <see cref="Trajectory"/> of the first landing point.</summary>
    public int LandingIndex { get; init; }

    public Vector3D StartPosition { get; init; }
    public double AimHeadingDegrees { get; init; }

    public double CarryDistance { get; init; }
    public double TotalDistance { get; init; }
    /// <summary>Highest point of the flight above the launch point's ball height.</summary>
    public double ApexHeight { get; init; }
    public double TimeOfFlight { get; init; }
    /// <summary>Descent angle at the first landing, below horizontal (degrees).</summary>
    public double LandingAngleDegrees { get; init; }
    public double LandingSpeed { get; init; }
    public Vector3D LandingPosition { get; init; }
    public Vector3D LandingVelocity { get; init; }
    /// <summary>Horizontal distance from the first landing to the final position.</summary>
    public double RollDistance { get; init; }
    public Vector3D FinalPosition { get; init; }

    public double CarryLateral { get; init; }
    public double FinalLateral { get; init; }
    /// <summary>Largest |lateral| during the flight (signed).</summary>
    public double MaximumLateralDeviation { get; init; }
    /// <summary>
    /// Sideways bend of the flight: lateral distance of the landing point from the straight line of the initial launch
    /// direction (positive = curved right).
    /// </summary>
    public double Curvature { get; init; }

    /// <summary>Number of ground impacts after which the ball left the ground again.</summary>
    public int BounceCount { get; init; }
    public IReadOnlyList<GroundContactEvent> GroundContacts { get; init; } = Array.Empty<GroundContactEvent>();
    public double TotalTime { get; init; }
    public SimulationEnd End { get; init; }
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public double CarryYards => CarryDistance * UnitConversion.MetresToYards;
    public double TotalYards => TotalDistance * UnitConversion.MetresToYards;
    public double ApexYards => ApexHeight * UnitConversion.MetresToYards;
    public double ApexFeet => ApexHeight * UnitConversion.MetresToFeet;
    public double CarryLateralYards => CarryLateral * UnitConversion.MetresToYards;
    public double FinalLateralYards => FinalLateral * UnitConversion.MetresToYards;
    public double RollYards => RollDistance * UnitConversion.MetresToYards;
}
