using SquareGspro.Core.Course;
using SquareGspro.Physics;

namespace SquareGspro.Core;

/// <summary>A point on the ball path. Metres: X downrange, Y height of the bottom of the ball, Z lateral (positive = right). T in seconds.</summary>
public readonly record struct FlightPoint(double T, double X, double Y, double Z);

public sealed record FlightResult(
    IReadOnlyList<FlightPoint> Points, int LandingIndex,
    double CarryYards, double TotalYards, double ApexYards, double LateralYards, double HangTimeSec, double DescentDeg);

/// <summary>
/// Range shots on flat ground through the physics engine (<see cref="SquareGspro.Physics"/>): aerodynamic flight,
/// bounce and roll on fairway, and putts on the green, with the current tuning (<see cref="ActivePhysics"/>: by
/// default standard air, no wind and a stimp-10 green). The range points along +Y, so the wind direction is relative
/// to the target line. Inputs use GSPro conventions (HLA positive = right, spin axis positive = curves right), which
/// are the engine's.
/// </summary>
public static class BallFlight
{
    private static readonly GolfPhysicsEngine Engine = new();

    public static FlightResult Simulate(double ballSpeedMph, double vlaDeg, double hlaDeg, double totalSpinRpm, double spinAxisDeg)
    {
        var r = Engine.SimulateShot(new SimulationRequest
        {
            Launch = LaunchConditions.FromDisplayUnits(ballSpeedMph, vlaDeg, hlaDeg, Math.Max(0, totalSpinRpm), spinAxisDeg),
            Terrain = new FlatTerrain(CourseGround.SurfaceFor(Lie.Fairway)),
            Ball = ActivePhysics.Ball,
            Environment = ActivePhysics.Environment,
        });
        return new FlightResult(r.Trajectory.Select(p => CourseGround.ToFlightPoint(p)).ToArray(), r.LandingIndex,
            r.CarryYards, r.TotalYards, r.ApexYards, r.FinalLateralYards, r.TimeOfFlight, r.LandingAngleDegrees);
    }

    /// <summary>A putt (or any ball struck along the ground) on a flat green. No air time, so <see cref="FlightResult.LandingIndex"/> is 0.</summary>
    public static FlightResult Putt(double ballSpeedMph, double hlaDeg)
    {
        double v = ballSpeedMph * UnitConversion.MphToMetresPerSecond;
        if (v <= 0) return new FlightResult(new[] { new FlightPoint(0, 0, 0, 0), new FlightPoint(0, 0, 0, 0) }, 0, 0, 0, 0, 0, 0, 0);
        var r = Engine.SimulatePutt(new PuttingRequest
        {
            Terrain = new FlatTerrain(CourseGround.Green), BallSpeed = v, DirectionDegrees = hlaDeg,
            Ball = ActivePhysics.Ball, Environment = ActivePhysics.Environment,
        });
        var pts = r.Trajectory.Select(p => CourseGround.ToFlightPoint(p)).ToList();
        if (pts.Count == 1) pts.Add(pts[0]);
        return new FlightResult(pts, 0, 0, r.Displacement * Units.MetresToYards, 0, r.Break * Units.MetresToYards, 0, 0);
    }
}
