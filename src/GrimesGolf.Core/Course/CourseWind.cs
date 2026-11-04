using GrimesGolf.Physics;

namespace GrimesGolf.Core.Course;

/// <summary>How windy course rounds are (a setting): the wind on each hole is drawn from this range.</summary>
public enum WindStrength { Calm, Light, Breezy, Windy }

/// <summary>
/// The wind on a hole: its speed (measured 10 m up, like a weather report), the course heading it blows from
/// (radians, as <see cref="HoleRound.Heading"/>: 0 = +X, positive = right) and how much it gusts on top.
/// <para>
/// A round has one prevailing wind (from its seed) that shifts a little from hole to hole, so the same round always
/// has the same wind, after a restart too.
/// </para>
/// </summary>
public readonly record struct CourseWind(double SpeedMph, double FromHeading, double GustMph = 0)
{
    public static readonly CourseWind Calm = new(0, 0);

    public bool IsCalm => SpeedMph < 0.5 && GustMph < 0.5;

    /// <summary>Speed range (mph) and gusts on top, for a strength.</summary>
    private static (double Min, double Max, double Gust) Range(WindStrength s) => s switch
    {
        WindStrength.Light => (2, 7, 2),
        WindStrength.Breezy => (7, 13, 4),
        WindStrength.Windy => (13, 21, 7),
        _ => (0, 0, 0),
    };

    /// <summary>The wind on hole <paramref name="holeIndex"/> of the round with <paramref name="seed"/>.</summary>
    public static CourseWind ForHole(WindStrength strength, int seed, int holeIndex)
    {
        if (strength == WindStrength.Calm) return Calm;
        var (min, max, gust) = Range(strength);
        var round = new Random(seed);
        double prevailing = round.NextDouble() * 2 * Math.PI;
        var hole = new Random(unchecked(seed * 31 + holeIndex * 7919 + 17));
        double from = prevailing + (hole.NextDouble() - 0.5) * (50 * Math.PI / 180);   // ±25° from hole to hole
        double speed = min + (max - min) * hole.NextDouble();
        return new CourseWind(Math.Round(speed), Math.IEEERemainder(from, 2 * Math.PI), gust);
    }

    /// <summary>The air for the physics engine: stronger with height (open grass), gusting over a few seconds.</summary>
    public IWindField Field()
    {
        if (IsCalm) return Wind.Calm;
        double ms = SpeedMph * UnitConversion.MphToMetresPerSecond, fromDeg = FromHeading * 180 / Math.PI;
        IWindField wind = new PowerLawWindProfile(ms, fromDeg);
        return GustMph > 0 ? new GustingWind(wind, ms, ms + GustMph * UnitConversion.MphToMetresPerSecond, fromDeg) : wind;
    }

    /// <summary>Wind against a shot along <paramref name="heading"/> (mph): positive into it, negative helping.</summary>
    public double Against(double heading) => SpeedMph * Math.Cos(FromHeading - heading);

    /// <summary>Wind across a shot along <paramref name="heading"/> (mph): positive from the right, negative from the left.</summary>
    public double Across(double heading) => SpeedMph * Math.Sin(FromHeading - heading);

    /// <summary>
    /// How much longer (above 1) or shorter a full shot along <paramref name="heading"/> plays, by the usual rule of
    /// thumb: about 1% per mph into the wind and half that downwind.
    /// </summary>
    public double PlaysLikeFactor(double heading)
    {
        double into = Against(heading);
        return 1 + (into >= 0 ? 0.01 * into : 0.005 * into);
    }

    /// <summary>"12 mph, into and from the left" for a shot along <paramref name="heading"/> ("Calm" when there's none).</summary>
    public string Describe(double heading)
    {
        if (IsCalm) return "Calm";
        double into = Against(heading), across = Across(heading);
        string along = Math.Abs(into) < SpeedMph * 0.38 ? "" : into > 0 ? "into" : "helping";
        string side = Math.Abs(across) < SpeedMph * 0.38 ? "" : across > 0 ? "from the right" : "from the left";
        string how = along.Length > 0 && side.Length > 0 ? $"{along}, {side}" : along.Length > 0 ? along : side;
        return $"{SpeedMph:0} mph{(how.Length > 0 ? ", " + how : "")}";
    }
}
