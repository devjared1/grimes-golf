using GrimesGolf.Core;
using GrimesGolf.Core.Course;
using GrimesGolf.Core.Storage;
using Xunit;

namespace GrimesGolf.Tests;

public class HouseRulesTests
{
    /// <summary>Lands ~90 yd on the par 3: in the pond (2 strokes each, replayed from the tee).</summary>
    private static readonly RangeShot Topped = new(70, 12, 0, 4000, 0, null, null);
    private static readonly RangeShot GoodIron = new(112, 18, 0, 7600, 0, null, null);

    private static HoleRound OnParThree(HouseRules rules)
    {
        var round = new HoleRound(TestCourse.Holes, rules: rules);
        round.StartHole(1);
        return round;
    }

    private static ShotOutcome Hit(HoleRound r, RangeShot s, bool putt = false) => r.Play(s, putt, r.DefaultAimHeading());

    private static RangeShot PuttStopping(double metresShort, HoleRound r) =>
        new(Math.Sqrt(2 * 0.55 * (r.DistanceToPin - metresShort)) * Units.MsToMph, 1, 0, 0, 0, null, null);

    [Fact]
    public void PicksUpAtDoublePar()
    {
        var round = OnParThree(new HouseRules(PickUpAtDoublePar: true));
        Hit(round, Topped);
        Hit(round, Topped);
        Assert.False(round.Holed);
        var o = Hit(round, Topped);   // 6 strokes: double par on a par 3
        Assert.True(o.PickedUp && o.HoleOver && !o.Holed);
        Assert.True(round.Holed && round.PickedUp);
        Assert.Equal(6, round.Scores[1]);
        Assert.Contains("Picked up", o.Message);
        Assert.Throws<InvalidOperationException>(() => Hit(round, GoodIron));
    }

    [Fact]
    public void StrictRulesPlayOnPastDoublePar()
    {
        var round = OnParThree(HouseRules.Strict);
        for (int i = 0; i < 3; i++) Hit(round, Topped);
        Assert.Equal(6, round.Strokes);
        Assert.False(round.Holed);
        Assert.False(round.CanMulligan);
    }

    [Fact]
    public void MulliganTakesBackTheShotAndPenalty()
    {
        var round = OnParThree(new HouseRules(Mulligans: 1));
        Hit(round, Topped);
        Assert.Equal(2, round.Strokes);
        Assert.True(round.CanMulligan);
        Assert.True(round.Mulligan());
        Assert.Equal(0, round.Strokes);
        Assert.Equal(round.Hole.Tee, round.Ball);
        Assert.Equal(Lie.Tee, round.Lie);
        Assert.Equal(0, round.MulligansLeft);

        Hit(round, Topped);
        Assert.False(round.Mulligan());   // none left
        Assert.Equal(2, round.Strokes);
    }

    [Fact]
    public void OnlyTheLastShotCanBeTakenBackAndNotAHoledOne()
    {
        var round = OnParThree(new HouseRules(Mulligans: 3));
        Hit(round, GoodIron);
        var onGreen = round.Ball;
        Hit(round, PuttStopping(-4, round), putt: true);   // races past
        Assert.True(round.Mulligan());
        Assert.Equal(onGreen, round.Ball);
        Assert.False(round.CanMulligan);                   // only one step back

        Hit(round, PuttStopping(0.3, round), putt: true);  // tapped in
        Assert.True(round.Holed);
        Assert.False(round.CanMulligan);
        Assert.Equal(2, round.MulligansLeft);
    }

    [Fact]
    public void MulliganOnThePickUpShotReopensTheHole()
    {
        var round = OnParThree(new HouseRules(Mulligans: 1, PickUpAtDoublePar: true));
        for (int i = 0; i < 3; i++) Hit(round, Topped);
        Assert.True(round.PickedUp);
        Assert.True(round.Mulligan());
        Assert.False(round.Holed || round.PickedUp);
        Assert.Equal(4, round.Strokes);
        Assert.Null(round.Scores[1]);
    }

