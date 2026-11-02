using Xunit;

namespace SquareGspro.Physics.Tests;

public class LieModelTests
{
    private static readonly LaunchConditions SevenIron = TestShots.SevenIron();
    private static LieAdjustment Apply(LieCondition c, double loft = 32) => LieModel.Default.Apply(SevenIron, c, loft);

    [Fact]
    public void CleanLiesChangeNothing()
    {
        foreach (var kind in new[] { LieKind.Tee, LieKind.Fairway, LieKind.Green })
        {
            var a = Apply(LieCondition.Flat(kind));
            Assert.False(a.Changed);
            Assert.Equal(SevenIron, a.Launch with { Sources = SevenIron.Sources });
            Assert.Equal("", a.Summary);
        }
    }

    [Fact]
    public void RoughCutsSpinAndSpeedAndIsMarkedAdjusted()
    {
        var rough = Apply(LieCondition.Flat(LieKind.Rough));
        var deep = Apply(LieCondition.Flat(LieKind.DeepRough));
        Assert.True(rough.Launch.TotalSpin < SevenIron.TotalSpin * 0.8);
        Assert.True(rough.Launch.BallSpeed < SevenIron.BallSpeed);
        Assert.True(deep.Launch.BallSpeed < rough.Launch.BallSpeed);
        Assert.True(rough.Launch.LaunchAngle > SevenIron.LaunchAngle);
        Assert.Equal(ValueSource.Adjusted, rough.Launch.Sources[LaunchParameter.TotalSpin]);
        Assert.Equal(ValueSource.Measured, rough.Launch.Sources[LaunchParameter.LaunchDirection]);
        Assert.Contains("spin", rough.Summary);
    }

    [Fact]
    public void LiesChangeWhereTheBallGoes()
    {
        double Carry(LieKind k) => TestShots.Carry(Apply(LieCondition.Flat(k)).Launch).CarryDistance;
        double fairway = Carry(LieKind.Fairway);
        Assert.True(Carry(LieKind.DeepRough) < fairway - 15, "deep rough costs distance");
        Assert.True(Carry(LieKind.Sand) < fairway - 5);
        // The flier: less spin from the rough rolls out further.
        var clean = TestShots.Fly(SevenIron);
        var flier = TestShots.Fly(Apply(LieCondition.Flat(LieKind.Rough)).Launch);
        Assert.True(flier.RollDistance > clean.RollDistance);
    }

    [Fact]
    public void UphillLaunchesHigherDownhillLower()
    {
        var up = Apply(new LieCondition(LieKind.Fairway, UphillDegrees: 4));
        var down = Apply(new LieCondition(LieKind.Fairway, UphillDegrees: -4));
        Assert.Equal(3.2, up.LaunchChangeDegrees, 9);
        Assert.True(up.Launch.LaunchAngle > SevenIron.LaunchAngle && down.Launch.LaunchAngle < SevenIron.LaunchAngle);
        Assert.True(up.Launch.TotalSpin > down.Launch.TotalSpin);
    }

    [Fact]
    public void SidehillLieTurnsTheFaceByLoftGeometry()
    {
        var above = Apply(new LieCondition(LieKind.Fairway, BallAboveFeetDegrees: 5), loft: 32);
        double face = Math.Atan(Math.Tan(32 * Math.PI / 180) * Math.Sin(5 * Math.PI / 180)) * 180 / Math.PI;
        Assert.Equal(-0.8 * face, above.DirectionChangeDegrees, 9);   // right-hander: ball above feet pulls left...
        Assert.True(above.SpinAxisChangeDegrees < 0);                   // ...and draws
        var below = Apply(new LieCondition(LieKind.Fairway, BallAboveFeetDegrees: -5), loft: 32);
        Assert.True(below.DirectionChangeDegrees > 0 && below.SpinAxisChangeDegrees > 0);
        var lefty = Apply(new LieCondition(LieKind.Fairway, BallAboveFeetDegrees: 5, LeftHanded: true), loft: 32);
        Assert.Equal(-above.DirectionChangeDegrees, lefty.DirectionChangeDegrees, 9);
        // A wedge is turned more than a driver by the same lie.
        Assert.True(Math.Abs(Apply(new LieCondition(LieKind.Fairway, BallAboveFeetDegrees: 5), 56).DirectionChangeDegrees)
                    > 3 * Math.Abs(Apply(new LieCondition(LieKind.Fairway, BallAboveFeetDegrees: 5), 10).DirectionChangeDegrees));
    }

    [Fact]
    public void AdjustedLaunchIsStillValid()
    {
        var extreme = Apply(new LieCondition(LieKind.DeepRough, UphillDegrees: 60, BallAboveFeetDegrees: -60));
        Assert.Empty(extreme.Launch.Validate());
        Assert.Equal(3 + 0.8 * LieModel.Default.MaximumSlopeDegrees, extreme.LaunchChangeDegrees, 9);   // slope capped
    }
}

public class SpinBackTests
{
    private static double Roll(LaunchConditions l, SurfaceProperties s)
    {
        var r = TestShots.Fly(l, terrain: new FlatTerrain(s));
        return r.FinalPosition.Y - r.LandingPosition.Y;
    }

    private static readonly LaunchConditions Pw = LaunchConditions.FromDisplayUnits(102, 24.2, 0, 9304, 0);
    private static readonly LaunchConditions LowChip = LaunchConditions.FromDisplayUnits(40, 30, 0, 4500, 0);

    [Fact]
    public void HighSpinWedgeSpinsBackOnAReceptiveGreen()
    {
        Assert.InRange(Roll(Pw, Surfaces.Green), -6, -1);
    }

