using Xunit;

namespace GrimesGolf.Physics.Tests;

public class CoefficientModelTests
{
    private static TabulatedAerodynamicModel Simple() => new("test", new[] { 1e5, 2e5 }, new[] { 0.0, 0.2 },
        new[,] { { 0.30, 0.34 }, { 0.20, 0.24 } }, new[,] { { 0.0, 0.2 }, { 0.0, 0.3 } }, 0.01);

    [Fact]
    public void InterpolatesBilinearlyAndClampsAtEdges()
    {
        var m = Simple();
        Assert.Equal(0.30, m.Evaluate(1e5, 0).Drag, 12);
        Assert.Equal((0.30 + 0.34 + 0.20 + 0.24) / 4, m.Evaluate(1.5e5, 0.1).Drag, 12);
        Assert.Equal(0.25, m.Evaluate(1.5e5, 0.2).Lift, 12);
        Assert.Equal(m.Evaluate(2e5, 0.2).Drag, m.Evaluate(9e5, 5).Drag);   // tables clamp, no extrapolation
        Assert.Equal(m.Evaluate(2e5, 0.2).Lift, m.Evaluate(9e5, 5).Lift);
        Assert.Equal(m.Evaluate(1e5, 0).Drag, m.Evaluate(1, -1).Drag);
        Assert.Equal(0.001, m.Evaluate(1e5, 0.1).SpinMoment, 12);
    }

    [Fact]
    public void RejectsBadTables()
    {
        Assert.Throws<ArgumentException>(() => new TabulatedAerodynamicModel("bad", new[] { 2e5, 1e5 }, new[] { 0.0, 0.2 },
            new double[2, 2] { { 0.3, 0.3 }, { 0.3, 0.3 } }, new double[2, 2], 0.01));
        Assert.Throws<ArgumentException>(() => new TabulatedAerodynamicModel("bad", new[] { 1e5, 2e5 }, new[] { 0.0, 0.2 },
            new double[2, 2] { { 0.3, -0.1 }, { 0.3, 0.3 } }, new double[2, 2], 0.01));
    }

    [Fact]
    public void DefaultModelHasPublishedTrends()
    {
        var m = DefaultAerodynamics.Provisional;
        Assert.Empty(CalibrationService.CheckPhysicalRange(m));
        Assert.True(m.Evaluate(4e4, 0.1).Drag > m.Evaluate(1.5e5, 0.1).Drag, "drag crisis: higher drag below critical Re");
        Assert.True(m.Evaluate(1.5e5, 0.25).Drag > m.Evaluate(1.5e5, 0.05).Drag, "drag rises with spin");
        Assert.True(m.Evaluate(1.5e5, 0.25).Lift > m.Evaluate(1.5e5, 0.1).Lift, "lift rises with spin");
        Assert.Equal(0, m.Evaluate(1.5e5, 0).Lift, 12);
    }

    [Fact]
    public void ScaledModelScalesAndTilts()
    {
        var inner = DefaultAerodynamics.Provisional;
        var s = new ScaledAerodynamicModel(inner, 0.9, 1.1, 2, dragReynoldsSlope: 0.2);
        var a = inner.Evaluate(1.5e5, 0.1);
        var b = s.Evaluate(1.5e5, 0.1);   // at the pivot the slope has no effect
        Assert.Equal(a.Drag * 0.9, b.Drag, 12);
        Assert.Equal(a.Lift * 1.1, b.Lift, 12);
        Assert.Equal(a.SpinMoment * 2, b.SpinMoment, 12);
        Assert.True(s.Evaluate(2.5e5, 0.1).Drag / inner.Evaluate(2.5e5, 0.1).Drag > 0.9);
    }
}

public class AerodynamicForceTests
{
    private static readonly AirProperties Air = AirProperties.Standard;
    private static readonly BallProperties Ball = BallProperties.Standard;

    [Fact]
    public void DragOpposesRelativeAirflow()
    {
        var v = new Vector3D(3, 60, 10);
        var a = AerodynamicForces.Evaluate(Ball, Air, v, Vector3D.Zero);
        Assert.Equal(-1, Vector3D.Dot(a.DragAcceleration.Normalized(), v.Normalized()), 9);
        Assert.Equal(Vector3D.Zero, a.LiftAcceleration);
        double expected = 0.5 * Air.Density * a.Coefficients.Drag * Ball.CrossSectionAreaSquareMetres * v.LengthSquared / Ball.MassKg;
        Assert.Equal(expected, a.DragAcceleration.Length, 9);
    }

    [Fact]
    public void BackspinLiftsAndLiftIsPerpendicular()
    {
        var v = new Vector3D(0, 60, 0);
        var w = new Vector3D(300, 0, 0);   // backspin
        var a = AerodynamicForces.Evaluate(Ball, Air, v, w);
        Assert.True(a.LiftAcceleration.Z > 0);
        Assert.Equal(0, Vector3D.Dot(a.LiftAcceleration, v), 9);
        Assert.Equal(0, Vector3D.Dot(a.LiftAcceleration, w), 9);
        Assert.Equal(Ball.RadiusMetres * 300 / 60, a.SpinParameter, 12);
    }

    [Fact]
    public void TiltedAxisLiftsSideways()
    {
        var l = LaunchConditions.FromDisplayUnits(150, 0, 0, 3000, 20);
        var a = AerodynamicForces.Evaluate(Ball, Air, l.Direction * l.BallSpeed, l.SpinVector);
        Assert.True(a.LiftAcceleration.X > 0, "positive axis pushes right");
        Assert.Equal(Math.Tan(20 * Math.PI / 180), a.LiftAcceleration.X / a.LiftAcceleration.Z, 9);
    }

