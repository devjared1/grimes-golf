using GrimesGolf.Core.Course;
using GrimesGolf.Core.Square;
using GrimesGolf.Physics;

namespace GrimesGolf.Core.Monitors;

/// <summary>Square shots in the device-neutral form (<see cref="LaunchMonitorInput"/>).</summary>
public static class SquareShots
{
    public const string Device = "Square Golf";

    /// <summary>
    /// A Square shot: m/s to mph, and the spin axis and sidespin flipped from the device's sign (negative = curves
    /// right) to the input's (positive = curves right). Converted back by <see cref="RangeShot.FromLaunchMonitor"/>,
    /// it is the same shot <see cref="RangeShot.FromSquare"/> makes.
    /// </summary>
    /// <remarks>
    /// Club path and face angle are passed on as the Square reports them; their signs (and the left-handed case) are
    /// still to be confirmed against real shots (ROADMAP: "Verify ... left-handed face/path signs"). Nothing uses
    /// them yet. Impact location is left out until its axes are known.
    /// </remarks>
    public static MonitorShot ToMonitorShot(BallMetricsEvent ball, ClubMetricsEvent? club, bool leftHanded = false, string device = Device) => new(
        new LaunchMonitorInput
        {
            Device = device,
            BallSpeedMph = ball.BallSpeedMs * Units.MsToMph,
            LaunchAngleDegrees = ball.LaunchDeg,
            LaunchDirectionDegrees = ball.DirectionDeg,
            TotalSpinRpm = ball.TotalSpinRpm,
            SpinAxisDegrees = -ball.SpinAxisDeg,
            BackspinRpm = ball.BackSpinRpm,
            SidespinRpm = -ball.SideSpinRpm,
            ClubSpeedMph = club?.ClubSpeedMs * Units.MsToMph,
            ClubPathDegrees = club?.PathDeg,
            FaceAngleDegrees = club?.FaceDeg,
            AttackAngleDegrees = club?.AttackDeg,
            DynamicLoftDegrees = club?.DynamicLoftDeg,
            LeftHanded = leftHanded,
        },
        ReportedSmash: club?.Smash);
}
