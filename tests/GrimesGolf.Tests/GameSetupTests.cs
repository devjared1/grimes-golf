using GrimesGolf.Core;
using GrimesGolf.Core.Course;
using GrimesGolf.Core.Storage;
using Xunit;

namespace GrimesGolf.Tests;

public class OutlineTests
{
    [Fact]
    public void RoundedOutlineCutsCornersButKeepsTheShape()
    {
        var square = new Polygon(new P2[] { new(0, 0), new(40, 0), new(40, 40), new(0, 40) });
        var round = square.Rounded();
        Assert.True(round.Points.Count > 4 * 4);
        Assert.InRange(round.Area / square.Area, 0.99, 1.0);   // only the corners go
        Assert.False(round.Contains(new P2(0.3, 0.3)));       // the corner itself is rounded off...
        Assert.True(round.Contains(new P2(20, 0.3)));         // ...the straight edges stay where they were
        Assert.True(round.Contains(new P2(3, 3)));
        // Small shapes: never more than a quarter of an edge, so a little square goes round, not to nothing.
        var tiny = new Polygon(new P2[] { new(0, 0), new(2, 0), new(2, 2), new(0, 2) }).Rounded();
        Assert.InRange(tiny.Area, Math.PI, 4);
    }
}

public class GameSetupTests
{
    private static readonly Golfer[] Two = { new("Ann"), new("Bob") };
    private static readonly Golfer[] Four = { new("Ann"), new("Bob"), new("Cat"), new("Dee") };
    private static readonly RangeShot Topped = new(70, 12, 0, 4000, 0, null, null);    // into the pond on the par 3
    private static readonly RangeShot GoodIron = new(112, 18, 0, 7600, 0, null, null); // onto the par-3 green

    private static ShotOutcome Hit(Match m, RangeShot s, bool putt = false)
    {
        var o = m.Play(s, putt, m.Current.DefaultAimHeading());
        m.AdvanceTurn();
        return o;
    }

    private static RangeShot Putt(Match m, double metresShort) =>
        new(Math.Sqrt(2 * 0.55 * (m.Current.DistanceToPin - metresShort)) * Units.MsToMph, 1, 0, 0, 0, null, null);

    [Fact]
    public void HoleSetsPickTheHoles()
    {
        var deerRun = CourseLibrary.All[0].Load().Holes;
        Assert.Equal(Enumerable.Range(1, 9), new GameSetup(Holes: HoleSet.Front9).Pick(deerRun).Select(h => h.Number));
        Assert.Equal(Enumerable.Range(10, 9), new GameSetup(Holes: HoleSet.Back9).Pick(deerRun).Select(h => h.Number));
        var par3s = new GameSetup(Holes: HoleSet.Par3s).Pick(deerRun);
        Assert.NotEmpty(par3s);
        Assert.All(par3s, h => Assert.Equal(3, h.Par));
        Assert.Equal(18, GameSetup.Default.Pick(deerRun).Count);

        // Nine holes or fewer: front and back nine are all of them.
        Assert.Equal(3, new GameSetup(Holes: HoleSet.Back9).Pick(TestCourse.Holes).Count);
        Assert.Equal(new[] { 2 }, new GameSetup(Holes: HoleSet.Par3s).Pick(TestCourse.Holes).Select(h => h.Number));
    }

    [Fact]
    public void GamesNeedTheRightNumberOfPlayers()
    {
        Assert.NotNull(GameSetup.Problem(GameType.Scramble, 1));
        Assert.Null(GameSetup.Problem(GameType.Scramble, 3));
        Assert.NotNull(GameSetup.Problem(GameType.MatchPlay, 3));
        Assert.Null(GameSetup.Problem(GameType.MatchPlay, 4));
        Assert.Equal(GameType.Stroke, new GameSetup(GameType.MatchPlay).TypeFor(3));
        Assert.Equal(GameType.Stroke, new Match(TestCourse.Holes, null, Two.Take(1).ToList(), type: GameType.BestBall).Type);

        Assert.Equal(2, new Match(TestCourse.Holes, null, Four, type: GameType.Scramble).Sides.Count);
        Assert.Single(new Match(TestCourse.Holes, null, Two, type: GameType.BestBall).Sides);
        Assert.Equal(2, new Match(TestCourse.Holes, null, Two, type: GameType.MatchPlay).Sides.Count);
        var fourBall = new Match(TestCourse.Holes, null, Four, type: GameType.MatchPlay);
        Assert.Equal(new[] { 0, 1 }, fourBall.Sides[0]);
        Assert.Equal("Cat & Dee", fourBall.SideName(1));
    }

