using Xunit;

namespace GrimesGolf.Physics.Tests;

public class FlightTests
{
    [Fact]
    public void ZeroWindStraightShotHasNoLateralDeviation()
    {
        var r = TestShots.Fly(TestShots.Driver());
        Assert.All(r.Trajectory, p => Assert.Equal(0, p.Position.X, 9));
        Assert.Equal(0, r.FinalLateral, 9);
        Assert.Equal(SimulationEnd.Rest, r.End);
    }

    [Fact]
    public void BackspinProducesLift()
    {
        var spin = TestShots.Carry(TestShots.SevenIron());
        var none = TestShots.Carry(LaunchConditions.FromDisplayUnits(120, 16.3, 0, 0, 0));
        Assert.True(spin.ApexHeight > none.ApexHeight + 10, $"{spin.ApexHeight} vs {none.ApexHeight}");
        Assert.True(spin.TimeOfFlight > none.TimeOfFlight);
    }

    [Fact]
    public void MoreBackspinChangesLift()
    {
        var low = TestShots.Carry(LaunchConditions.FromDisplayUnits(150, 12, 0, 2000, 0));
        var high = TestShots.Carry(LaunchConditions.FromDisplayUnits(150, 12, 0, 5000, 0));
        Assert.True(high.ApexHeight > low.ApexHeight);
        Assert.True(high.LandingAngleDegrees > low.LandingAngleDegrees);
    }

    [Fact]
    public void TiltedSpinAxisCurvesTheBall()
    {
        var fade = TestShots.Carry(TestShots.Driver(axisDeg: 15));
        var draw = TestShots.Carry(TestShots.Driver(axisDeg: -15));
        Assert.True(fade.Curvature > 10, $"fade curvature {fade.Curvature}");
        Assert.Equal(fade.Curvature, -draw.Curvature, 6);   // mirror images
        Assert.Equal(fade.CarryDistance, draw.CarryDistance, 6);
        Assert.True(fade.MaximumLateralDeviation > 0);
    }

    [Fact]
    public void LaunchDirectionStartsTheBallOffline()
    {
        var r = TestShots.Carry(TestShots.Driver(directionDeg: 3));
        Assert.Equal(r.CarryDistance * Math.Sin(3 * Math.PI / 180), r.CarryLateral, 2);
        Assert.Equal(0, r.Curvature, 6);   // straight along its own start line
    }

    [Fact]
    public void HeadwindAndTailwind()
    {
        var calm = TestShots.Carry(TestShots.Driver());
        var head = TestShots.Carry(TestShots.Driver(), TestShots.WithWind(Wind.Uniform(5, 0)));
        var tail = TestShots.Carry(TestShots.Driver(), TestShots.WithWind(Wind.Uniform(5, 180)));
        Assert.True(head.CarryDistance < calm.CarryDistance - 10);
        Assert.True(tail.CarryDistance > calm.CarryDistance + 5);
        Assert.True(head.ApexHeight > calm.ApexHeight, "headwind balloons the ball");
        Assert.Equal(0, head.CarryLateral, 9);
    }

    [Fact]
    public void CrosswindPushesTheBallDownwind()
    {
        var fromRight = TestShots.Carry(TestShots.Driver(), TestShots.WithWind(Wind.Uniform(5, 90)));
        var fromLeft = TestShots.Carry(TestShots.Driver(), TestShots.WithWind(Wind.Uniform(5, 270)));
        Assert.True(fromRight.CarryLateral < -5, $"{fromRight.CarryLateral}");
        Assert.Equal(fromRight.CarryLateral, -fromLeft.CarryLateral, 6);
    }

    [Fact]
    public void QuarteringAndProfiledWindAreHandled()
    {
        var quartering = TestShots.Carry(TestShots.Driver(), TestShots.WithWind(Wind.Uniform(5, 45)));
        Assert.True(quartering.CarryLateral < 0);
        var calm = TestShots.Carry(TestShots.Driver());
        Assert.True(quartering.CarryDistance < calm.CarryDistance);
        // The same 5 m/s reference wind matters more higher up, so a profile measured at 2 m hurts more than uniform 5 m/s.
        var profile = TestShots.Carry(TestShots.Driver(), TestShots.WithWind(new PowerLawWindProfile(5, 0, ReferenceHeightMetres: 2)));
        var uniform = TestShots.Carry(TestShots.Driver(), TestShots.WithWind(Wind.Uniform(5, 0)));
        Assert.True(profile.CarryDistance < uniform.CarryDistance);
    }

