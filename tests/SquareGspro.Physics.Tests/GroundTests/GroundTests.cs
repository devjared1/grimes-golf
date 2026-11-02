using Xunit;

namespace SquareGspro.Physics.Tests;

public class BounceTests
{
    private static readonly BallProperties Ball = BallProperties.Standard;

    [Fact]
    public void VerticalDropUsesPennerRestitution()
    {
        var green = (Surfaces.Green with { Firmness = 0.5 }).Effective();   // Penner's values are at nominal firmness
        var s = new BallState(Vector3D.Zero, new Vector3D(0, 0, -5), Vector3D.Zero);
        var b = BounceModel.Resolve(s, Vector3D.UnitZ, green, Ball);
        double e = 0.510 - 0.0375 * 5 + 0.000903 * 25;
        Assert.Equal(e, b.Restitution, 9);
        Assert.Equal(5 * e, b.Outgoing.Velocity.Z, 9);
        Assert.Equal(0, b.Outgoing.Velocity.Y, 9);
        Assert.Equal(0, b.CraterAngle, 12);
    }

    [Fact]
    public void RestitutionCurveFlattensAtHighSpeed()
    {
        var green = (Surfaces.Green with { Firmness = 0.5 }).Effective();   // Penner's values are at nominal firmness
        Assert.Equal(0.12, green.RestitutionAt(25), 9);
        Assert.True(green.RestitutionAt(2) > green.RestitutionAt(10));
    }

    [Fact]
    public void BackspinChecksTheBallTopspinReleasesIt()
    {
        // Without skid, so the contact grips: a skidding contact's friction is the same whatever the spin, and the
        // spin only tells on the next touch (see FastLandingSkidsKeepingItsBackspinForTheNextTouch).
        var fairway = (Surfaces.Fairway with { ImpactSkidSpeed = 0 }).Effective();
        var v = new Vector3D(0, 15, -12);
        var back = BounceModel.Resolve(new BallState(Vector3D.Zero, v, new Vector3D(800, 0, 0)), Vector3D.UnitZ, fairway, Ball);
        var none = BounceModel.Resolve(new BallState(Vector3D.Zero, v, Vector3D.Zero), Vector3D.UnitZ, fairway, Ball);
        var top = BounceModel.Resolve(new BallState(Vector3D.Zero, v, new Vector3D(-300, 0, 0)), Vector3D.UnitZ, fairway, Ball);
        Assert.True(back.Outgoing.Velocity.Y < none.Outgoing.Velocity.Y);
        Assert.True(none.Outgoing.Velocity.Y < top.Outgoing.Velocity.Y);
        Assert.True(back.Outgoing.AngularVelocity.X < 800, "friction removes backspin");
    }

    [Fact]
    public void ImpactNeverAddsEnergy()
    {
        foreach (var surface in new[] { Surfaces.Green, Surfaces.Fairway, Surfaces.Rough, Surfaces.Sand, Surfaces.HardGround })
            foreach (double w in new[] { -400.0, 0, 300, 900 })
            {
                var s = new BallState(Vector3D.Zero, new Vector3D(2, 18, -14), new Vector3D(w, 50, 0));
                var b = BounceModel.Resolve(s, Vector3D.UnitZ, surface.Effective(), Ball);
                Assert.True(Energy(b.Outgoing) <= Energy(s) + 1e-9, $"{surface.Name} spin {w}");
            }
        static double Energy(in BallState s) =>
            0.5 * Ball.MassKg * s.Velocity.LengthSquared + 0.5 * Ball.MomentOfInertia * s.AngularVelocity.LengthSquared;
    }

    [Fact]
    public void FirmnessAndMoistureAdjustCoefficients()
    {
        var soft = Surfaces.SoftGround.Effective();
        var firm = (Surfaces.Fairway with { Firmness = 0.9 }).Effective();
        var nominal = Surfaces.Fairway.Effective();
        Assert.True(firm.RestitutionAt(8) > nominal.RestitutionAt(8));
        Assert.True(soft.RestitutionAt(8) < nominal.RestitutionAt(8));
        Assert.True(soft.CraterAngleDegrees > nominal.CraterAngleDegrees);
        var wet = Surfaces.WetFairway.Effective();
        Assert.True(wet.RollingResistance > nominal.RollingResistance);
        Assert.Equal(nominal, Surfaces.Fairway.Effective().Effective());   // idempotent
    }
}

public class RollTests
{
    private static GroundContext Context(ITerrainProvider terrain, bool airDrag = false) => new()
    {
        Ball = BallProperties.Standard, Air = AirProperties.Standard, Wind = Wind.Calm, Terrain = terrain,
        Options = GroundOptions.Default with { IncludeAirDrag = airDrag },
    };