    [Fact]
    public void GimmeDistanceIsAHouseRule()
    {
        var holeOut = OnParThree(new HouseRules(GimmeMetres: 0));
        Hit(holeOut, GoodIron);
        Hit(holeOut, PuttStopping(0.3, holeOut), putt: true);
        Assert.False(holeOut.Holed);

        var generous = OnParThree(new HouseRules(GimmeMetres: 1.5));
        Hit(generous, GoodIron);
        var o = Hit(generous, PuttStopping(1.0, generous), putt: true);
        Assert.True(o.Holed, o.Message);
        Assert.Equal(3, generous.Strokes);
    }

    private const double SixFeet = 6 * Units.FeetToMetres;

    [Fact]
    public void AutoPuttsComeFromTheCircles()
    {
        var rules = new HouseRules(GimmeMetres: SixFeet, AutoPutt: true);
        var random = new Random(1);
        Assert.Equal(2 * SixFeet, rules.SecondCircleMetres);
        Assert.Equal(1, rules.AutoPutts(1.5, random));     // inside 6 ft
        Assert.Equal(2, rules.AutoPutts(3.0, random));     // between 6 and 12 ft
        var far = Enumerable.Range(0, 200).Select(_ => rules.AutoPutts(10, random)).ToList();
        Assert.All(far, p => Assert.InRange(p, 2, 3));
        Assert.Contains(2, far);
        Assert.Contains(3, far);
    }

    [Fact]
    public void AutoPuttingFinishesTheHoleWhenTheBallStopsOnTheGreen()
    {
        var rules = new HouseRules(GimmeMetres: SixFeet, AutoPutt: true);
        var round = OnParThree(rules);
        var o = Hit(round, GoodIron);
        Assert.Equal(Lie.Green, o.RestLie);
        Assert.True(o.Holed && round.Holed, o.Message);
        double left = o.Rest.DistanceTo(round.Hole.Pin);
        int putts = round.Strokes - 1;
        if (left <= SixFeet) Assert.Equal(1, putts);
        else if (left <= 2 * SixFeet) Assert.Equal(2, putts);
        else Assert.InRange(putts, 2, 3);
        Assert.Equal(round.Strokes, round.Scores[1]);

        // The same shot always takes the same putts (a replay, or another PC).
        var again = OnParThree(rules);
        Hit(again, GoodIron);
        Assert.Equal(round.Strokes, again.Strokes);
    }

    [Fact]
    public void AutoPuttingStillPicksUpAtDoublePar()
    {
        // A tiny gimme circle, so the iron onto the green needs 2 or 3 putts: 4 + 1 + 2 is past double par (6).
        var round = OnParThree(new HouseRules(GimmeMetres: 0.05, PickUpAtDoublePar: true, AutoPutt: true));
        Hit(round, Topped);
        Hit(round, Topped);
        var o = Hit(round, GoodIron);
        Assert.True(o.PickedUp && !o.Holed, o.Message);
        Assert.Equal(6, round.Scores[1]);
    }

    [Fact]
    public void SettingsFromBeforeTheGimmeCircleGetTheNewDefault()
    {
        var old = AppSettings.Defaults with { HouseRules = new HouseRules(Mulligans: 1, GimmeMetres: HouseRules.DefaultGimmeMetres) };
        Assert.Equal(6.0, old.Upgraded().Rules.GimmeFeet, 6);
        Assert.Equal(1, old.Upgraded().Rules.Mulligans);
        var chosen = AppSettings.Defaults with { HouseRules = new HouseRules(GimmeMetres: 10 * Units.FeetToMetres) };
        Assert.Equal(chosen, chosen.Upgraded());
        Assert.Equal(HouseRules.DefaultGimmeFeet, AppSettings.GarageRules.GimmeFeet, 6);
    }
}

