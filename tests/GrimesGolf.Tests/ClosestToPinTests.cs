using GrimesGolf.Core;
using GrimesGolf.Core.Course;
using GrimesGolf.Core.Storage;
using Xunit;

namespace GrimesGolf.Tests;

public class ClosestToPinTests
{
    private static readonly Golfer[] Three = { new("Ann"), new("Bob"), new("Cat") };

    private static PinShot Shot(double metres) => new(metres, true, metres == 0, "");
    private static readonly PinShot Wet = new(null, false, false, "In the water");

    private static ClosestToPinGame Play(ShotOrder order, int shots, params double?[] metres)
    {
        var game = new ClosestToPinGame(Three, shots, order);
        foreach (var m in metres) game.Record(m is { } d ? Shot(d) : Wet);
        return game;
    }

    [Fact]
    public void TakingTurnsGoesRoundThePlayersEachShot()
    {
        var game = new ClosestToPinGame(Three, 2, ShotOrder.TakeTurns);
        var order = new List<(int Player, int Shot)>();
        while (!game.Complete)
        {
            order.Add((game.Turn, game.ShotNumber));
            game.Record(Shot(5));
        }
        Assert.Equal(new[] { (0, 1), (1, 1), (2, 1), (0, 2), (1, 2), (2, 2) }, order);
    }

    [Fact]
    public void AllInARowGivesEachPlayerTheirShotsTogether()
    {
        var game = new ClosestToPinGame(Three, 2, ShotOrder.AllInARow);
        var order = new List<(int Player, int Shot)>();
        while (!game.Complete)
        {
            order.Add((game.Turn, game.ShotNumber));
            game.Record(Shot(5));
        }
        Assert.Equal(new[] { (0, 1), (0, 2), (1, 1), (1, 2), (2, 1), (2, 2) }, order);
        Assert.Equal(6, game.TotalShots);
    }

    [Fact]
    public void ALonePlayerHasNoTurnsToTake()
    {
        var game = new ClosestToPinGame(new[] { new Golfer("Solo") }, 3, ShotOrder.TakeTurns);
        Assert.Equal(ShotOrder.AllInARow, game.Order);
        Assert.Equal(3, game.TotalShots);
    }

    [Fact]
    public void TheClosestShotWinsWhateverTheOrder()
    {
        // Ann 9 and 4 m, Bob 12 and 2 m, Cat 7 and 8 m.
        var game = Play(ShotOrder.TakeTurns, 2, 9, 12, 7, 4, 2, 8);
        Assert.True(game.Complete);
        Assert.Equal(new[] { 1 }, game.Winners);
        var s = game.Standings();
        Assert.Equal(new[] { 1, 0, 2 }, s.Select(x => x.Player));
        Assert.Equal(new[] { 1, 2, 3 }, s.Select(x => x.Place));
        Assert.Equal(2, s[0].Best!.Metres);
    }

    [Fact]
    public void ATieOnTheBestShotGoesToTheNextBest()
    {
        // Ann and Bob both have a 3 m shot; Bob's other shot is closer, so he wins.
        var game = Play(ShotOrder.AllInARow, 2, 3, 10, 3, 6, 20, 20);
        Assert.Equal(new[] { 1 }, game.Winners);
        Assert.Equal(new[] { 1, 0, 2 }, game.Standings().Select(x => x.Player));
        Assert.Equal(new[] { 1, 2, 3 }, game.Standings().Select(x => x.Place));
    }

    [Fact]
    public void PlayersWhoCantBeSeparatedShareTheLead()
    {
        var game = Play(ShotOrder.AllInARow, 1, 4, 4, 9);
        Assert.Equal(new[] { 0, 1 }, game.Winners);
        Assert.Equal(new[] { 1, 1, 3 }, game.Standings().Select(x => x.Place));
    }