    private static (BallState State, double Time) RollUntil(ref GroundContext c, BallState s, Func<BallState, double, bool> stop, double max = 60)
    {
        double t = 0, dt = c.Options.TimeStepSeconds;
        while (t < max && !stop(s, t))
        {
            if (RollModel.Step(ref s, ref c, t, dt) == RollStepOutcome.AtRest) break;
            t += dt;
        }
        return (s, t);
    }

    private static BallState Rolling(GroundContext c, Vector3D velocity)
    {
        var n = c.Terrain.GetSurfaceNormal(0, 0);
        return new BallState(c.OnSurface(0, 0), velocity, Vector3D.Cross(n, velocity) / BallProperties.Standard.RadiusMetres);
    }

    [Fact]
    public void IdealSphereRollsDownAnInclineAtFiveSeventhsGSinTheta()
    {
        var plane = new InclinedPlaneTerrain(Surfaces.Fairway with { RollingResistance = 0 }, 0, -0.1);
        var c = Context(plane);
        var (s, t) = RollUntil(ref c, Rolling(c, Vector3D.Zero), (_, time) => time >= 1);
        double sinTheta = 0.1 / Math.Sqrt(1.01);
        Assert.Equal(5.0 / 7.0 * PhysicsConstants.Gravity * sinTheta, s.Speed / t, 2);
    }

    [Fact]
    public void FlatRollStopsWhereRollingResistanceSaysAndRestsThere()
    {
        var c = Context(new FlatTerrain(Surfaces.Fairway with { RollingResistance = 0.1 }));
        var (s, _) = RollUntil(ref c, Rolling(c, new Vector3D(0, 3, 0)), (_, _) => false);
        double expected = 9 / (2 * 0.1 * PhysicsConstants.Gravity);
        Assert.InRange(s.Position.Y, expected * 0.995, expected * 1.005);
        Assert.Equal(Vector3D.Zero, s.Velocity);
    }

    [Fact]
    public void SlidingBallEndsRollingAtFiveSeventhsOfItsSpeed()
    {
        var c = Context(new FlatTerrain(Surfaces.Fairway with { RollingResistance = 0 }));
        var start = new BallState(c.OnSurface(0, 0), new Vector3D(0, 5, 0), Vector3D.Zero);
        bool rolled = false;
        double t = 0;
        var s = start;
        while (t < 5)
        {
            var o = RollModel.Step(ref s, ref c, t, c.Options.TimeStepSeconds);
            t += c.Options.TimeStepSeconds;
            if (o == RollStepOutcome.Rolling) { rolled = true; break; }
        }
        Assert.True(rolled);
        Assert.InRange(s.Speed, 5 * 5.0 / 7.0 - 0.02, 5 * 5.0 / 7.0 + 0.02);
    }

    [Fact]
    public void ResistanceHoldsTheBallOnAGentleSlope()
    {
        var gentle = Context(new InclinedPlaneTerrain(Surfaces.Green, 0.02, 0));
        var (s, t) = RollUntil(ref gentle, Rolling(gentle, Vector3D.Zero), (_, _) => false);
        Assert.True(t < 0.01, "a ball placed on a 2% green stays put");
        var steep = Context(new InclinedPlaneTerrain(Surfaces.Green, 0.2, 0));
        var (s2, _) = RollUntil(ref steep, Rolling(steep, Vector3D.Zero), (_, time) => time >= 1);
        Assert.True(s2.Position.X < -0.3, "it rolls down a 20% slope");
    }
}

public class GroundShotTests
{
    private static FlightResults Shot(ITerrainProvider terrain, LaunchConditions? launch = null) => TestShots.Fly(launch ?? TestShots.Driver(), terrain: terrain);

    [Fact]
    public void FullShotBouncesThenRollsToRest()
    {
        var r = Shot(TestShots.Fairway);
        Assert.Equal(SimulationEnd.Rest, r.End);
        Assert.True(r.BounceCount >= 1);
        Assert.Equal(GroundContactKind.Landing, r.GroundContacts[0].Kind);
        Assert.Contains(r.GroundContacts, e => e.Kind == GroundContactKind.StartedRolling);
        Assert.Equal(GroundContactKind.CameToRest, r.GroundContacts[^1].Kind);
        Assert.InRange(r.RollYards, 5, 40);
        Assert.Equal(BallPhase.Rest, r.Trajectory[^1].Phase);
    }

