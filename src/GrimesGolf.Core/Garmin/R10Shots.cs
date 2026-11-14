using GrimesGolf.Core.Course;
using GrimesGolf.Core.Monitors;
using GrimesGolf.Physics;

namespace GrimesGolf.Core.Garmin;

/// <summary>R10 shots in the device-neutral form (<see cref="LaunchMonitorInput"/>).</summary>
public static class R10Shots
{
    public const string Device = "Garmin Approach R10";

    /// <summary>
    /// An R10 shot: m/s to mph, and the spin axis flipped from the R10's sign (positive = tilted left, the ball
    /// curving left) to the input's (positive = curving right). Launch direction, club path and face angle are
    /// already positive = right. The R10 reports total spin and axis, not backspin and sidespin, so those stay null.
    /// What it didn't measure stays null too, for the adapter to mark as estimated: club angles all exactly 0 mean
    /// it didn't track the club (a slow swing), and with no spin there's no axis either.
    /// </summary>
    public static MonitorShot ToMonitorShot(R10Metrics m, bool leftHanded = false)
    {
        var notes = new List<string>();
        var b = m.Ball;
        double? totalSpin = b?.TotalSpin;
        double? axis = totalSpin > 0 && b?.SpinAxis is { } a ? -a : null;
        if (b?.SpinSource is { } src && src != R10SpinSource.Measured && totalSpin > 0)
            notes.Add(src switch
            {
                R10SpinSource.Ratio => "Spin estimated by the R10 (from launch and speed, not read off the ball)",
                R10SpinSource.BallFlight => "Spin estimated by the R10 from the ball's flight",
                _ => "Spin estimated by the R10",
            });

        var c = m.Club;
        bool tracked = c is { } k && !(k.Face == 0 && k.Path == 0 && k.AttackAngle == 0);
        if (c != null && !tracked) notes.Add("Club angles not tracked (a slow swing reads all zeros)");
        double? clubMph = c?.HeadSpeed > 0 ? c.HeadSpeed * Units.MsToMph : null;

        var input = new LaunchMonitorInput
        {
            Device = Device,
            BallSpeedMph = b?.BallSpeed * Units.MsToMph,
            LaunchAngleDegrees = b?.LaunchAngle,
            LaunchDirectionDegrees = b?.LaunchDirection,
            TotalSpinRpm = totalSpin,
            SpinAxisDegrees = axis,
            ClubSpeedMph = clubMph,
            ClubPathDegrees = tracked ? c!.Path : null,
            FaceAngleDegrees = tracked ? c!.Face : null,
            AttackAngleDegrees = tracked ? c!.AttackAngle : null,
            LeftHanded = leftHanded,
        };
        return new MonitorShot(input, null, notes);
    }

    /// <summary>A shot as the R10 would send it (for the fake R10): the reverse of <see cref="ToMonitorShot"/>.</summary>
    public static R10Metrics FromRangeShot(RangeShot shot, uint shotId) => new(
        shotId, 1,
        new R10Ball((float)shot.LaunchDeg, (float)shot.DirectionDeg, (float)(shot.BallMph / Units.MsToMph), (float)-shot.SpinAxisDeg,
                    (float)shot.SpinRpm, R10SpinSource.Ratio, 1),
        shot.ClubMph is { } club ? new R10Club((float)(club / Units.MsToMph), 0.5f, 1.5f, -2f) : null);
}