    private static readonly LaunchConditions SevenIron = LaunchConditions.FromDisplayUnits(118, 16.5, 0, 7000, 0);
    private static readonly LaunchConditions LowWedge = LaunchConditions.FromDisplayUnits(80, 25, 0, 10500, 0);

    /// <summary>Forward distance (m) from the landing to the second touch, and the furthest the ball got past the landing.</summary>
    private static (double FirstHop, double Furthest, double Final) Bounces(LaunchConditions l, SurfaceProperties s)
    {
        var r = TestShots.Fly(l, terrain: new FlatTerrain(s));
        double land = r.LandingPosition.Y;
        var second = r.GroundContacts.Where(c => c.Kind == GroundContactKind.Bounce).Select(c => c.Position.Y).DefaultIfEmpty(land).First();
        return (second - land, r.Trajectory.Max(p => p.Position.Y) - land, r.FinalPosition.Y - land);
    }

    [Fact]
    public void FullShotsHopForwardBeforeTheGreenGrabsThem()
    {
        // The stock green is receptive: a spinny full PW only just hops forward (~0.2 m) before it checks.
        var pw = Bounces(Pw, Surfaces.Green);
        Assert.True(pw.FirstHop > 0.1, $"PW first hop {pw.FirstHop:0.00} m");
        Assert.True(pw.Final < pw.Furthest - 1, $"PW checks back: final {pw.Final:0.00} m, furthest {pw.Furthest:0.00} m");

        // A mid iron hops further and finishes past its pitch mark (on the stock green it then stops; less grippy greens
        // let it roll out).
        var iron = Bounces(SevenIron, Surfaces.Green);
        Assert.True(iron.FirstHop > 1, $"7-iron first hop {iron.FirstHop:0.00} m");
        Assert.True(iron.Final > 1, $"7-iron finishes past its pitch mark: final {iron.Final:0.00} m");

        // Without skid the green grabs the PW on its first touch and throws it back.
        Assert.True(Bounces(Pw, Surfaces.Green with { ImpactSkidSpeed = 0 }).FirstHop < 0);
    }

    [Fact]
    public void LowSpinningWedgeSkipsThenChecks()
    {
        var w = Bounces(LowWedge, Surfaces.Green);
        Assert.True(w.FirstHop > 2, $"first hop {w.FirstHop:0.00} m");
        Assert.True(w.Final < w.Furthest - 1 && w.Final < 5, $"checks: final {w.Final:0.00} m, furthest {w.Furthest:0.00} m");
        // Turf drag is what stops it: without, it skips on and releases.
        Assert.True(Bounces(LowWedge, Surfaces.Green with { ImpactDrag = 0 }).Final > w.Final + 3);
    }

    [Fact]
    public void FastLandingSkidsKeepingItsBackspinForTheNextTouch()
    {
        var landing = new BallState(Vector3D.Zero, new Vector3D(0, 15.4, -17.3), new Vector3D(840, 0, 0));   // a PW, ~8000 rpm
        var skid = BounceModel.Resolve(landing, Vector3D.UnitZ, Surfaces.Green.Effective(), BallProperties.Standard);
        var grab = BounceModel.Resolve(landing, Vector3D.UnitZ, (Surfaces.Green with { ImpactSkidSpeed = 0 }).Effective(), BallProperties.Standard);
        Assert.True(skid.Slid);
        Assert.True(skid.Outgoing.Velocity.Y > 0, "hops forward");
        Assert.True(skid.Outgoing.AngularVelocity.X > 0.3 * 840, "keeps much of its backspin");
        Assert.True(grab.Outgoing.Velocity.Y < skid.Outgoing.Velocity.Y);
    }

    [Fact]
    public void TurfDragTakesPaceButNotSpin()
    {
        var landing = new BallState(Vector3D.Zero, new Vector3D(0, 17, -14), new Vector3D(900, 0, 0));
        var green = Surfaces.Green.Effective();
        var with = BounceModel.Resolve(landing, Vector3D.UnitZ, green, BallProperties.Standard);
        var without = BounceModel.Resolve(landing, Vector3D.UnitZ, green with { ImpactDrag = 0 }, BallProperties.Standard);
        Assert.Equal(without.Outgoing.AngularVelocity.X, with.Outgoing.AngularVelocity.X, 9);
        Assert.Equal(without.Outgoing.Velocity.Z, with.Outgoing.Velocity.Z, 9);
        Assert.Equal(without.Outgoing.Velocity.Y * (1 - green.ImpactDrag), with.Outgoing.Velocity.Y, 9);   // a hard landing: the full share
    }

    [Fact]
    public void FirmGreensReleaseAndLowSpinChipsRunOut()
    {
        Assert.True(Roll(Pw, GreenModel.Surface(10, firmness: 0.8)) > Roll(Pw, Surfaces.Green) + 1);
        Assert.True(Roll(LowChip, Surfaces.Green) > 8);
    }

    [Fact]
    public void TangentialRestitutionLeavesMoreBackspinWhenTheContactGrips()
    {
        var s = new BallState(Vector3D.Zero, new Vector3D(0, 3, -2), new Vector3D(150, 0, 0));
        var grip = Surfaces.Fairway with { TangentialRestitution = 0, StaticFriction = 2, DynamicFriction = 2 };
        var a = BounceModel.Resolve(s, Vector3D.UnitZ, grip.Effective(), BallProperties.Standard);
        var b = BounceModel.Resolve(s, Vector3D.UnitZ, (grip with { TangentialRestitution = 0.4 }).Effective(), BallProperties.Standard);
        Assert.False(a.Slid);
        Assert.True(b.Outgoing.Velocity.Y < a.Outgoing.Velocity.Y, "the reversed slip throws the ball back");
    }
}
