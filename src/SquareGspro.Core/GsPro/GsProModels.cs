namespace SquareGspro.Core.GsPro;

// Property names intentionally match the GSPro Open Connect v1 JSON exactly.

public sealed class GsProShot
{
    public string DeviceID { get; set; } = "";
    public string Units { get; set; } = "Yards";
    public int ShotNumber { get; set; }
    public string APIversion { get; set; } = "1";
    public GsProBallData BallData { get; set; } = new();
    public GsProClubData ClubData { get; set; } = new();
    public GsProShotOptions ShotDataOptions { get; set; } = new();
}

public sealed class GsProBallData
{
    public double Speed { get; set; }        // mph
    public double SpinAxis { get; set; }     // deg, positive = curves right
    public double TotalSpin { get; set; }    // rpm
    public double BackSpin { get; set; }     // rpm
    public double SideSpin { get; set; }     // rpm
    public double HLA { get; set; }          // deg, positive = right of target
    public double VLA { get; set; }          // deg
    public double CarryDistance { get; set; }
}

public sealed class GsProClubData
{
    public double Speed { get; set; }        // mph
    public double AngleOfAttack { get; set; }
    public double FaceToTarget { get; set; }
    public double Lie { get; set; }
    public double Loft { get; set; }
    public double Path { get; set; }
    public double SpeedAtImpact { get; set; }
    public double VerticalFaceImpact { get; set; }
    public double HorizontalFaceImpact { get; set; }
    public double ClosureRate { get; set; }
}

public sealed class GsProShotOptions
{
    public bool ContainsBallData { get; set; }
    public bool ContainsClubData { get; set; }
    public bool LaunchMonitorIsReady { get; set; }
    public bool LaunchMonitorBallDetected { get; set; }
    public bool IsHeartBeat { get; set; }
}

public sealed class GsProResponse
{
    public int Code { get; set; }
    public string? Message { get; set; }
    public GsProPlayer? Player { get; set; }
}

public sealed class GsProPlayer
{
    public string? Handed { get; set; }   // "RH" / "LH"
    public string? Club { get; set; }     // "DR", "I7", "PT", ...
}