    [Fact]
    public void ABallInTheWaterHasNoDistanceAndComesLast()
    {
        var game = Play(ShotOrder.TakeTurns, 1, null, 30, 15);
        Assert.Equal(new[] { 2, 1, 0 }, game.Standings().Select(x => x.Player));
        Assert.Null(game.Standings()[2].Best);
        Assert.Equal(new[] { 2 }, game.Winners);
        // A second shot that stays dry beats nothing at all.
        var again = Play(ShotOrder.TakeTurns, 2, null, 30, 15, 50, null, null);
        Assert.Equal(50, again.BestOf(0)!.Metres);
        Assert.Equal(new[] { 2, 1, 0 }, again.Standings().Select(x => x.Player));
    }

    [Fact]
    public void AHoleInOneIsDistanceZero()
    {
        var game = Play(ShotOrder.TakeTurns, 1, 4, 0, 1);
        Assert.Equal(new[] { 1 }, game.Winners);
        Assert.True(game.BestOf(1)!.Holed);
    }

    [Fact]
    public void StandingsMidGameCountOnlyWhatsBeenHit()
    {
        var game = Play(ShotOrder.TakeTurns, 3, 8, 6);
        Assert.False(game.Complete);
        Assert.Null(game.Winners);
        Assert.Equal(2, game.Turn);
        Assert.Equal(1, game.ShotNumber);
        Assert.Equal(new[] { 1, 0, 2 }, game.Standings().Select(x => x.Player));
        Assert.Throws<InvalidOperationException>(() => Play(ShotOrder.TakeTurns, 1, 1, 2, 3, 4));
    }

    [Fact]
    public void TheGameNeedsOneToSixPlayers()
    {
        var six = Enumerable.Range(1, 6).Select(i => new Golfer($"P{i}")).ToList();
        Assert.Equal(6, new ClosestToPinGame(six, 1, ShotOrder.TakeTurns).TotalShots);
        Assert.Throws<ArgumentException>(() => new ClosestToPinGame(six.Append(new Golfer("P7")).ToList(), 1, ShotOrder.TakeTurns));
        Assert.Throws<ArgumentException>(() => new ClosestToPinGame(Array.Empty<Golfer>(), 1, ShotOrder.TakeTurns));
    }

    [Fact]
    public void TheSetupFillsInBlankNamesAndKeepsToTheLimits()
    {
        var setup = new ClosestToPinSetup(Enumerable.Range(0, 9).Select(i => new Golfer(i == 1 ? " " : $"P{i}")).ToList(), ShotsEach: 40);
        Assert.Equal(ClosestToPinGame.MaxPlayers, setup.Golfers.Count);
        Assert.Equal("Player 2", setup.Golfers[1].Name);
        Assert.Equal(ClosestToPinSetup.MaxShots, setup.Shots);
        Assert.Equal("Player 1", new ClosestToPinSetup().Golfers.Single().Name);
    }

