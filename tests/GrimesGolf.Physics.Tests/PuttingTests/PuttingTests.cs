using Xunit;

namespace GrimesGolf.Physics.Tests;

public class PuttingTests
{
    private static readonly GolfPhysicsEngine Engine = new();

    private static PuttingResults Putt(ITerrainProvider green, double speed = 2.0, double direction = 0, double rollFraction = 1) =>
        Engine.SimulatePutt(new PuttingRequest { Terrain = green, BallSpeed = speed, DirectionDegrees = direction, InitialRollFraction = rollFraction });

    private static ITerrainProvider Flat(double stimp = 10) => new FlatTerrain(GreenModel.Surface(stimp));

    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(13)]
    public void StimpCalibrationReproducesTheReading(double stimp)
    {
        var green = GreenModel.Surface(stimp);
        Assert.Equal(stimp, GreenModel.MeasureStimp(green), 2);
        // Drag makes the calibrated resistance a little lower than the drag-free closed form.
        Assert.InRange(green.RollingResistance / GreenModel.RollingResistanceForStimp(stimp), 0.9, 1.0);
    }

    [Fact]
    public void FlatPuttRollsStraightAndStops()
    {
        var r = Putt(Flat());
        Assert.Equal(SimulationEnd.Rest, r.End);
        Assert.Equal(0, r.Break, 9);
        double mu = GreenModel.Surface(10).RollingResistance;
        double dragFree = 4.0 / (2 * mu * PhysicsConstants.Gravity);
        Assert.InRange(r.Displacement, dragFree * 0.93, dragFree);
        Assert.Equal(r.Displacement, r.DistanceTravelled, 6);
        Assert.InRange(r.InitialAcceleration, -mu * PhysicsConstants.Gravity * 1.15, -mu * PhysicsConstants.Gravity);
        Assert.True(r.TimeToStop > 2);
    }

    [Fact]
    public void FasterGreenRollsFurther()
    {
        Assert.True(Putt(Flat(13)).Displacement > Putt(Flat(9)).Displacement * 1.3);
    }

    [Fact]
    public void UphillShorterDownhillLonger()
    {
        double flat = Putt(Flat()).Displacement;
        double up = Putt(new InclinedPlaneTerrain(GreenModel.Surface(10), 0, 0.02)).Displacement;
        double down = Putt(new InclinedPlaneTerrain(GreenModel.Surface(10), 0, -0.02)).Displacement;
        Assert.True(up < flat * 0.85);
        Assert.True(down > flat * 1.2);
    }

    [Fact]
    public void SidehillPuttsBreakDownTheSlope()
    {
        var right = Putt(new InclinedPlaneTerrain(GreenModel.Surface(10), -0.02, 0));   // lower to the right
        var left = Putt(new InclinedPlaneTerrain(GreenModel.Surface(10), 0.02, 0));
        Assert.True(right.Break > 0.1, $"{right.Break}");
        Assert.Equal(right.Break, -left.Break, 6);
    }

    [Fact]
    public void DoubleBreakerBendsBothWays()
    {
        // Falls right for the first 2 m, then left.
        var green = new FunctionTerrain((x, y) => 0.025 * x * Math.Tanh((y - 2) / 0.3), GreenModel.Surface(10));
        var r = Putt(green, speed: 2.4);
        double furthestRight = r.Trajectory.Max(p => p.Position.X);
        Assert.True(furthestRight > 0.05, $"first breaks right: {furthestRight}");
        Assert.True(r.Break < -0.05, $"then left: {r.Break}");
        Assert.Equal(r.Break, r.MaximumBreak, 2);   // the final left break is the biggest
    }

    [Fact]
    public void ShortAndLongPutts()
    {
        var shortPutt = Putt(Flat(), speed: 0.6);
        var longPutt = Putt(Flat(), speed: 4.5);
        Assert.InRange(shortPutt.Displacement, 0.25, 0.45);
        Assert.InRange(longPutt.Displacement, 15, 22);
    }

    [Fact]
    public void SkiddingStartLosesDistance()
    {
        Assert.True(Putt(Flat(), rollFraction: 0).Displacement < Putt(Flat()).Displacement);
    }

    [Fact]
    public void GrainHelpsDownGrainAndSlowsIntoIt()
    {
        var grain = new FlatTerrain(GreenModel.Surface(10, grainDirection: Vector3D.UnitY, grainStrength: 0.1));
        double down = Putt(grain, direction: 0).Displacement;
        double into = Putt(grain, direction: 180).Displacement;
        Assert.True(down > into * 1.1);
        Assert.True(Putt(grain, direction: 90).Break < 0, "across the grain it drifts toward the grain direction (+Y = left of a putt heading +X)");
    }

    [Fact]
    public void VariableGreenSpeedAcrossTheSurface()
    {
        SurfaceProperties slow = GreenModel.Surface(8), fast = GreenModel.Surface(13);
        var slowThenFast = new FunctionTerrain((_, _) => 0, (x, y) => y < 1.5 ? slow : fast);
        double mixed = Putt(slowThenFast).Displacement;
        Assert.True(mixed > Putt(Flat(8)).Displacement && mixed < Putt(Flat(13)).Displacement);
    }

    [Fact]
    public void RejectsBadPutts()
    {
        Assert.Throws<SimulationValidationException>(() => Putt(Flat(), speed: -1));
        Assert.Throws<SimulationValidationException>(() => Putt(Flat(), speed: double.NaN));
    }
}