public class GolfDataTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sq-golfdata-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static readonly Golfer[] Two = { new("Jared"), new("Mike", LeftHanded: true) };

    [Fact]
    public void MatchInProgressResumesWhereItLeftOff()
    {
        var rules = new HouseRules(Mulligans: 2, PickUpAtDoublePar: true);
        var match = new Match(TestCourse.Holes, null, Two, rules);
        match.StartHole(0);
        match.Play(new RangeShot(150, 11, 0, 2700, 0, null, null), false, match.Current.DefaultAimHeading());
        match.Mulligan();
        match.Play(new RangeShot(150, 11, 0, 2700, 0, null, null), false, match.Current.DefaultAimHeading());
        match.AdvanceTurn();   // Mike to tee off
        var started = new DateTime(2026, 11, 2, 15, 30, 0);

        var data = new GolfData(_dir);
        string? error = null;
        data.Error += e => error = e;
        data.SaveRound(SavedRound.From(match, "Test holes", started));

        var saved = new GolfData(_dir).LoadCurrentRound();
        Assert.NotNull(saved);
        Assert.True(saved!.Fits(TestCourse.Holes));
        Assert.Equal(rules, saved.Rules);
        Assert.Equal(Two, saved.Golfers);
        var resumed = new Match(TestCourse.Holes, null, saved.Golfers, saved.Rules);
        saved.ApplyTo(resumed);
        Assert.Equal(1, resumed.Turn);
        Assert.Equal(match.Rounds[0].Ball, resumed.Rounds[0].Ball);
        Assert.Equal(match.Rounds[0].Lie, resumed.Rounds[0].Lie);
        Assert.Equal(1, resumed.Rounds[0].Strokes);
        Assert.Equal(1, resumed.Rounds[0].MulligansLeft);
        Assert.Equal(0, resumed.Rounds[1].Strokes);
        Assert.Equal(2, resumed.Rounds[1].MulligansLeft);
        Assert.Equal(started, saved.Started);
        Assert.Null(error);
    }

    [Fact]
    public void FinishedRoundIsFiledAndNoLongerCurrent()
    {
        var data = new GolfData(_dir);
        var match = new Match(TestCourse.Holes, null, Two);
        data.SaveRound(SavedRound.From(match, "Test holes", DateTime.Now));
        Assert.True(File.Exists(data.CurrentRoundFile));

        var saved = SavedRound.From(match, "Test holes", DateTime.Now);
        var done = saved with { Players = new[] { saved.Players[0] with { Scores = new int?[] { 4, 3, 6 } }, saved.Players[1] with { Scores = new int?[] { 5, 3, 5 } } } };
        data.SaveRound(done);
        Assert.False(File.Exists(data.CurrentRoundFile));
        Assert.Null(data.LoadCurrentRound());
        var filed = Assert.Single(data.LoadRounds());
        Assert.Equal(new int?[] { 4, 3, 6 }, filed.Players[0].Scores);
        Assert.Equal(13, filed.Players[1].Total);
        Assert.Single(data.LoadRounds("mike"));
        Assert.Empty(data.LoadRounds("Sam"));
    }

    [Fact]
    public void SaveForAChangedCourseDoesNotFit()
    {
        var saved = SavedRound.From(new Match(TestCourse.Holes, null, Two), "Test holes", DateTime.Now);
        Assert.False(saved.Fits(TestCourse.Holes.Take(2).ToList()));
        Assert.False((saved with { Pars = new[] { 4, 3, 4 } }).Fits(TestCourse.Holes));
    }

    [Fact]
    public void ShotsAppendAsJsonLines()
    {
        var data = new GolfData(_dir, new DateTime(2026, 11, 2, 9, 0, 0));
        var shot = new RangeShot(120, 16, -1.5, 6500, 3, 87, 1.38);
        var f = new FlightResult(Array.Empty<FlightPoint>(), 0, 160, 172, 30, -4, 6, 45);
        data.AppendShot(ShotRecord.Of(shot, f, "Jared", "I7", false));
        var onCourse = ShotRecord.Of(shot, f, "Jared", "I7", false) with
        {
            At = new DateTime(2026, 11, 2, 9, 5, 0), Course = "Test holes", Hole = 2, Stroke = 1, Lie = Lie.Tee, RestLie = Lie.Green,
        };
        data.AppendShot(onCourse);
        data.AppendShot(onCourse with { Mulligan = true });   // taken back
        File.AppendAllText(data.SessionFile, "{\"At\":\"2026-11-02T09:");   // cut short by a crash

        var shots = data.LoadShots();
        Assert.Equal(2, shots.Count);
        Assert.Equal(160, shots[0].CarryYards);
        Assert.Null(shots[0].Course);
        Assert.False(shots[0].Mulligan);
        Assert.Equal(Lie.Green, shots[1].RestLie);
        Assert.True(shots[1].Mulligan);
        Assert.Contains("\"RestLie\":\"Green\"", File.ReadAllText(data.SessionFile));
    }

    [Fact]
    public void SettingsAreCreatedWithGarageRules()
    {
        var s = AppSettings.Load(_dir);
        Assert.Equal(AppSettings.GarageRules, s.Rules);
        Assert.Equal("Player 1", Assert.Single(s.Golfers).Name);
        Assert.True(File.Exists(Path.Combine(_dir, AppSettings.FileName)));
        File.WriteAllText(Path.Combine(_dir, AppSettings.FileName),
            "{ \"Players\": [ { \"Name\": \" Mike \", \"LeftHanded\": true }, { \"Name\": \"\" } ], \"HouseRules\": { \"Mulligans\": 0, \"GimmeMetres\": 0.9 }, \"TextSize\": \"Large\" }");
        var edited = AppSettings.Load(_dir);
        Assert.Equal(new[] { new Golfer("Mike", true), new Golfer("Player 2") }, edited.Golfers);
        Assert.Equal(new HouseRules(0, 0.9, false), edited.Rules);
        Assert.Equal(1.15f, edited.UiScale);

        (edited with { LastMonitor = 0xC0FFEE, CameraFollow = false }).Save(_dir);
        var again = AppSettings.Load(_dir);
        Assert.Equal(0xC0FFEEUL, again.LastMonitor);
        Assert.False(again.CameraFollow);
        Assert.Equal(TextSize.Large, again.TextSize);
        Assert.Equal(0.85f, (again with { TextSize = TextSize.Small }).UiScale);
        Assert.True(again.HoleFlyover);   // on unless switched off
    }
}

