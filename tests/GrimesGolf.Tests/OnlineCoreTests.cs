using GrimesGolf.Core;
using GrimesGolf.Core.Course;
using GrimesGolf.Core.Storage;
using Xunit;

namespace GrimesGolf.Tests;

/// <summary>The core pieces online play is built on: snapshots, players away, the course fingerprint, the online save.</summary>
public class OnlineCoreTests
{
    private static readonly Golfer[] Two = { new("Ann"), new("Bob") };
    private static readonly Golfer[] Four = { new("Ann"), new("Bob"), new("Cat"), new("Dee") };
    private static readonly RangeShot Topped = new(70, 12, 0, 4000, 0, null, null);
    private static readonly RangeShot GoodIron = new(112, 18, 0, 7600, 0, null, null);
    private static readonly RangeShot Drive = new(150, 11, 0, 2700, 0, null, null);

    private static ShotOutcome Hit(Match m, RangeShot s, bool putt = false)
    {
        var o = m.Play(s, putt, m.Current.DefaultAimHeading());
        m.AdvanceTurn();
        return o;
    }

    private static bool PuttNext(Match m) => m.Current.DistanceToPin < 40;

    private static RangeShot NextShot(Match m) =>
        PuttNext(m)
            ? new RangeShot(Math.Sqrt(2 * 0.55 * Math.Max(0.1, m.Current.DistanceToPin - 0.3)) * Units.MsToMph, 1, 0, 0, 0, null, null)
            : m.Current.DistanceToPin > 180 ? Drive : GoodIron;

    [Fact]
    public void MidScrambleSnapshotSurvivesJsonAndPlaysOnTheSame()
    {
        var a = new Match(TestCourse.Holes, null, Four, new HouseRules(Mulligans: 2), GameType.Scramble);
        a.StartHole(1);
        a.SetWind(new CourseWind(9, 1.2, 3));
        Hit(a, Topped);          // Ann in the water: her team's stroke is part-way through
        Assert.True(a.CanMulligan);

        var json = a.Capture().ToJson();
        var snap = MatchSnapshot.FromJson(json);
        Assert.Equal(json, snap.ToJson());

        var b = new Match(TestCourse.Holes, null, Four, new HouseRules(Mulligans: 2), GameType.Scramble);
        b.Restore(snap);
        Assert.True(a.Capture().SameAs(b.Capture()));
        Assert.Equal(a.Turn, b.Turn);
        Assert.True(b.CanMulligan);              // the mulligan state comes across too
        Assert.Equal(a.Wind, b.Wind);

        // Both go on the same from here: the pick, the putts, the next hole.
        for (int i = 0; i < 60 && !a.HoleComplete; i++)
        {
            var s = NextShot(a);
            bool putt = PuttNext(a);
            var oa = Hit(a, s, putt);
            var ob = Hit(b, s, putt);
            Assert.Equal(oa.Message, ob.Message);
            Assert.True(a.Capture().SameAs(b.Capture()));
        }
        Assert.True(a.HoleComplete);
        a.StartHole(2);
        b.StartHole(2);
        Assert.True(a.Capture().SameAs(b.Capture()));
    }

    [Fact]
    public void RestoreLeavesTheSameRoundObjects()
    {
        var m = new Match(TestCourse.Holes, null, Two);
        var round = m.Rounds[1];
        var other = new Match(TestCourse.Holes, null, Two);
        other.StartHole(2);
        Hit(other, Drive);
        m.Restore(other.Capture());
        Assert.Same(round, m.Rounds[1]);
        Assert.Equal(2, m.HoleIndex);
        Assert.Equal(1, m.Rounds[0].Strokes);
    }

    [Fact]
    public void RestoreRejectsAnotherLineUp()
    {
        var two = new Match(TestCourse.Holes, null, Two);
        var four = new Match(TestCourse.Holes, null, Four);
        Assert.Throws<ArgumentException>(() => two.Restore(four.Capture()));
    }

    [Fact]
    public void AwayPlayerIsSkippedAndPicksUp()
    {
        var m = new Match(TestCourse.Holes, null, Two);
        m.StartHole(0);
        Assert.Equal(0, m.Turn);
        m.SetAway(0, true);
        Assert.True(m.IsAway(0));
        Assert.Equal(1, m.Turn);
        Assert.True(m.Rounds[0].PickedUp);
        Assert.Equal(8, m.Rounds[0].Scores[0]);   // double par on the par 4

        // Still away on the next hole: picked up straight away there too.
        m.StartHole(1);
        Assert.Equal(1, m.Turn);
        Assert.Equal(6, m.Rounds[0].Scores[1]);

        // Back: plays from the next hole on.
        m.SetAway(0, false);
        m.StartHole(2);
        Assert.False(m.Rounds[0].Holed);
        Assert.Contains(m.Turn, new[] { 0, 1 });
    }