    [Fact]
    public void SpinDecayOpposesSpin()
    {
        var w = new Vector3D(300, 0, 40);
        var a = AerodynamicForces.Evaluate(Ball, Air, new Vector3D(0, 60, 0), w);
        Assert.Equal(-1, Vector3D.Dot(a.AngularAcceleration.Normalized(), w.Normalized()), 9);
    }

    [Fact]
    public void ReynoldsNumberAtDriverSpeed()
    {
        var a = AerodynamicForces.Evaluate(Ball, Air, new Vector3D(0, 75, 0), Vector3D.Zero);
        Assert.InRange(a.Reynolds, 2.1e5, 2.3e5);
    }
}

public class AtmosphereTests
{
    [Fact]
    public void StandardAtmosphere()
    {
        var a = AirProperties.Standard;
        Assert.Equal(PhysicsConstants.StandardAirDensity, a.Density, 3);
        Assert.Equal(1.789e-5, a.DynamicViscosity, 7);
        Assert.Equal(PhysicsConstants.StandardPressurePa, a.PressurePa, 6);
    }

    [Fact]
    public void AltitudeLowersDensityThroughPressure()
    {
        double p = AtmosphericModel.StationPressure(101325, 1609);   // Denver
        Assert.InRange(p, 83000, 84000);
        var high = AtmosphericModel.Evaluate(new AtmosphericConditions { AltitudeMetres = 1609 });
        Assert.InRange(high.Density / PhysicsConstants.StandardAirDensity, 0.81, 0.84);
    }

    [Fact]
    public void StationPressureOverridesAltitude()
    {
        var a = AtmosphericModel.Evaluate(new AtmosphericConditions { AltitudeMetres = 3000, StationPressurePa = 101325 });
        Assert.Equal(PhysicsConstants.StandardAirDensity, a.Density, 3);
    }

    [Fact]
    public void TemperatureAndHumidity()
    {
        var cold = AtmosphericModel.Evaluate(new AtmosphericConditions { TemperatureCelsius = 0 });
        var hot = AtmosphericModel.Evaluate(new AtmosphericConditions { TemperatureCelsius = 35 });
        Assert.True(cold.Density > hot.Density);
        var dry = AtmosphericModel.Evaluate(new AtmosphericConditions { TemperatureCelsius = 30 });
        var humid = AtmosphericModel.Evaluate(new AtmosphericConditions { TemperatureCelsius = 30, RelativeHumidityPercent = 100 });
        Assert.True(humid.Density < dry.Density, "water vapour is lighter than dry air");
        Assert.InRange((dry.Density - humid.Density) / dry.Density, 0.01, 0.02);
        Assert.InRange(AtmosphericModel.SaturationVapourPressure(30), 4240, 4250);   // Buck: ≈4.245 kPa at 30 °C
    }

    [Fact]
    public void RejectsImpossibleConditions()
    {
        Assert.NotEmpty(new AtmosphericConditions { RelativeHumidityPercent = 120 }.Validate());
        Assert.NotEmpty(new AtmosphericConditions { TemperatureCelsius = 90 }.Validate());
        Assert.Empty(AtmosphericConditions.Standard.Validate());
    }
}

public class WindTests
{
    [Fact]
    public void DirectionIsWhereTheWindComesFrom()
    {
        var head = Wind.FromDirection(10, 0);
        Assert.Equal(-10, head.Y, 12);
        Assert.Equal(0, head.X, 12);
        var fromRight = Wind.FromDirection(10, 90);
        Assert.Equal(-10, fromRight.X, 12);
        Assert.Equal(10, Wind.FromDirection(10, 180).Y, 12);
    }

    [Fact]
    public void PowerLawProfileGrowsWithHeight()
    {
        var w = new PowerLawWindProfile(5, 0, ReferenceHeightMetres: 10);
        Assert.Equal(5, w.SpeedAt(10), 12);
        Assert.True(w.SpeedAt(30) > w.SpeedAt(10));
        Assert.True(w.SpeedAt(2) < w.SpeedAt(10));
        Assert.Equal(w.SpeedAt(0.5), w.SpeedAt(-3), 12);
    }

    [Fact]
    public void TabulatedProfileInterpolates()
    {
        var w = new TabulatedWindProfile(new[] { new WindLayer(0, 2, 0), new WindLayer(20, 6, 0) });
        Assert.Equal(-4, w.VelocityAt(Vector3D.Zero, 10, 0).Y, 12);
        Assert.Equal(-6, w.VelocityAt(Vector3D.Zero, 50, 0).Y, 12);
        Assert.Equal(-2, w.VelocityAt(Vector3D.Zero, -1, 0).Y, 12);
    }

    [Fact]
    public void GustsAreDeterministicAndBounded()
    {
        var mean = Wind.Uniform(4, 0);
        var a = new GustingWind(mean, 4, 7, 0, seed: 3);
        var b = new GustingWind(mean, 4, 7, 0, seed: 3);
        for (double t = 0; t < 30; t += 0.37)
        {
            var va = a.VelocityAt(Vector3D.Zero, 10, t);
            Assert.Equal(va, b.VelocityAt(Vector3D.Zero, 10, t));
            Assert.InRange(-va.Y, 4 - 1e-9, 7 + 1e-9);   // headwind component between mean and gust
        }
        Assert.NotEqual(a.VelocityAt(Vector3D.Zero, 10, 1), new GustingWind(mean, 4, 7, 0, seed: 4).VelocityAt(Vector3D.Zero, 10, 1));
    }
}