public class MatchTests
{
    private static readonly Golfer[] Three = { new("Ann"), new("Bob"), new("Cat", LeftHanded: true) };
    private static readonly RangeShot Topped = new(70, 12, 0, 4000, 0, null, null);    // into the pond on the par 3
    private static readonly RangeShot GoodIron = new(112, 18, 0, 7600, 0, null, null); // onto the par-3 green
    private static readonly RangeShot Short = new(95, 22, 0, 7500, 0, null, null);     // front of the par-3 green, ~15 yd

    private static ShotOutcome Hit(Match m, RangeShot s, bool putt = false)
    {
        var o = m.Play(s, putt, m.Current.DefaultAimHeading());
        m.AdvanceTurn();
        return o;
    }

    private static RangeShot Putt(Match m, double metresShort) =>
        new(Math.Sqrt(2 * 0.55 * (m.Current.DistanceToPin - metresShort)) * Units.MsToMph, 1, 0, 0, 0, null, null);

    [Fact]
    public void EveryoneTeesOffThenFarthestPlays()
    {
        var m = new Match(TestCourse.Holes, null, Three);
        m.StartHole(1);
        Assert.Equal(0, m.Turn);
        Hit(m, GoodIron);                 // Ann on the green
        Assert.Equal(1, m.Turn);
        Hit(m, Short);                    // Bob on the front
        Assert.Equal(2, m.Turn);
        Assert.True(m.Golfer.LeftHanded);
        Hit(m, Topped);                   // Cat in the water: back on the tee, 2 strokes
        Assert.Equal(2, m.Turn);          // farthest away: plays again
        Hit(m, Short);
        // Ann is farthest of the three on the green (Cat's ball is exactly where Bob's is: honour order breaks the tie).
        var far = Enumerable.Range(0, 3).OrderByDescending(p => Math.Round(m.Rounds[p].DistanceToPin, 2)).First();
        Assert.Equal(far, m.Turn);
        Assert.Equal(m.Rounds[1].Ball, m.Rounds[2].Ball);
        Assert.NotEqual(2, m.Turn);
        Assert.False(m.HoleComplete);
    }