    [Fact]
    public void TheSetupIsKeptWithTheSettings()
    {
        var setup = new ClosestToPinSetup(new[] { new Golfer("Ann", true), new Golfer("Bob") }, 4, ShotOrder.AllInARow, "Deer Run", 7, 1,
                                          GameSetup.Default with { Wind = WindStrength.Breezy, Stimp = 12 });
        var dir = Path.Combine(Path.GetTempPath(), "ctp-" + Guid.NewGuid().ToString("N"));
        try
        {
            (AppSettings.Defaults with { ClosestToPin = setup }).Save(dir);
            var back = AppSettings.Load(dir).ClosestToPin!;
            Assert.Equal(4, back.ShotsEach);
            Assert.Equal(ShotOrder.AllInARow, back.Order);
            Assert.Equal("Deer Run", back.Course);
            Assert.Equal((7, 1), (back.HoleNumber, back.Tee));
            Assert.True(back.Golfers[0].LeftHanded);
            Assert.Equal(WindStrength.Breezy, back.Game.Wind);
            Assert.Equal(12, back.Game.Stimp);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        Assert.Null(AppSettings.Defaults.ClosestToPin);
    }

    [Theory]
    [InlineData(0.0, "0 in")]
    [InlineData(0.1, "4 in")]          // 3.9 in
    [InlineData(0.3048, "1 ft")]
    [InlineData(0.5, "1 ft 8 in")]     // 19.7 in
    [InlineData(3.6576, "12 ft")]
    [InlineData(3.7, "12 ft 2 in")]
    [InlineData(18.3, "20 yd")]        // just past 20 yd it is yards
    [InlineData(18.2, "59 ft 9 in")]   // and just short of it feet
    [InlineData(55, "60 yd")]
    public void DistancesAreReadTheWayAPinIsMeasured(double metres, string text) => Assert.Equal(text, PinShot.Format(metres));

    [Fact]
    public void AShotTellsHowItFinished()
    {
        Assert.Equal("Holed", new PinShot(0, true, true, "Holed!").Text);
        Assert.Equal("Water", Wet.Text);
        Assert.Equal("Out of bounds", new PinShot(null, false, false, "Out of bounds").Text);
        Assert.Equal("7 ft 4 in", new PinShot(2.24, true, false, "Green").Text);
    }

    // ---- shots on a real hole: the hand-made par 3 over the water ----

    private static readonly Hole Par3 = TestCourse.Holes[1];
    private static readonly HouseRules Strict = new(Mulligans: 0, GimmeMetres: 0);

    private static ShotOutcome HitFromTee(RangeShot shot)
    {
        var match = new Match(new[] { Par3 }, null, new[] { new Golfer("Ann") }, Strict);
        match.StartHole(0);
        return match.Play(shot, putt: false, match.Current.DefaultAimHeading());
    }

    [Fact]
    public void AShotOntoTheGreenScoresItsDistanceFromThePin()
    {
        var o = HitFromTee(new RangeShot(112, 18, 0, 7600, 0, null, null));
        var shot = PinShot.From(o, Par3);
        Assert.NotNull(shot.Metres);
        Assert.Equal(o.NextBall.DistanceTo(Par3.Pin), shot.Metres!.Value, 6);
        Assert.Equal(o.RestLie == Lie.Green, shot.OnGreen);
        Assert.False(shot.Holed);
    }

    [Fact]
    public void AShotInTheWaterHasNoDistance()
    {
        var shot = PinShot.From(HitFromTee(new RangeShot(70, 12, 0, 4000, 0, null, null)), Par3);
        Assert.Null(shot.Metres);
        Assert.Equal("In the water", shot.Note);
    }

    [Fact]
    public void StrictRulesDontTapInABallNearTheHole()
    {
        // With a gimme circle the ball would be "tapped in" and score 0; the game plays strict, so it keeps its distance.
        var match = new Match(new[] { Par3 }, null, new[] { new Golfer("Ann") }, Strict);
        match.StartHole(0);
        match.Current.Place(Par3.Pin + new P2(0, 0.5));
        var o = match.Play(new RangeShot(2, 0, 0, 0, 0, null, null), putt: true, HoleRound.Heading(match.Current.Ball, Par3.Pin));
        var shot = PinShot.From(o, Par3);
        Assert.False(o.Holed && shot.Metres > 0);
    }

    // ---- tee boxes ----

    private static Polygon Box(double x, double z, double half = 3) =>
        new(new P2[] { new(x - half, z - half), new(x + half, z - half), new(x + half, z + half), new(x - half, z + half) });

    private static Hole WithTees(params Shape[] tees) => Par3 with { Tee = new P2(0, 0), Pin = new P2(150, 0), TeeAim = new P2(150, 0), Tees = tees };

    [Fact]
    public void AHoleWithNoMappedTeesHasItsOneTee()
    {
        var tees = TeeBoxes.For(Par3);
        Assert.Equal("Tee", tees.Single().Name);
        Assert.Equal(Par3.Tee, tees[0].At);
        Assert.Equal(Par3.Tee.DistanceTo(Par3.Pin) * Units.MetresToYards, tees[0].Yards, 6);
    }

    [Fact]
    public void MappedTeesInLineWithTheHoleAreOfferedLongestFirst()
    {
        var hole = WithTees(Box(-1, 0), Box(25, 2), Box(50, -3));   // the second and third are forward of the first
        var tees = TeeBoxes.For(hole);
        Assert.Equal(new[] { "Back", "Middle", "Front" }, tees.Select(t => t.Name));
        Assert.True(tees[0].Yards > tees[1].Yards && tees[1].Yards > tees[2].Yards);
        Assert.Equal(0, TeeBoxes.DefaultIndex(hole, tees));
    }

    [Fact]
    public void TeesOfOtherHolesAreLeftOut()
    {
        // One far away, one beside the hole's tee but pointing a different way (its own pin is elsewhere).
        var tees = TeeBoxes.For(WithTees(Box(400, 0), Box(0, 70), Box(-1, 0)));
        Assert.Single(tees);
    }

    [Fact]
    public void ATeeAreaBelongsToTheHoleWhoseTeeIsNearestIt()
    {
        // A tee area in line with this par 3 but sitting at the next hole's tee is that hole's.
        var next = Par3 with { Number = 3, Tee = new P2(-45, 3) };
        var hole = WithTees(Box(-1, 0), Box(-44, 3));
        Assert.Equal(2, TeeBoxes.For(hole).Count);
        Assert.Single(TeeBoxes.For(hole, new[] { hole, next }));
    }

    [Fact]
    public void TeesCloseTogetherAreOne()
    {
        var tees = TeeBoxes.For(WithTees(Box(-1, 0), Box(1, 1), Box(30, 0)));
        Assert.Equal(2, tees.Count);
    }

    [Fact]
    public void PlayingFromAnotherTeeMovesTheTeeAndTheStartOfTheLine()
    {
        var hole = WithTees(Box(-1, 0), Box(40, 0)) with { Route = new[] { new P2(0, 0), new P2(150, 0) } };
        var tees = TeeBoxes.For(hole);
        var moved = TeeBoxes.Play(hole, tees[1]);
        Assert.Equal(tees[1].At, moved.Tee);
        Assert.Equal(tees[1].At, moved.Route![0]);
        Assert.Equal(hole.Pin, moved.Route[^1]);
        Assert.Equal(hole.Pin, moved.TeeAim);
        Assert.Equal(tees[1].Yards, moved.LengthYards, 0);
        Assert.Equal(Lie.Tee, moved.LieAt(moved.Tee));
        Assert.Same(hole, TeeBoxes.Play(hole, tees[0].At == hole.Tee ? tees[0] : tees.First(t => t.At == hole.Tee)));
    }

    [Fact]
    public void TheCourseTakesTheMovedHoleInPlaceOfTheOriginal()
    {
        var course = new GrimesGolf.Core.Course.Course("Test", TestCourse.Holes);
        var moved = Par3 with { Tee = new P2(30, 0) };
        var played = TeeBoxes.WithHole(course, Par3, moved);
        Assert.Equal(TestCourse.Holes.Count, played.Holes.Count);
        Assert.Same(moved, played.Holes.Single(h => h.Number == Par3.Number));
        Assert.Same(TestCourse.Holes[0], played.Holes[0]);
    }

    [Fact]
    public void DeerRunsParThreesHaveTeesToPickFrom()
    {
        var course = CourseLibrary.All.Single(e => e.Pack.Id == "deer-run").Load();
        var threes = course.Holes.Where(h => h.Par == 3).ToList();
        Assert.NotEmpty(threes);
        foreach (var hole in threes)
        {
            var tees = TeeBoxes.For(hole, course.Holes);
            Assert.NotEmpty(tees);
            Assert.Equal(tees.OrderByDescending(t => t.Yards).Select(t => t.Yards), tees.Select(t => t.Yards));
            // Every tee is a place a ball can be played from: it counts as the tee.
            foreach (var tee in tees) Assert.Equal(Lie.Tee, TeeBoxes.Play(hole, tee).LieAt(tee.At));
        }
    }
}
