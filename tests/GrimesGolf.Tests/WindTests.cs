using GrimesGolf.Core;
using GrimesGolf.Core.Course;
using Xunit;

namespace GrimesGolf.Tests;

public class WindTests
{
    private static readonly RangeShot SevenIron = new(120, 16.5, 0, 7000, 0, null, null);

    /// <summary>A 7-iron off the par 3's tee straight along the hole, in <paramref name="wind"/>.</summary>
    private static ShotOutcome HitInto(CourseWind wind)
    {
        var round = new HoleRound(TestCourse.Holes, rules: HouseRules.Strict);
        round.StartHole(1);
        round.Wind = wind;
        return round.Play(SevenIron, putt: false, round.DefaultAimHeading());
    }

    private static double Along(HoleRound r) => HoleRound.Heading(r.Hole.Tee, r.Hole.TeeAim);

    [Fact]
    public void SameRoundSameWind()
    {
        var a = CourseWind.ForHole(WindStrength.Breezy, 1234, 5);
        var b = CourseWind.ForHole(WindStrength.Breezy, 1234, 5);
        Assert.Equal(a, b);
        Assert.InRange(a.SpeedMph, 7, 13);
        Assert.True(CourseWind.ForHole(WindStrength.Calm, 1234, 5).IsCalm);
    }

    [Fact]
    public void HolesShareAPrevailingWind()
    {
        var winds = Enumerable.Range(0, 18).Select(h => CourseWind.ForHole(WindStrength.Windy, 99, h)).ToList();
        var first = winds[0].FromHeading;
        // Within ±25° of the round's prevailing direction, so never more than 50° apart.
        Assert.All(winds, w => Assert.InRange(Math.Abs(Math.IEEERemainder(w.FromHeading - first, 2 * Math.PI)), 0, 50 * Math.PI / 180 + 1e-9));
    }

    [Fact]
    public void HeadwindShortensAndTailwindLengthensCarry()
    {
        var round = new HoleRound(TestCourse.Holes);
        round.StartHole(1);
        double along = Along(round);
        double calm = HitInto(CourseWind.Calm).Flight.CarryYards;
        double into = HitInto(new CourseWind(15, along)).Flight.CarryYards;
        double helping = HitInto(new CourseWind(15, along + Math.PI)).Flight.CarryYards;
        Assert.True(into < calm - 5, $"into {into:0} vs calm {calm:0}");
        Assert.True(helping > calm + 3, $"helping {helping:0} vs calm {calm:0}");
    }

    [Fact]
    public void CrosswindFromTheRightPushesTheBallLeft()
    {
        var round = new HoleRound(TestCourse.Holes);
        round.StartHole(1);
        var o = HitInto(new CourseWind(15, Along(round) + Math.PI / 2));   // from the right
        Assert.True(o.Flight.LateralYards < -2, $"offline {o.Flight.LateralYards:0.0} yd");
    }

    [Fact]
    public void PlaysLikeAllowsForTheWind()
    {
        var round = new HoleRound(TestCourse.Holes);
        round.StartHole(1);
        double still = round.PlaysLike(round.Hole.Pin);
        double heading = HoleRound.Heading(round.Ball, round.Hole.Pin);
        round.Wind = new CourseWind(10, heading);
        Assert.Equal(still * 1.10, round.PlaysLike(round.Hole.Pin), 6);
        round.Wind = new CourseWind(10, heading + Math.PI);
        Assert.Equal(still * 0.95, round.PlaysLike(round.Hole.Pin), 6);
        Assert.Equal("10 mph, helping", round.Wind.Describe(heading));
        Assert.Equal("10 mph, from the left", new CourseWind(10, heading - Math.PI / 2).Describe(heading));
    }
}