    [Fact]
    public void ScrambleTeamPlaysTheBestShot()
    {
        var m = new Match(TestCourse.Holes, null, Two, new HouseRules(Mulligans: 2), GameType.Scramble);
        m.StartHole(1);
        Assert.Equal(0, m.Turn);
        Hit(m, Topped);                        // Ann in the water
        Assert.Null(m.LastPick);
        Assert.Equal(1, m.Turn);               // Bob hits from the same spot (the tee)
        Assert.Equal(m.Hole.Tee, m.Rounds[1].Ball);
        Assert.True(m.CanMulligan);            // Ann's can still be taken back before Bob hits
        Hit(m, GoodIron);                      // Bob on the green: the team plays his ball
        Assert.Equal(1, m.LastPick?.Player);
        Assert.Contains("Bob's ball", m.LastPick?.Message);
        Assert.Equal(m.Rounds[0].Save(), m.Rounds[1].Save());
        Assert.Equal(1, m.Rounds[0].Strokes);
        Assert.Equal(Lie.Green, m.Rounds[0].Lie);
        Assert.False(m.CanMulligan);           // the pick can't be taken back

        // Ann putts first from there; holing it finishes the team's hole without Bob putting.
        Assert.Equal(0, m.Turn);
        var o = Hit(m, Putt(m, 0.3), putt: true);   // inside the gimme: tapped in for 3
        Assert.True(o.Holed, o.Message);
        Assert.True(m.HoleComplete);
        Assert.Equal(3, m.SideScore(0, 1));
        Assert.Equal(3, m.Rounds[1].Scores[1]);
    }

    [Fact]
    public void ScrambleTeamsTakeTurns()
    {
        var m = new Match(TestCourse.Holes, null, Four, type: GameType.Scramble);
        m.StartHole(1);
        var order = new List<int>();
        for (int i = 0; i < 4; i++) { order.Add(m.Turn); Hit(m, i % 2 == 0 ? Topped : GoodIron); }
        Assert.Equal(new[] { 0, 1, 2, 3 }, order);   // both of team one, then both of team two
        Assert.All(m.Rounds, r => Assert.Equal(Lie.Green, r.Lie));
    }

    [Fact]
    public void BestBallCountsTheBetterScore()
    {
        var m = new Match(TestCourse.Holes, null, Two, type: GameType.BestBall);
        m.Resume(0, new (IReadOnlyList<int?>, HoleRound.State, int, int)[]
        {
            (new int?[] { 5, 3, null }, new HoleRound.State(2, TestCourse.Holes[2].Tee, Lie.Tee, 0, false, null), 0, 0),
            (new int?[] { 4, 4, null }, new HoleRound.State(2, TestCourse.Holes[2].Tee, Lie.Tee, 0, false, null), 0, 0),
        });
        Assert.Equal(4, m.SideScore(0, 0));
        Assert.Equal(3, m.SideScore(0, 1));
        Assert.Null(m.SideScore(0, 2));
        Assert.Equal((7, 0, 2), m.SideStanding(0));
        Assert.Equal("Ann & Bob 7 (E)", m.Summary());
    }

    private static Match MatchPlayed(int?[] ann, int?[] bob)
    {
        var m = new Match(TestCourse.Holes, null, Two, type: GameType.MatchPlay);
        int current = Array.IndexOf(ann, null);
        var at = TestCourse.Holes[Math.Max(0, current)].Tee;
        m.Resume(0, new (IReadOnlyList<int?>, HoleRound.State, int, int)[]
        {
            (ann, new HoleRound.State(Math.Max(0, current), at, Lie.Tee, 0, current < 0, ann[Math.Max(0, current)]), 0, 0),
            (bob, new HoleRound.State(Math.Max(0, current), at, Lie.Tee, 0, current < 0, bob[Math.Max(0, current)]), 0, 0),
        });
        return m;
    }

