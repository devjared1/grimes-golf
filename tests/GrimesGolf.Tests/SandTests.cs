using GrimesGolf.Core;
using GrimesGolf.Core.Course;
using GrimesGolf.Physics;
using Xunit;

namespace GrimesGolf.Tests;

/// <summary>A ball reaching a bunker runs into the sand as far as its pace takes it, but can't roll out again.</summary>
public class SandTests
{
    private static double Yd(double yards) => yards * Units.YardsToMetres;

    /// <summary>On the par 3's green, just above its front-left bunker (at 152, −15 yd), rolled toward it.</summary>
    private static ShotOutcome RollTowardTheBunker(double metresPerSecond)
    {
        var round = new HoleRound(TestCourse.Holes, rules: HouseRules.Strict with { GimmeMetres = 0 });
        round.StartHole(1);
        var from = new P2(Yd(152), Yd(-6));
        Assert.Equal(Lie.Green, round.Hole.LieAt(from));
        round.Restore(new HoleRound.State(1, from, Lie.Green, 1, false, null));
        return round.Play(new RangeShot(metresPerSecond * Units.MsToMph, 1, 0, 0, 0, null, null), putt: true, headingRad: -Math.PI / 2);
    }

    /// <summary>How far the ball went on from where it first reached the sand (m).</summary>
    private static double IntoTheSand(ShotOutcome o, Hole hole)
    {
        var entry = o.Flight.Points.First(p => hole.LieAt(new P2(p.X, p.Z)) == Lie.Bunker);
        return new P2(entry.X, entry.Z).DistanceTo(o.Rest);
    }

    [Fact]
    public void ABallRollingIntoABunkerRunsOnIntoTheSand()
    {
        var o = RollTowardTheBunker(5);
        Assert.Equal(Lie.Bunker, o.RestLie);
        var hole = TestCourse.Holes[1];
        double gentle = IntoTheSand(o, hole);
        Assert.True(gentle > 0.1, $"stopped {gentle:0.00} m into the sand");

        // Faster in, further in.
        double firmer = IntoTheSand(RollTowardTheBunker(6.5), hole);
        Assert.True(firmer > gentle, $"{firmer:0.00} m vs {gentle:0.00} m");
    }

    [Fact]
    public void ABallCantRollOutOfABunker()
    {
        var o = RollTowardTheBunker(15);   // fast enough to cross it
        Assert.Equal(Lie.Bunker, o.RestLie);
        Assert.Equal(Lie.Bunker, TestCourse.Holes[1].LieAt(o.Rest));
    }

    [Fact]
    public void ABallLandingInSandHopsAndRunsALittleUnlessItComesInSteep()
    {
        var sand = new FlatTerrain(Surfaces.Sand);
        FlightResults Fly(double mph, double launch, double spin) => BallFlightSimulator.Simulate(
            LaunchConditions.FromDisplayUnits(mph, launch, 0, spin, 0), BallProperties.Standard, EnvironmentConditions.Standard, sand, SimulationOptions.Default);
        var driver = Fly(160, 11.5, 2700);
        Assert.True(driver.BounceCount >= 1, "a long, shallower landing hops");
        Assert.InRange(driver.RollDistance, 0.3, 5);
        var wedge = Fly(100, 24, 9300);   // steep and spinning: buries near its pitch mark
        Assert.InRange(wedge.RollDistance, 0, 1.5);
        Assert.True(wedge.RollDistance < driver.RollDistance);
    }

    [Fact]
    public void ATuningSavedAtTheOldSandDefaultsPlaysTheNewOnes()
    {
        string json = """{ "name": "Mine", "values": { "ground.sand.bounce": 0.15, "ground.sand.crater": 35, "ground.sand.rolling": 1, "ground.sand.drag": 0, "ground.rough.rolling": 1.1 } }""";
        var t = PhysicsTuning.FromJson(json);
        Assert.Equal(Surfaces.SandBounce, t["ground.sand.bounce"]);
        Assert.Equal(Surfaces.SandCrater, t["ground.sand.crater"]);
        Assert.Equal(Surfaces.SandRolling, t["ground.sand.rolling"]);
        Assert.Equal(Surfaces.SandDrag, t["ground.sand.drag"]);
        Assert.Equal(1.1, t["ground.rough.rolling"]);   // anything else is kept

        var tuned = PhysicsTuning.FromJson("""{ "values": { "ground.sand.bounce": 0.3 } }""");
        Assert.Equal(0.3, tuned["ground.sand.bounce"]);
    }
}
