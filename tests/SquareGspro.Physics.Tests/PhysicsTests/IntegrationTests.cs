using Xunit;

namespace SquareGspro.Physics.Tests;

public class LaunchMonitorAdapterTests
{
    [Fact]
    public void BackspinAndSidespinDeriveTotalAndAxis()
    {
        var n = LaunchMonitorAdapter.NormalizeBallLaunch(new LaunchMonitorInput
        {
            BallSpeedMph = 150, LaunchAngleDegrees = 12, LaunchDirectionDegrees = 1, BackspinRpm = 3000, SidespinRpm = 500,
        });
        Assert.True(n.IsValid);
        var l = n.Launch!;
        Assert.Equal(Math.Sqrt(3000 * 3000 + 500 * 500), l.TotalSpinRpm, 6);
        Assert.Equal(Math.Atan2(500, 3000) * UnitConversion.RadiansToDegrees, l.SpinAxisDegrees, 6);
        Assert.Equal(ValueSource.Derived, l.Sources[LaunchParameter.TotalSpin]);
        Assert.Equal(ValueSource.Measured, l.Sources[LaunchParameter.BallSpeed]);
        Assert.Equal(150 * UnitConversion.MphToMetresPerSecond, l.BallSpeed, 12);
    }

    [Fact]
    public void MeasuredTotalAndAxisAreKeptAndDisagreementReported()
    {
        var n = LaunchMonitorAdapter.NormalizeBallLaunch(new LaunchMonitorInput
        {
            BallSpeedMph = 150, LaunchAngleDegrees = 12, LaunchDirectionDegrees = 0, TotalSpinRpm = 3000, SpinAxisDegrees = 10, BackspinRpm = 2000,
        });
        Assert.Equal(3000, n.Launch!.TotalSpinRpm, 9);
        Assert.Equal(10, n.Launch.SpinAxisDegrees, 9);
        Assert.Contains(n.Warnings, w => w.Contains("disagrees"));
    }

    [Fact]
    public void AssumedValuesAreMarkedEstimated()
    {
        var n = LaunchMonitorAdapter.NormalizeBallLaunch(new LaunchMonitorInput { BallSpeedMph = 150, LaunchAngleDegrees = 12, TotalSpinRpm = 3000 });
        Assert.True(n.IsValid);
        Assert.Equal(ValueSource.Estimated, n.Launch!.Sources[LaunchParameter.SpinAxis]);
        Assert.Equal(ValueSource.Estimated, n.Launch.Sources[LaunchParameter.LaunchDirection]);
        Assert.Equal(2, n.Warnings.Count);
    }

    [Theory]
    [InlineData(null, 12.0, 3000.0)]
    [InlineData(150.0, null, 3000.0)]
    [InlineData(150.0, 12.0, null)]
    [InlineData(-5.0, 12.0, 3000.0)]
    [InlineData(400.0, 12.0, 3000.0)]
    [InlineData(150.0, 12.0, double.NaN)]
    [InlineData(150.0, 12.0, 40000.0)]
    public void RejectsMissingOrImpossibleInput(double? speed, double? launch, double? spin)
    {
        var n = LaunchMonitorAdapter.NormalizeBallLaunch(new LaunchMonitorInput { BallSpeedMph = speed, LaunchAngleDegrees = launch, TotalSpinRpm = spin });
        Assert.False(n.IsValid);
        Assert.Null(n.Launch);
    }

    [Fact]
    public void ClubDeliveryNormalisation()
    {
        var n = LaunchMonitorAdapter.NormalizeClubDelivery(new LaunchMonitorInput { ClubSpeedMph = 100, FaceAngleDegrees = 2, ImpactToeMillimetres = 5 });
        Assert.True(n.IsValid);
        Assert.Equal(100 * UnitConversion.MphToMetresPerSecond, n.Club!.ClubSpeed, 12);
        Assert.Equal(0.005, n.Club.ImpactToeMetres, 12);
        Assert.Contains(n.Warnings, w => w.Contains("Club path"));
        Assert.False(LaunchMonitorAdapter.NormalizeClubDelivery(new LaunchMonitorInput { FaceAngleDegrees = 2 }).IsValid);
    }
}

public class EngineTests
{
    private static readonly LaunchMonitorInput Measured = new()
    {
        Device = "Test", BallSpeedMph = 165, LaunchAngleDegrees = 12.5, LaunchDirectionDegrees = 0, BackspinRpm = 2600, SpinAxisDegrees = 0,
    };

