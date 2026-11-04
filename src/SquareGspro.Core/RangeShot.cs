using SquareGspro.Core.Square;

namespace SquareGspro.Core;

/// <summary>A club with GSPro code, typical launch numbers (used for demo shots) and typical static loft.</summary>
public sealed record ClubPreset(string Code, string Label, double BallMph, double LaunchDeg, double SpinRpm, double Smash, double LoftDeg);

public static class ClubPresets
{
    public static readonly IReadOnlyList<ClubPreset> All = new ClubPreset[]
    {
        new("DR", "Driver", 160, 11.5, 2700, 1.48, 10.5), new("W3", "3 Wood", 150, 10.5, 3600, 1.46, 15),
        new("W5", "5 Wood", 143, 12, 4300, 1.44, 18), new("H4", "4 Hybrid", 137, 13, 4700, 1.42, 22),
        new("I4", "4 Iron", 132, 13.5, 5000, 1.38, 22), new("I5", "5 Iron", 128, 14, 5500, 1.37, 25),
        new("I6", "6 Iron", 124, 15, 6200, 1.35, 28), new("I7", "7 Iron", 118, 16.5, 7000, 1.33, 32),
        new("I8", "8 Iron", 113, 18, 7800, 1.31, 36), new("I9", "9 Iron", 107, 20, 8600, 1.29, 40),
        new("PW", "Pitching Wedge", 100, 24, 9300, 1.25, 45), new("GW", "Gap Wedge", 92, 27, 9800, 1.22, 50),
        new("SW", "Sand Wedge", 84, 30, 10000, 1.20, 56), new("LW", "Lob Wedge", 74, 33, 10200, 1.18, 60),
        new("PT", "Putter", 5, 1, 0, 1.0, 3),
    };

    /// <summary>The preset for a club code, or null if unknown.</summary>
    public static ClubPreset? Find(string? code) => code != null && IndexOf(code) is var i and >= 0 ? All[i] : null;

    public static int IndexOf(string code)
    {
        for (int i = 0; i < All.Count; i++) if (All[i].Code.Equals(code, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}

/// <summary>Where a shot came from: the swing meter on screen, a launch monitor, a demo shot or a development tool.</summary>
public enum ShotSource { Screen, Monitor, Demo, Dev }

/// <summary>A shot in display (GSPro) conventions: mph, direction and spin axis positive = right.</summary>
public sealed record RangeShot(
    double BallMph, double LaunchDeg, double DirectionDeg, double SpinRpm, double SpinAxisDeg,
    double? ClubMph, double? Smash, bool IsDemo = false)
{
    /// <summary>Where it came from (online play only takes shots from a launch monitor).</summary>
    public ShotSource Source { get; init; } = IsDemo ? ShotSource.Demo : ShotSource.Screen;

    /// <summary>Every number is a real number (not NaN or infinite), as anything sent over a network must be checked for.</summary>
    public bool IsFinite => double.IsFinite(BallMph) && double.IsFinite(LaunchDeg) && double.IsFinite(DirectionDeg) && double.IsFinite(SpinRpm)
                            && double.IsFinite(SpinAxisDeg) && (ClubMph is null || double.IsFinite(ClubMph.Value)) && (Smash is null || double.IsFinite(Smash.Value));

    public static RangeShot FromSquare(BallMetricsEvent ball, ClubMetricsEvent? club)
    {
        double mph = ball.BallSpeedMs * ShotMapper.MsToMph;
        double? clubMph = club?.ClubSpeedMs * ShotMapper.MsToMph;
        double? smash = club?.Smash ?? (clubMph > 0 ? mph / clubMph : null);
        // Square reports spin axis negative = curves right; flip to the display convention.
        return new RangeShot(mph, ball.LaunchDeg, ball.DirectionDeg, ball.TotalSpinRpm, -ball.SpinAxisDeg, clubMph, smash) { Source = ShotSource.Monitor };
    }

    /// <summary>A made-up shot around the club's typical numbers.</summary>
    public static RangeShot Demo(ClubPreset c, Random rng)
    {
        double Jitter(double v, double pct) => v * (1 + (rng.NextDouble() * 2 - 1) * pct);
        double mph = Jitter(c.BallMph, 0.04);
        return new RangeShot(mph, Jitter(c.LaunchDeg, 0.1), (rng.NextDouble() * 2 - 1) * 4, Jitter(c.SpinRpm, 0.1),
                             (rng.NextDouble() * 2 - 1) * 12, mph / c.Smash, c.Smash, IsDemo: true);
    }

    public FlightResult Fly() => BallFlight.Simulate(BallMph, LaunchDeg, DirectionDeg, SpinRpm, SpinAxisDeg);

    /// <summary>Formats a signed left/right value as "L 2.1" / "R 2.1".</summary>
    public static string Side(double v, string fmt = "0.0") =>
        Math.Abs(v) < 0.05 ? 0.0.ToString(fmt) : (v > 0 ? "R " : "L ") + Math.Abs(v).ToString(fmt);
}