    [Fact]
    public void SkipScoreKeepsStrokesAlreadyTakenAboveDoublePar()
    {
        var r = new HoleRound(TestCourse.Holes);
        r.StartHole(1);   // par 3
        for (int i = 0; i < 7; i++) r.Play(Topped, false, r.DefaultAimHeading());
        Assert.True(r.Strokes > 6);
        int strokes = r.Strokes;
        r.PickUpHole(r.SkipScore);
        Assert.Equal(strokes, r.Scores[1]);
        Assert.True(r.PickedUp);
    }

    [Fact]
    public void ScrambleDoesNotWaitForAnAwayTeammate()
    {
        var m = new Match(TestCourse.Holes, null, Two, type: GameType.Scramble);
        m.StartHole(1);
        Hit(m, GoodIron);         // Ann hits; Bob's turn
        Assert.Equal(1, m.Turn);
        m.SetAway(1, true);       // Bob drops: the pick is made with Ann's shot
        Assert.NotNull(m.LastPick);
        Assert.Equal(m.Rounds[0].Save(), m.Rounds[1].Save());
        Assert.Equal(0, m.Turn);

        // Putting out without Bob finishes the team's hole, Bob included.
        for (int i = 0; i < 8 && !m.HoleComplete; i++) Hit(m, NextShot(m), putt: PuttNext(m));
        Assert.True(m.HoleComplete);
        Assert.Equal(m.Rounds[0].Scores[1], m.Rounds[1].Scores[1]);
    }

    [Fact]
    public void ScrambleTeamWithNobodyLeftPicksUp()
    {
        var m = new Match(TestCourse.Holes, null, Four, type: GameType.Scramble);
        m.StartHole(0);
        m.SetAway(0, true);
        m.SetAway(1, true);
        Assert.True(m.Rounds[0].PickedUp && m.Rounds[1].PickedUp);
        Assert.Contains(m.Turn, new[] { 2, 3 });
    }

    [Fact]
    public void FingerprintIsStableAndNoticesChanges()
    {
        var holes = TestCourse.Holes;
        Assert.Equal(CourseFingerprint.Of(holes, null), CourseFingerprint.Of(holes.ToList(), null));
        var moved = holes.Select((h, i) => i == 1 ? h with { Pin = h.Pin + new P2(1, 0) } : h).ToList();
        Assert.NotEqual(CourseFingerprint.Of(holes, null), CourseFingerprint.Of(moved, null));
        var deerRun = CourseLibrary.All[0].Load();
        Assert.Equal(CourseFingerprint.Of(deerRun), CourseFingerprint.Of(CourseLibrary.All[0].Load()));
    }

    [Fact]
    public void ShotSourceDefaults()
    {
        Assert.Equal(ShotSource.Screen, GoodIron.Source);
        Assert.Equal(ShotSource.Demo, RangeShot.Demo(ClubPresets.All[0], new Random(1)).Source);
        Assert.False((GoodIron with { BallMph = double.NaN }).IsFinite);
        Assert.True(GoodIron.IsFinite);
    }

    private sealed record OnlineSave(string Code, MatchSnapshot Snapshot);

    [Fact]
    public void OnlineRoundIsKeptApartFromTheOfflineOne()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gg-online-" + Guid.NewGuid().ToString("N"));
        try
        {
            var data = new GolfData(dir);
            var offline = new Match(TestCourse.Holes, null, Two);
            offline.StartHole(0);
            data.SaveRound(SavedRound.From(offline, "Test", DateTime.Now));
            Assert.True(File.Exists(data.CurrentRoundFile));

            // An online round, finished and filed: the offline save is untouched.
            var online = new Match(TestCourse.Holes, null, Two);
            data.SaveOnlineRound(new OnlineSave("ABCDE", online.Capture()));
            Assert.Equal("ABCDE", data.LoadOnlineRound<OnlineSave>()?.Code);
            data.FileRound(SavedRound.From(online, "Test online", DateTime.Now));
            data.ClearOnlineRound();
            Assert.True(File.Exists(data.CurrentRoundFile));
            Assert.False(File.Exists(data.OnlineRoundFile));
            Assert.Single(Directory.GetFiles(data.RoundsFolder));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }
}