    [Fact]
    public void HighAltitudeFliesFurther()
    {
        var sea = TestShots.Carry(TestShots.Driver());
        var denver = TestShots.Carry(TestShots.Driver(), TestShots.WithAir(altitude: 1609));
        Assert.InRange(denver.CarryDistance / sea.CarryDistance, 1.04, 1.12);
    }

    [Fact]
    public void ColdAirFliesShorterThanHotAir()
    {
        var cold = TestShots.Carry(TestShots.Driver(), TestShots.WithAir(tempC: 0));
        var hot = TestShots.Carry(TestShots.Driver(), TestShots.WithAir(tempC: 35));
        Assert.True(cold.CarryDistance < hot.CarryDistance - 3);
    }

    [Fact]
    public void HumidAirFliesSlightlyFurther()
    {
        var dry = TestShots.Carry(TestShots.Driver(), TestShots.WithAir(tempC: 30, humidity: 0));
        var humid = TestShots.Carry(TestShots.Driver(), TestShots.WithAir(tempC: 30, humidity: 100));
        Assert.InRange(humid.CarryDistance - dry.CarryDistance, 0.1, 3);
    }

    [Fact]
    public void BallLaunchedHorizontallyFromATeeFallsUnderGravity()
    {
        var r = TestShots.Fly(LaunchConditions.FromDisplayUnits(20, 0, 0, 0, 0),
                              options: SimulationOptions.Default with { TeeHeightMetres = 0.05, EnableBounce = false });
        double fall = Math.Sqrt(2 * 0.05 / PhysicsConstants.Gravity);
        Assert.InRange(r.TimeOfFlight, fall * 0.99, fall * 1.03);
        Assert.InRange(r.CarryDistance, 20 * UnitConversion.MphToMetresPerSecond * fall * 0.95, 20 * UnitConversion.MphToMetresPerSecond * fall);
    }

    [Fact]
    public void BallLaunchedVerticallyComesStraightBack()
    {
        var r = TestShots.Fly(LaunchConditions.FromDisplayUnits(60, 90, 0, 0, 0), options: SimulationOptions.Default with { EnableBounce = false });
        double v = 60 * UnitConversion.MphToMetresPerSecond;
        Assert.True(r.CarryDistance < 1e-6);
        Assert.True(r.ApexHeight < v * v / (2 * PhysicsConstants.Gravity), "drag lowers the apex below the vacuum value");
        Assert.True(r.ApexHeight > 0.5 * v * v / (2 * PhysicsConstants.Gravity));
    }

    [Fact]
    public void SpinDecaysDuringFlight()
    {
        var r = TestShots.Carry(TestShots.Driver());
        double start = r.Trajectory[0].SpinRate, end = r.Trajectory[r.LandingIndex].SpinRate;
        Assert.True(end < start);
        double perSecond = 1 - Math.Pow(end / start, 1 / r.TimeOfFlight);
        Assert.InRange(perSecond, 0.01, 0.06);   // provisional decay model: a few percent per second
    }

    [Fact]
    public void ResultsAreDeterministic()
    {
        var a = TestShots.Fly(TestShots.Driver(7), TestShots.WithWind(new GustingWind(Wind.Uniform(3, 30), 3, 6, 30)));
        var b = TestShots.Fly(TestShots.Driver(7), TestShots.WithWind(new GustingWind(Wind.Uniform(3, 30), 3, 6, 30)));
        Assert.Equal(a.FinalPosition, b.FinalPosition);
        Assert.Equal(a.Trajectory, b.Trajectory);
    }

    [Fact]
    public void SamplingIntervalDoesNotChangeThePhysics()
    {
        var sparse = TestShots.Fly(TestShots.Driver(4), options: SimulationOptions.Default with { TrajectorySampleIntervalSeconds = 0.25 });
        var every = TestShots.Fly(TestShots.Driver(4), options: SimulationOptions.Default with { TrajectorySampleIntervalSeconds = 0 });
        Assert.Equal(sparse.FinalPosition, every.FinalPosition);
        Assert.Equal(sparse.CarryDistance, every.CarryDistance);
        Assert.True(every.Trajectory.Count > 10 * sparse.Trajectory.Count);
        // Samples are about 0.25 s apart (events add extra points), ending at the final position.
        Assert.Equal(sparse.FinalPosition, sparse.Trajectory[^1].Position);
        Assert.Equal(sparse.LandingPosition, sparse.Trajectory[sparse.LandingIndex].Position);
        for (int i = 1; i < sparse.Trajectory.Count; i++) Assert.True(sparse.Trajectory[i].Time - sparse.Trajectory[i - 1].Time <= 0.25 + 1e-9);
    }