    [Fact]
    public void MeasuredLaunchIsFlownAsGivenAndInputPreserved()
    {
        var engine = new GolfPhysicsEngine();
        var r = engine.SimulateShot(new SimulationRequest { Input = Measured with { ClubSpeedMph = 112, FaceAngleDegrees = 3 } });
        Assert.Equal(165, r.Launch.BallSpeedMph, 9);
        Assert.Equal(12.5, r.Launch.LaunchAngleDegrees, 9);
        Assert.Null(r.Impact);
        Assert.Equal(3, r.Input!.FaceAngleDegrees);
        Assert.True(r.CarryYards > 200);
    }

    [Fact]
    public void ClubImpactModeCalculatesTheLaunch()
    {
        var engine = new GolfPhysicsEngine();
        var r = engine.SimulateShot(new SimulationRequest
        {
            InputMode = SimulationInputMode.ClubImpact, Club = ClubProperties.SevenIron,
            Input = new LaunchMonitorInput { ClubSpeedMph = 90, AttackAngleDegrees = -4.3, DynamicLoftDegrees = 23.1, ClubPathDegrees = 0, FaceAngleDegrees = 0 },
        });
        Assert.NotNull(r.Impact);
        Assert.Equal(ValueSource.Calculated, r.Launch.Sources[LaunchParameter.BallSpeed]);
        Assert.InRange(r.CarryYards, 150, 190);
        Assert.Contains(r.Diagnostics, d => d.Contains("Impact location"));
    }

    [Fact]
    public void AutoModePicksTheImpactModelWithoutBallData()
    {
        var r = new GolfPhysicsEngine().SimulateShot(new SimulationRequest
        {
            Club = ClubProperties.Driver,
            Input = new LaunchMonitorInput { ClubSpeedMph = 105, AttackAngleDegrees = 2, DynamicLoftDegrees = 14, ClubPathDegrees = 0, FaceAngleDegrees = 0, ImpactToeMillimetres = 0 },
        });
        Assert.NotNull(r.Impact);
    }

    [Fact]
    public void InvalidRequestsThrowWithEveryProblemAndAreLogged()
    {
        var log = new List<string>();
        var engine = new GolfPhysicsEngine(log.Add);
        var e = Assert.Throws<SimulationValidationException>(() => engine.SimulateShot(new SimulationRequest
        {
            Input = new LaunchMonitorInput { BallSpeedMph = 150, LaunchAngleDegrees = 12, TotalSpinRpm = 3000 },
            Options = SimulationOptions.Default with { TimeStepSeconds = -1 },
            Environment = EnvironmentConditions.Standard with { Atmosphere = new AtmosphericConditions { RelativeHumidityPercent = 150 } },
        }));
        Assert.Equal(2, e.Problems.Count);
        Assert.Equal(2, log.Count);
        Assert.Throws<SimulationValidationException>(() => engine.SimulateShot(new SimulationRequest()));
        Assert.Throws<SimulationValidationException>(() => engine.SimulateShot(new SimulationRequest
        {
            InputMode = SimulationInputMode.ClubImpact, Input = new LaunchMonitorInput { ClubSpeedMph = 100 },   // no club
        }));
    }

    [Fact]
    public void BatchMatchesSingleRunsInOrder()
    {
        var engine = new GolfPhysicsEngine();
        var requests = Enumerable.Range(0, 24).Select(i => new SimulationRequest
        {
            Launch = LaunchConditions.FromDisplayUnits(100 + 3 * i, 10 + i * 0.5, i % 5 - 2, 2500 + 200 * i, i % 7 - 3),
            Options = SimulationOptions.Default with { TrajectorySampleIntervalSeconds = 0.1 },
        }).ToArray();
        var batch = engine.SimulateBatch(requests);
        Assert.Equal(requests.Length, batch.Count);
        for (int i = 0; i < requests.Length; i++)
        {
            var single = engine.SimulateShot(requests[i]);
            Assert.Equal(single.FinalPosition, batch[i].FinalPosition);
            Assert.Equal(single.Trajectory, batch[i].Trajectory);
        }
    }

    [Fact]
    public void CalibrationProfileIsApplied()
    {
        var engine = new GolfPhysicsEngine();
        var plain = engine.SimulateShot(new SimulationRequest { Input = Measured });
        var lessDrag = engine.SimulateShot(new SimulationRequest { Input = Measured, Calibration = new CalibrationProfile { DragScale = 0.9 } });
        var bouncier = engine.SimulateShot(new SimulationRequest { Input = Measured, Calibration = new CalibrationProfile { RollingResistanceScale = 0.5 } });
        Assert.True(lessDrag.CarryDistance > plain.CarryDistance);
        Assert.Equal(plain.CarryDistance, bouncier.CarryDistance, 9);
        Assert.True(bouncier.RollDistance > plain.RollDistance);
        Assert.Throws<SimulationValidationException>(() => engine.SimulateShot(new SimulationRequest { Input = Measured, Calibration = new CalibrationProfile { LiftScale = 3 } }));
    }
}
