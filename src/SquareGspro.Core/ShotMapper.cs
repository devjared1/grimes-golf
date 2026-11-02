using SquareGspro.Core.GsPro;
using SquareGspro.Core.Square;

namespace SquareGspro.Core;

/// <summary>Converts Square (m/s, device sign conventions) to GSPro Open Connect (mph, GSPro conventions).</summary>
public static class ShotMapper
{
    public const double MsToMph = 2.2369362920544;

    public static GsProShot Map(ConnectorOptions o, int shotNumber, BallMetricsEvent ball, ClubMetricsEvent? club)
    {
        bool hasClub = club is { HasAnyData: true };
        var shot = new GsProShot
        {
            DeviceID = o.DeviceId,
            Units = "Yards",
            ShotNumber = shotNumber,
            APIversion = "1",
            BallData = new GsProBallData
            {
                Speed = Round(ball.BallSpeedMs * MsToMph),
                VLA = ball.LaunchDeg,
                HLA = ball.DirectionDeg,          // positive = right in both systems
                TotalSpin = ball.TotalSpinRpm,
                BackSpin = ball.BackSpinRpm,
                // Square reports negative = curves right; GSPro expects positive = curves right.
                SpinAxis = -ball.SpinAxisDeg,
                SideSpin = -ball.SideSpinRpm,
            },
            ShotDataOptions = new GsProShotOptions
            {
                ContainsBallData = true,
                ContainsClubData = hasClub,
                LaunchMonitorIsReady = true,
                LaunchMonitorBallDetected = true,
                IsHeartBeat = false,
            },
        };

        if (hasClub)
        {
            double clubMph = Round((club!.ClubSpeedMs ?? 0) * MsToMph);
            shot.ClubData = new GsProClubData
            {
                Speed = clubMph,
                SpeedAtImpact = clubMph,
                AngleOfAttack = club.AttackDeg ?? 0,
                FaceToTarget = club.FaceDeg ?? 0,
                Path = club.PathDeg ?? 0,
                Loft = club.DynamicLoftDeg ?? 0,
                // Impact location scale/sign is unverified on the Square side, so it is not forwarded.
            };
        }
        return shot;
    }

    /// <summary>A no-shot status update telling GSPro whether the monitor is ready / sees a ball.</summary>
    public static GsProShot Status(ConnectorOptions o, int shotNumber, bool ready, bool ballDetected) => new()
    {
        DeviceID = o.DeviceId,
        ShotNumber = shotNumber,
        ShotDataOptions = new GsProShotOptions
        {
            ContainsBallData = false,
            ContainsClubData = false,
            LaunchMonitorIsReady = ready,
            LaunchMonitorBallDetected = ballDetected,
            IsHeartBeat = false,
        },
    };

    private static double Round(double v) => Math.Round(v, 2);
}
