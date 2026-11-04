using Xunit;

namespace GrimesGolf.Physics.Tests;

public class VectorTests
{
    [Fact]
    public void DotAndCross()
    {
        var a = new Vector3D(1, 2, 3);
        var b = new Vector3D(-4, 5, 0.5);
        Assert.Equal(1 * -4 + 2 * 5 + 3 * 0.5, Vector3D.Dot(a, b), 12);
        Assert.Equal(Vector3D.UnitZ, Vector3D.Cross(Vector3D.UnitX, Vector3D.UnitY));
        var c = Vector3D.Cross(a, b);
        Assert.Equal(0, Vector3D.Dot(c, a), 10);
        Assert.Equal(0, Vector3D.Dot(c, b), 10);
    }

    [Fact]
    public void NormalizeAndProject()
    {
        Assert.Equal(1, new Vector3D(3, -4, 12).Normalized().Length, 12);
        Assert.Equal(Vector3D.Zero, Vector3D.Zero.Normalized());
        var p = new Vector3D(1, 2, 3).ProjectOnPlane(Vector3D.UnitZ);
        Assert.Equal(new Vector3D(1, 2, 0), p);
    }

    [Fact]
    public void HeadingRotationIsClockwiseFromY()
    {
        var r = Vector3D.UnitY.RotateHeading(Math.PI / 2);
        Assert.Equal(1, r.X, 12);
        Assert.Equal(0, r.Y, 12);
        var back = r.RotateHeading(-Math.PI / 2);
        Assert.Equal(1, back.Y, 12);
    }
}

public class UnitConversionTests
{
    [Fact]
    public void Conversions()
    {
        Assert.Equal(44.704, 100 * UnitConversion.MphToMetresPerSecond, 10);
        Assert.Equal(2 * Math.PI, 60 * UnitConversion.RpmToRadiansPerSecond, 12);
        Assert.Equal(91.44, 100 * UnitConversion.YardsToMetres, 10);
        Assert.Equal(1, UnitConversion.MetresToYards * UnitConversion.YardsToMetres, 12);
        Assert.Equal(100, UnitConversion.FahrenheitToCelsius(212), 10);
        Assert.Equal(Math.PI, 180 * UnitConversion.DegreesToRadians, 12);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(10, 15, -5)]
    [InlineData(-3, 40, 30)]
    [InlineData(25, 5, -60)]
    public void SpinVectorRoundTrips(double direction, double launch, double axis)
    {
        var l = LaunchConditions.FromDisplayUnits(150, launch, direction, 3000, axis);
        var w = l.SpinVector;
        Assert.Equal(l.TotalSpin, w.Length, 9);
        Assert.Equal(0, Vector3D.Dot(w, l.Direction), 9);   // no rifle spin
        var (total, ax, rifle) = LaunchConditions.DecomposeSpin(w, l.Direction);
        Assert.Equal(l.TotalSpin, total, 9);
        Assert.Equal(l.SpinAxis, ax, 9);
        Assert.Equal(0, rifle, 9);
    }

    [Fact]
    public void BackspinAndSidespinComponents()
    {
        var l = LaunchConditions.FromDisplayUnits(150, 12, 0, 3000, 30);
        Assert.Equal(3000 * Math.Cos(Math.PI / 6), l.BackspinRpm, 6);
        Assert.Equal(1500, l.SidespinRpm, 6);
    }

    [Fact]
    public void PureBackspinPointsRightForBallGoingForward()
    {
        // ω × v must give lift upward: ω = +X for v = +Y.
        var w = LaunchConditions.FromDisplayUnits(100, 0, 0, 3000, 0).SpinVector;
        Assert.True(w.X > 0);
        Assert.Equal(0, w.Y, 9);
        Assert.Equal(0, w.Z, 9);
    }
}

public class IntegratorTests
{
    /// <summary>x'' = −x along X (unit harmonic oscillator).</summary>
    private struct Oscillator : IStateDerivative
    {
        public BallState Evaluate(in BallState s, double time) =>
            new(s.Velocity, new Vector3D(-s.Position.X, 0, 0), Vector3D.Zero);
    }

    private static double Error(double dt, bool rk4)
    {
        var f = new Oscillator();
        var s = new BallState(new Vector3D(1, 0, 0), Vector3D.Zero, Vector3D.Zero);
        int steps = (int)Math.Round(2 * Math.PI / dt);
        double h = 2 * Math.PI / steps, t = 0;
        for (int i = 0; i < steps; i++, t += h)
            s = rk4 ? NumericalIntegrator.Rk4(ref f, s, t, h) : NumericalIntegrator.Euler(ref f, s, t, h);
        return Math.Abs(s.Position.X - 1) + Math.Abs(s.Velocity.X);
    }

    [Fact]
    public void Rk4IsAccurateAndFourthOrder()
    {
        double e1 = Error(0.02, true), e2 = Error(0.01, true);
        Assert.True(e1 < 1e-6, $"RK4 error {e1}");
        double order = Math.Log2(e1 / e2);
        Assert.InRange(order, 3.7, 4.3);
        Assert.True(Error(0.02, false) > 1000 * e1, "Euler should be far less accurate");
    }

    [Fact]
    public void VacuumProjectileMatchesAnalyticSolution()
    {
        double v = 40, angle = 35 * Math.PI / 180, g = PhysicsConstants.Gravity;
        var launch = new LaunchConditions { BallSpeed = v, LaunchAngle = angle, LaunchDirection = 0, TotalSpin = 0, SpinAxis = 0 };
        var r = TestShots.Fly(launch, ball: BallProperties.Standard with { Aerodynamics = VacuumAerodynamicModel.Instance },
                              options: SimulationOptions.Default with { EnableBounce = false });
        Assert.Equal(v * v * Math.Sin(2 * angle) / g, r.CarryDistance, 6);
        Assert.Equal(2 * v * Math.Sin(angle) / g, r.TimeOfFlight, 8);
        Assert.Equal(Math.Pow(v * Math.Sin(angle), 2) / (2 * g), r.ApexHeight, 3);
        Assert.Equal(35, r.LandingAngleDegrees, 6);
    }

    [Fact]
    public void FlightConvergesWithTimeStep()
    {
        var coarse = TestShots.Fly(TestShots.Driver(5), options: SimulationOptions.Default with { EnableBounce = false });
        var fine = TestShots.Fly(TestShots.Driver(5), options: SimulationOptions.Default with { EnableBounce = false, TimeStepSeconds = 1.0 / 1920 });
        Assert.True(Math.Abs(coarse.CarryDistance - fine.CarryDistance) < 0.01, $"{coarse.CarryDistance} vs {fine.CarryDistance}");
        Assert.True(Math.Abs(coarse.CarryLateral - fine.CarryLateral) < 0.01);
    }
}
