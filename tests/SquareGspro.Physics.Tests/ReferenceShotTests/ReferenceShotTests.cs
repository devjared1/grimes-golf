using Xunit;
using Xunit.Abstractions;

namespace SquareGspro.Physics.Tests;

/// <summary>
/// The engine against the TrackMan tour-average data set, and a regression baseline. NOTE: the default lift-curve
/// shape was chosen after comparing against this same data set, so these are not independent validation; they show
/// the engine is in the right range and catch regressions.
/// </summary>
public class ReferenceShotTests
{
    private readonly ITestOutputHelper _out;
    public ReferenceShotTests(ITestOutputHelper output) => _out = output;

    private static readonly CalibrationService Service = new();

    [Fact]
    public void DefaultModelIsWithinToleranceOfTourAverages()
    {
        var c = Service.Compare(ReferenceShots.TrackManTourAverages2014, CalibrationProfile.None);
        foreach (var x in c)
            _out.WriteLine($"{x.Shot.Name,-7} carry {x.Simulated.CarryYards,6:0.0} ({x.CarryError,5:+0.0;-0.0}) apex {x.Simulated.ApexYards,5:0.0} ({x.ApexError,5:+0.0;-0.0}) land {x.Simulated.LandingAngleDegrees,5:0.0} ({x.LandingAngleError,5:+0.0;-0.0})");
        var e = CalibrationService.Errors(c);
        _out.WriteLine(e.ToString());
        Assert.True(e.CarryRmsYards < 7, e.ToString());
        Assert.True(e.ApexRmsYards < 4, e.ToString());
        Assert.True(e.LandingAngleRmsDegrees < 4, e.ToString());
        Assert.All(c, x => Assert.InRange(x.CarryError!.Value, -18, 10));
    }

    /// <summary>
    /// Regression baseline: carry, apex, landing angle and total for fixed shots with the default models. If a change
    /// moves these, the test fails; update the numbers only when the change in behaviour is intended (and say why).
    /// </summary>
    [Theory]
    [MemberData(nameof(Baseline))]
    public void RegressionBaseline(string name, double carry, double apex, double landing, double total, double lateral)
    {
        var shot = RegressionShots.Single(s => s.Name == name);
        var r = TestShots.Fly(shot.Launch, terrain: TestShots.Fairway);
        _out.WriteLine($"{{ \"{name}\", {r.CarryYards:0.00}, {r.ApexYards:0.00}, {r.LandingAngleDegrees:0.00}, {r.TotalYards:0.00}, {r.FinalLateralYards:0.00} }},");
        const double tolerance = 0.05;
        Assert.InRange(r.CarryYards, carry - tolerance, carry + tolerance);
        Assert.InRange(r.ApexYards, apex - tolerance, apex + tolerance);
        Assert.InRange(r.LandingAngleDegrees, landing - tolerance, landing + tolerance);
        Assert.InRange(r.TotalYards, total - tolerance, total + tolerance);
        Assert.InRange(r.FinalLateralYards, lateral - tolerance, lateral + tolerance);
    }

    private static readonly ReferenceShot[] RegressionShots =
    {
        new() { Name = "Driver", Source = "baseline", BallSpeedMph = 167, LaunchAngleDegrees = 10.9, TotalSpinRpm = 2686 },
        new() { Name = "Driver fade", Source = "baseline", BallSpeedMph = 160, LaunchAngleDegrees = 12, LaunchDirectionDegrees = -2, TotalSpinRpm = 3000, SpinAxisDegrees = 12 },
        new() { Name = "7-iron draw", Source = "baseline", BallSpeedMph = 120, LaunchAngleDegrees = 16.3, LaunchDirectionDegrees = 1.5, TotalSpinRpm = 7097, SpinAxisDegrees = -8 },
        new() { Name = "PW", Source = "baseline", BallSpeedMph = 102, LaunchAngleDegrees = 24.2, TotalSpinRpm = 9304 },
        new() { Name = "Chip", Source = "baseline", BallSpeedMph = 40, LaunchAngleDegrees = 30, TotalSpinRpm = 4500 },
    };