    [Fact]
    public void AimHeadingRotatesTheWholeShot()
    {
        var straight = TestShots.Fly(TestShots.Driver(8));
        var turned = TestShots.Fly(TestShots.Driver(8), options: SimulationOptions.Default with { AimHeadingDegrees = 90, StartX = 10, StartY = -5 });
        Assert.Equal(straight.CarryDistance, turned.CarryDistance, 6);
        Assert.Equal(straight.TotalDistance, turned.TotalDistance, 6);
        Assert.Equal(straight.FinalLateral, turned.FinalLateral, 6);
        Assert.Equal(10 + straight.FinalPosition.Y, turned.FinalPosition.X, 6);   // forward is now +X
    }

    [Fact]
    public void FlightOnlyStopsAtLanding()
    {
        var r = TestShots.Fly(TestShots.Driver(), options: SimulationOptions.Default with { EnableBounce = false });
        Assert.Equal(SimulationEnd.Landed, r.End);
        Assert.Equal(r.CarryDistance, r.TotalDistance, 9);
        Assert.Single(r.GroundContacts);
    }

    [Fact]
    public void TimeLimitIsReported()
    {
        var r = TestShots.Fly(TestShots.Driver(), options: SimulationOptions.Default with { MaximumFlightTimeSeconds = 2 });
        Assert.Equal(SimulationEnd.TimeLimit, r.End);
        Assert.NotEmpty(r.Diagnostics);
    }

    private sealed class StopWhen : IGroundObserver
    {
        private readonly Func<BallState, GroundEventKind, bool> _rule;
        public StopWhen(Func<BallState, GroundEventKind, bool> rule) => _rule = rule;
        public int Calls { get; private set; }
        public bool ShouldStop(in BallState state, double time, GroundEventKind kind) { Calls++; return _rule(state, kind); }
    }

    [Fact]
    public void ObserverCanStopTheBallAtLandingOrWhileRolling()
    {
        var atLanding = new StopWhen((_, k) => k == GroundEventKind.Impact);
        var a = TestShots.Fly(TestShots.Driver(), options: SimulationOptions.Default with { Observer = atLanding });
        Assert.Equal(SimulationEnd.Stopped, a.End);
        Assert.Equal(a.LandingPosition, a.FinalPosition);
        Assert.Equal(1, atLanding.Calls);

        var free = TestShots.Fly(TestShots.Driver());
        double stopAt = free.GroundContacts.First(c => c.Kind == GroundContactKind.StartedRolling).Position.Y + 2;
        var b = TestShots.Fly(TestShots.Driver(), options: SimulationOptions.Default with { Observer = new StopWhen((s, k) => k == GroundEventKind.Moving && s.Position.Y >= stopAt) });
        Assert.Equal(SimulationEnd.Stopped, b.End);
        Assert.InRange(b.FinalPosition.Y, stopAt, stopAt + 0.05);
        Assert.Equal(free.CarryDistance, b.CarryDistance, 9);

        var putt = PuttingSimulator.Simulate(new PuttingRequest
        {
            Terrain = new FlatTerrain(Surfaces.Green), BallSpeed = 2, Observer = new StopWhen((s, _) => s.Position.Y >= 1),
        });
        Assert.Equal(SimulationEnd.Stopped, putt.End);
        Assert.InRange(putt.FinalPosition.Y, 1, 1.01);
    }

    [Fact]
    public void LandingOnRaisedGroundShortensCarry()
    {
        var flat = TestShots.Carry(TestShots.SevenIron());
        var raised = TestShots.Carry(TestShots.SevenIron(), terrain: new FunctionTerrain((x, y) => y > 100 ? 10 : 0, Surfaces.Fairway));
        Assert.True(raised.CarryDistance < flat.CarryDistance - 5);
        Assert.Equal(10, raised.LandingPosition.Z - BallProperties.Standard.RadiusMetres, 3);
    }
}