    [Fact]
    public void PlayersCanJoinAndLeavePartWayThrough()
    {
        var m = new Match(TestCourse.Holes, null, Three);
        m.StartHole(1);
        Hit(m, GoodIron);                 // Ann on the green
        var ann = m.Rounds[0].Save();

        // Bob leaves, Dee joins: Ann and Cat carry on as they were, Dee starts on this hole's tee.
        var g = m.Regroup(new Golfer[] { new("Ann"), new("Cat", true), new("Dee") }, new int?[] { 0, 2, null });
        Assert.Equal(ann, g.Rounds[0].Save());
        Assert.Equal(1, g.HoleIndex);
        Assert.Equal(1, g.Rounds[2].FirstHole);
        Assert.Equal(0, g.Rounds[2].Strokes);
        Assert.Equal(1, g.Turn);          // Cat still to tee off, before Dee
        Assert.True(g.Players[1].LeftHanded);

        // Dee's round is complete without hole 1 (index 0), which she never played.
        var saved = SavedRound.From(g, "Test", DateTime.Now);
        Assert.Equal(1, saved.Players[2].FirstHole);
        var back = new Match(TestCourse.Holes, null, saved.Golfers);
        saved.ApplyTo(back);
        Assert.Equal(1, back.Rounds[2].FirstHole);
        Assert.False(back.Rounds[2].RoundComplete);
    }

    [Fact]
    public void HonourGoesToTheLowestScoreOnTheLastHole()
    {
        var m = new Match(TestCourse.Holes, null, Three, new HouseRules(PickUpAtDoublePar: true));
        m.StartHole(1);
        int guard = 0;
        while (!m.HoleComplete && guard++ < 40)
        {
            int p = m.Turn;
            var r = m.Current;
            if (r.Lie == Lie.Green) Hit(m, Putt(m, 0.3), putt: true);   // tap-in range
            else Hit(m, p == 0 ? Topped : GoodIron);                    // Ann keeps finding the water: picks up
        }
        Assert.True(m.HoleComplete);
        Assert.True(m.Rounds[0].PickedUp);
        Assert.Equal(6, m.Rounds[0].Scores[1]);
        Assert.Equal(3, m.Rounds[1].Scores[1]);

        m.StartHole(2);
        Assert.Equal(new[] { 1, 2, 0 }, m.TeeOrder);   // Bob and Cat tied on 3 keep their order; Ann last
        Assert.Equal(1, m.Turn);
        Assert.Null(m.LastToPlay);
        Assert.Equal((6, 3, 1), m.Standing(0));
    }

    [Fact]
    public void MulliganBelongsToWhoeverHitLast()
    {
        var m = new Match(TestCourse.Holes, null, Three, new HouseRules(Mulligans: 1));
        m.StartHole(1);
        Hit(m, Topped);                   // Ann in the water
        Assert.Equal(1, m.Turn);          // Bob's turn now...
        Assert.True(m.CanMulligan);
        Assert.True(m.Mulligan());        // ...but Ann takes hers back and plays again
        Assert.Equal(0, m.Turn);
        Assert.Equal(0, m.Rounds[0].Strokes);
        Hit(m, GoodIron);
        Hit(m, Topped);                   // Bob in the water
        Assert.Equal(1, m.LastToPlay);
        Hit(m, GoodIron);                 // Cat tees off: Bob's shot can't be taken back any more
        Assert.Equal(2, m.LastToPlay);
        Assert.True(m.Mulligan());        // Cat's can
        Assert.Equal(1, m.Rounds[1].MulligansLeft);
        Assert.Equal(0, m.Rounds[0].MulligansLeft);
    }

    [Fact]
    public void StandingsCountHolesFinished()
    {
        var m = new Match(TestCourse.Holes, null, Three);
        m.StartHole(1);
        Hit(m, GoodIron);
        var standings = ShotFinished.Standings(m);
        Assert.Equal(3, standings.Count);
        Assert.Equal(1, standings[0].HoleStrokes);
        Assert.Equal(0, standings[0].Thru);
    }

    [Fact]
    public void MatchNeedsOneToFourPlayers()
    {
        Assert.Throws<ArgumentException>(() => new Match(TestCourse.Holes, null, Array.Empty<Golfer>()));
        Assert.Throws<ArgumentException>(() => new Match(TestCourse.Holes, null, Enumerable.Range(1, 5).Select(i => new Golfer($"P{i}")).ToList()));
    }
}