    public static TheoryData<string, double, double, double, double, double> Baseline => new()
    {
        // name, carry yd, apex yd, landing °, total yd, final lateral yd (default models). Re-recorded 2026-11-01
        // when fairway turf got tangential restitution 0.1 (totals moved by under 0.5 yd), and again the same day when
        // fairway landings got skid (ImpactSkidSpeed 7): full shots hop forward before the turf grabs them, so totals
        // grew by 1-5 yd (most for the PW and chip, which used to be grabbed on their first touch); flights unchanged.
        { "Driver", 257.17, 31.36, 38.87, 295.65, 0.00 },
        { "Driver fade", 248.35, 33.78, 41.15, 279.66, 23.11 },
        { "7-iron draw", 168.95, 30.54, 46.75, 177.80, -9.13 },
        { "PW", 134.50, 29.37, 49.29, 138.35, 0.00 },
        { "Chip", 27.97, 4.53, 35.05, 45.98, 0.00 },
    };
}

public class CalibrationTests
{
    [Fact]
    public void CalibrationFitsTrainingStaysInBoundsAndReportsValidation()
    {
        var (train, val) = ReferenceShots.SplitAlternating(ReferenceShots.TrackManTourAverages2014);
        var svc = new CalibrationService();
        var before = CalibrationService.Errors(svc.Compare(train, CalibrationProfile.None));
        var p = svc.Calibrate(train, val, "test fit");
        Assert.Empty(p.Validate());
        Assert.NotNull(p.ValidationErrors);
        Assert.Equal(val.Count, p.ValidationErrors!.Shots);
        Assert.Empty(CalibrationService.CheckPhysicalRange(p.Apply(BallProperties.Standard).Aerodynamics));
        // The fit must do at least as well as no adjustment on the shots it was fitted to (weighted objective).
        double Score(CalibrationErrors e) => Math.Pow(e.CarryRmsYards!.Value / 3, 2) + Math.Pow(e.ApexRmsYards!.Value / 2, 2) + Math.Pow(e.LandingAngleRmsDegrees!.Value / 2, 2);
        Assert.True(Score(p.TrainingErrors!) <= Score(before) + 1e-6);
    }

    [Fact]
    public void TrainingAndValidationMustBeSeparate()
    {
        var shots = ReferenceShots.TrackManTourAverages2014;
        Assert.Throws<ArgumentException>(() => new CalibrationService().Calibrate(shots, shots.Take(2).ToArray(), "overlap"));
    }

    [Fact]
    public void UnphysicalCoefficientsAreDetected()
    {
        Assert.NotEmpty(CalibrationService.CheckPhysicalRange(new ScaledAerodynamicModel(DefaultAerodynamics.Provisional, 2, 1, 1)));
        Assert.NotEmpty(CalibrationService.CheckPhysicalRange(new ScaledAerodynamicModel(DefaultAerodynamics.Provisional, 1, 0.1, 1)));
        Assert.NotEmpty(new CalibrationProfile { DragScale = 0.2 }.Validate());
    }

    [Fact]
    public void ProfilesRoundTripThroughJson()
    {
        var p = new CalibrationProfile { Name = "x", DragScale = 0.95, LiftScale = 1.05, DragReynoldsSlope = -0.1, RollingResistanceScale = 1.2 };
        var back = CalibrationProfile.FromJson(p.ToJson());
        Assert.Equal(p, back);
    }

    [Fact]
    public void NelderMeadFindsABoundedMinimum()
    {
        var x = CalibrationService.NelderMead(v => Math.Pow(v[0] - 0.7, 2) + Math.Pow(v[1] - 3, 2), new[] { 1.0, 1.0 },
                                              new[] { 0.0, 0.0 }, new[] { 2.0, 2.0 }, 500);
        Assert.Equal(0.7, x[0], 3);
        Assert.Equal(2.0, x[1], 3);   // clamped at the bound
    }
}