    [Fact]
    public void HardGroundRunsFurtherThanSoft()
    {
        double hard = Shot(new FlatTerrain(Surfaces.HardGround)).RollDistance;
        double fairway = Shot(TestShots.Fairway).RollDistance;
        double soft = Shot(new FlatTerrain(Surfaces.SoftGround)).RollDistance;
        double sand = Shot(new FlatTerrain(Surfaces.Sand)).RollDistance;
        Assert.True(hard > fairway, $"{hard} vs {fairway}");
        Assert.True(fairway > soft, $"{fairway} vs {soft}");
        Assert.True(soft > sand);
        Assert.True(Shot(new FlatTerrain(Surfaces.HardGround)).BounceCount >= Shot(new FlatTerrain(Surfaces.SoftGround)).BounceCount);
    }

    [Fact]
    public void RoughAndWetGroundReduceRoll()
    {
        double fairway = Shot(TestShots.Fairway).RollDistance;
        Assert.True(Shot(new FlatTerrain(Surfaces.Rough)).RollDistance < fairway);
        Assert.True(Shot(new FlatTerrain(Surfaces.DeepRough)).RollDistance < Shot(new FlatTerrain(Surfaces.Rough)).RollDistance);
        Assert.True(Shot(new FlatTerrain(Surfaces.WetFairway)).RollDistance < fairway);
    }

    [Fact]
    public void IncreasedRollingResistanceReducesRoll()
    {
        double low = Shot(new FlatTerrain(Surfaces.Fairway with { RollingResistance = 0.08 })).RollDistance;
        double high = Shot(new FlatTerrain(Surfaces.Fairway with { RollingResistance = 0.2 })).RollDistance;
        Assert.True(high < low);
    }

    [Fact]
    public void UphillReducesAndDownhillIncreasesRoll()
    {
        // Measured once the ball is rolling: a ball meets an upslope sooner, still faster and flatter, so its hops
        // carry a little further than on the flat (each impact itself is slower uphill: see
        // UphillImpactsComeOffSlowerThanDownhillOnes).
        double flat = RollPhase(Shot(TestShots.Fairway));
        double up = RollPhase(Shot(new InclinedPlaneTerrain(Surfaces.Fairway, 0, 0.05)));
        double down = RollPhase(Shot(new InclinedPlaneTerrain(Surfaces.Fairway, 0, -0.05)));
        Assert.True(up < flat, $"{up} vs {flat}");
        Assert.True(down > flat, $"{down} vs {flat}");

        static double RollPhase(FlightResults r) =>
            r.FinalPosition.HorizontalDistanceTo(r.GroundContacts.First(c => c.Kind == GroundContactKind.StartedRolling).Position);
    }

    [Fact]
    public void UphillImpactsComeOffSlowerThanDownhillOnes()
    {
        var fairway = Surfaces.Fairway.Effective();
        var s = new BallState(Vector3D.Zero, new Vector3D(0, 19.6, -15.8), new Vector3D(209, 0, 0));   // a driver landing
        double Forward(Vector3D normal) => BounceModel.Resolve(s, normal.Normalized(), fairway, BallProperties.Standard).Outgoing.Velocity.Y;
        double up = Forward(new Vector3D(0, -0.05, 1)), flat = Forward(Vector3D.UnitZ), down = Forward(new Vector3D(0, 0.05, 1));
        Assert.True(up < flat && flat < down, $"{up} / {flat} / {down}");
    }

    [Fact]
    public void SideSlopeTurnsTheRollDownhill()
    {
        var r = Shot(new InclinedPlaneTerrain(Surfaces.Fairway, -0.04, 0));   // falls away to the right
        Assert.True(r.FinalLateral > r.CarryLateral + 0.5);
    }

    [Fact]
    public void BounceAndRollCanBeDisabled()
    {
        var noRoll = TestShots.Fly(TestShots.Driver(), options: SimulationOptions.Default with { EnableRoll = false });
        Assert.Equal(SimulationEnd.StoppedBouncing, noRoll.End);
        Assert.DoesNotContain(noRoll.GroundContacts, e => e.Kind == GroundContactKind.CameToRest);
    }

    [Fact]
    public void FollowsAHeightmap()
    {
        var hm = HeightmapTerrain.FromFunction(-50, -10, 2, 51, 211, (x, y) => 0.03 * y, (_, _) => Surfaces.Fairway);
        var plane = new InclinedPlaneTerrain(Surfaces.Fairway, 0, 0.03);
        Assert.Equal(plane.GetElevation(3.3, 77.7), hm.GetElevation(3.3, 77.7), 9);
        Assert.Equal(plane.GetSurfaceNormal(3, 70).Z, hm.GetSurfaceNormal(3.3, 77.7).Z, 9);
        var a = Shot(hm, TestShots.SevenIron());
        var b = Shot(plane, TestShots.SevenIron());
        Assert.Equal(b.CarryDistance, a.CarryDistance, 3);
        Assert.Equal(b.TotalDistance, a.TotalDistance, 1);
    }
}
