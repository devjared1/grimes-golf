using Xunit;

namespace SquareGspro.Physics.Tests;

public class ImpactTests
{
    private static readonly BallProperties Ball = BallProperties.Standard;

    private static ImpactResult Hit(ClubProperties club, double mph, double attack, double loft, double face = 0, double path = 0,
                                    double toe = 0, double height = 0) =>
        ImpactModel.Simulate(new ClubDelivery
        {
            ClubSpeed = mph * UnitConversion.MphToMetresPerSecond, AttackAngleDegrees = attack, DynamicLoftDegrees = loft,
            FaceAngleDegrees = face, PathDegrees = path, ImpactToeMetres = toe, ImpactHeightMetres = height,
        }, club, Ball);

    private static ImpactResult Driver(double face = 0, double path = 0, double toe = 0, double height = 0, double loft = 12.8) =>
        Hit(ClubProperties.Driver, 113, -1.3, loft, face, path, toe, height);

    [Theory]
    [InlineData("Driver", 113, -1.3, 12.8, 167, 10.9, 2686)]
    [InlineData("7-iron", 90, -4.3, 23.1, 120, 16.3, 7097)]
    [InlineData("Pitching wedge", 83, -5.0, 34.7, 102, 24.2, 9304)]
    public void PresetsReproduceTourAverageLaunch(string name, double clubMph, double attack, double loft, double ballMph, double launch, double spin)
    {
        var club = new[] { ClubProperties.Driver, ClubProperties.SevenIron, ClubProperties.PitchingWedge }.Single(c => c.Name == name);
        var r = Hit(club, clubMph, attack, loft);
        Assert.InRange(r.Launch.BallSpeedMph, ballMph - 3, ballMph + 1);
        Assert.InRange(r.Launch.LaunchAngleDegrees, launch - 0.5, launch + 0.5);
        Assert.InRange(r.Launch.TotalSpinRpm, spin - 200, spin + 200);
        Assert.All(r.Launch.Sources.Values, s => Assert.Equal(ValueSource.Calculated, s));
    }

    [Fact]
    public void SquareCentredStrikeIsStraight()
    {
        var r = Driver();
        Assert.Equal(0, r.Launch.LaunchDirectionDegrees, 9);
        Assert.Equal(0, r.Launch.SpinAxisDegrees, 9);
        Assert.InRange(r.SmashFactor, 1.42, 1.50);
        Assert.False(r.Slid);
    }

    [Fact]
    public void ConservesLinearMomentumAndDoesNotCreateEnergy()
    {
        var club = ClubProperties.SevenIron;
        var d = new ClubDelivery { ClubSpeed = 40, AttackAngleDegrees = -4, DynamicLoftDegrees = 24, FaceAngleDegrees = 2, PathDegrees = -3, ImpactToeMetres = 0.01 };
        var r = ImpactModel.Simulate(d, club, Ball);
        double p = 40 * club.Mass;
        var vClub = new Vector3D(Math.Sin(-3 * Math.PI / 180) * Math.Cos(-4 * Math.PI / 180), Math.Cos(-3 * Math.PI / 180) * Math.Cos(-4 * Math.PI / 180), Math.Sin(-4 * Math.PI / 180)) * 40;
        var before = vClub * club.Mass;
        var after = r.BallVelocity * Ball.MassKg + r.ClubVelocityAfter * club.Mass;
        Assert.True((before - after).Length < 1e-9 * p);
        double keBefore = 0.5 * club.Mass * 40 * 40;
        double keAfter = 0.5 * club.Mass * r.ClubVelocityAfter.LengthSquared + 0.5 * Ball.MassKg * r.BallVelocity.LengthSquared
                       + 0.5 * Ball.MomentOfInertia * r.BallSpin.LengthSquared;   // club rotation energy only adds to this
        Assert.True(keAfter < keBefore);
    }

    [Fact]
    public void OpenFaceStartsRightAndFades()
    {
        var r = Driver(face: 3);
        Assert.InRange(r.Launch.LaunchDirectionDegrees, 2.2, 3.0);   // start line mostly follows the face
        Assert.True(r.Launch.SpinAxisDegrees > 5);
        Assert.Equal(3, r.FaceToPathDegrees, 9);
    }

    [Fact]
    public void InToOutPathWithSquareFaceDraws()
    {
        var r = Driver(path: 4);
        Assert.InRange(r.Launch.LaunchDirectionDegrees, 0.1, 1.5);
        Assert.True(r.Launch.SpinAxisDegrees < -5);
    }

    [Fact]
    public void GearEffect()
    {
        var centre = Driver();
        var toe = Driver(toe: 0.015);
        var heel = Driver(toe: -0.015);
        Assert.True(toe.Launch.SpinAxisDegrees < -5, "toe strike hooks");
        Assert.True(heel.Launch.SpinAxisDegrees > 5, "heel strike slices");
        Assert.True(toe.Launch.LaunchDirectionDegrees > 0, "toe strike starts right of a square face (the face opens)");
        Assert.True(toe.Launch.BallSpeed < centre.Launch.BallSpeed);
        Assert.True(heel.Launch.BallSpeed < centre.Launch.BallSpeed);

        var high = Driver(height: 0.01);
        var low = Driver(height: -0.01);
        Assert.True(high.Launch.TotalSpin < centre.Launch.TotalSpin && centre.Launch.TotalSpin < low.Launch.TotalSpin);
        Assert.True(high.Launch.LaunchAngle > low.Launch.LaunchAngle);
    }

    [Fact]
    public void MoreLoftLaunchesHigherWithMoreSpin()
    {
        var a = Hit(ClubProperties.SevenIron, 90, -4, 20);
        var b = Hit(ClubProperties.SevenIron, 90, -4, 26);
        Assert.True(b.Launch.LaunchAngle > a.Launch.LaunchAngle);
        Assert.True(b.Launch.TotalSpin > a.Launch.TotalSpin);
        Assert.True(b.Launch.BallSpeed < a.Launch.BallSpeed);
        Assert.Equal(26 + 4, b.SpinLoftDegrees, 6);
    }

    [Fact]
    public void LeftHandedToeStrikeMirrors()
    {
        var rh = Driver(toe: 0.015);
        var lh = ImpactModel.Simulate(new ClubDelivery
        {
            ClubSpeed = 113 * UnitConversion.MphToMetresPerSecond, AttackAngleDegrees = -1.3, DynamicLoftDegrees = 12.8,
            ImpactToeMetres = 0.015, LeftHanded = true,
        }, ClubProperties.Driver, Ball);
        Assert.Equal(-rh.Launch.SpinAxisDegrees, lh.Launch.SpinAxisDegrees, 6);
    }

    [Fact]
    public void MissingDynamicLoftIsFlaggedAsEstimate()
    {
        var r = ImpactModel.Simulate(new ClubDelivery { ClubSpeed = 45 }, ClubProperties.Driver, Ball);
        Assert.Contains(r.Warnings, w => w.Contains("Dynamic loft"));
    }

    [Fact]
    public void LowFrictionMakesTheBallSlide()
    {
        var r = Hit(ClubProperties.PitchingWedge with { FaceFriction = 0.05 }, 83, -5, 34.7);
        Assert.True(r.Slid);
        Assert.True(r.Launch.TotalSpinRpm < Hit(ClubProperties.PitchingWedge, 83, -5, 34.7).Launch.TotalSpinRpm);
    }
}