    [Fact]
    public void MatchPlayCountsHolesWon()
    {
        var dormie = MatchPlayed(new int?[] { 4, 3, null }, new int?[] { 5, 3, null });
        Assert.Equal(0, dormie.HoleWinner(0));
        Assert.Equal(-1, dormie.HoleWinner(1));
        Assert.Null(dormie.HoleWinner(2));
        Assert.Equal(new MatchState(1, 2, 1, false), dormie.MatchStatus());
        Assert.Equal("Ann 1 UP (dormie)", dormie.MatchText());
        Assert.Equal("1 DN", dormie.MatchTextFor(1));

        var won = MatchPlayed(new int?[] { 4, 2, null }, new int?[] { 5, 3, null });
        Assert.True(won.MatchStatus().Decided);
        Assert.Equal("Ann wins 2 & 1", won.MatchText());

        var square = MatchPlayed(new int?[] { 5, 3, 5 }, new int?[] { 4, 4, 5 });
        Assert.Equal("Match halved", square.MatchText());
        Assert.Equal("AS", square.MatchTextFor(0));
    }

    [Fact]
    public void ConditionsChangeThePhysicsTuning()
    {
        var t = PhysicsTuning.Defaults;
        var wet = new GameSetup(Weather: Weather.Wet, Stimp: 12, Fairways: FairwaySpeed.Fast, Greens: GreenFirmness.Firm).ApplyTo(t, 200);
        Assert.Equal(12, wet["ground.green.stimp"]);
        Assert.True(wet["ground.wetness"] > 0.5);
        Assert.True(wet["ground.fairway.rolling"] < t["ground.fairway.rolling"]);
        Assert.True(wet["ground.green.firmness"] > t["ground.green.firmness"]);
        Assert.Equal(200 / Units.FeetToMetres, wet["air.altitude"], 0);   // the course's own height
        Assert.Equal(t["air.temperature"], GameSetup.Default.ApplyTo(t, 0)["air.temperature"]);   // mild: as tuned
        Assert.Equal(5000, new GameSetup(Altitude: AltitudeSetting.Feet5000).ApplyTo(t, 200)["air.altitude"], 0);
        Assert.Equal(95, new GameSetup(Weather: Weather.Hot).ApplyTo(t, 0)["air.temperature"]);
    }

    [Fact]
    public void ThinnerAirCarriesFurther()
    {
        var shot = GrimesGolf.Physics.LaunchConditions.FromDisplayUnits(150, 12, 0, 2800, 0);
        double Carry(PhysicsTuning t)
        {
            var s = PhysicsSettings.From(t);
            return new GrimesGolf.Physics.GolfPhysicsEngine().SimulateShot(new GrimesGolf.Physics.SimulationRequest
            {
                Launch = shot, Ball = s.Ball, Environment = s.Environment,
            }).CarryYards;
        }
        double sea = Carry(new GameSetup(Altitude: AltitudeSetting.SeaLevel).ApplyTo(PhysicsTuning.Defaults, 0));
        double high = Carry(new GameSetup(Altitude: AltitudeSetting.Feet5000).ApplyTo(PhysicsTuning.Defaults, 0));
        Assert.InRange(high / sea, 1.03, 1.15);   // a few percent further a mile up
    }

    [Fact]
    public void SavedRoundKeepsItsGameAndHoles()
    {
        var setup = new GameSetup(GameType.Scramble, HoleSet.Par3s, WindStrength.Breezy);
        var holes = setup.Pick(TestCourse.Holes);
        var m = new Match(holes, null, Two, type: setup.Type);
        m.StartHole(0);
        Hit(m, Topped);   // Ann in the water, Bob still to hit
        var saved = SavedRound.From(m, "Test", DateTime.Now, setup);
        Assert.Equal(new[] { 2 }, saved.HoleNumbers);
        Assert.Equal(setup, saved.Game);

        var back = new Match(saved.HolesOf(TestCourse.Holes), null, saved.Golfers, saved.Rules, saved.Game.Type);
        Assert.True(saved.Fits(back.Holes));
        saved.ApplyTo(back);
        Assert.Equal(GameType.Scramble, back.Type);
        // Saved part-way through the team's stroke: it carries on from Ann's shot, the only one hit.
        Assert.Equal(back.Rounds[0].Save(), back.Rounds[1].Save());
        Assert.Equal(2, back.Rounds[1].Strokes);
    }
}
