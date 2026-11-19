using GrimesGolf.Core;
using GrimesGolf.Core.Course;
using Xunit;
using Xunit.Abstractions;

namespace GrimesGolf.Tests;

public class PracticeGreenTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Kinds => new[] { new object[] { PracticeKind.Putting }, new object[] { PracticeKind.Chipping } };

    [Fact]
    public void PuttingGreenHasSixHolesAndChippingGreenFour()
    {
        Assert.Equal(6, PracticeGreen.Of(PracticeKind.Putting).Holes.Count);
        Assert.Equal(4, PracticeGreen.Of(PracticeKind.Chipping).Holes.Count);
    }

    [Theory, MemberData(nameof(Kinds))]
    public void EveryHoleSitsWellInsideTheGreenAndApartFromTheOthers(PracticeKind kind)
    {
        var g = PracticeGreen.Of(kind);
        var green = g.Holes[0].Greens.Single();
        foreach (var hole in g.Holes)
        {
            Assert.Equal(Lie.Green, hole.LieAt(hole.Pin));
            Assert.True(green.DistanceTo(hole.Pin) < -3, $"{hole.Name} is {-green.DistanceTo(hole.Pin):0.0} m inside the edge");
            // A cup's neighbours are far enough off that a putt at one doesn't roll over another.
            foreach (var other in g.Holes.Where(h => h != hole))
                Assert.True(hole.Pin.DistanceTo(other.Pin) > 5, $"{hole.Name} and {other.Name} are {hole.Pin.DistanceTo(other.Pin):0.0} m apart");
            // The aim starts at the cup, not at a tee shot's landing area.
            Assert.Equal(hole.Pin, hole.TeeAim);
        }
        Assert.Equal(g.Holes.Count, g.Holes.Select(h => h.Number).Distinct().Count());
    }

    [Theory, MemberData(nameof(Kinds))]
    public void EverySpotIsWhereItSaysAndFarFromTheTee(PracticeKind kind)
    {
        var g = PracticeGreen.Of(kind);
        Assert.InRange(g.Spots.Count, 8, 14);
        foreach (var spot in g.Spots)
        {
            var hole = g.Holes[0];
            Assert.True(g.CanPlace(spot.At), spot.Name);
            Assert.Equal(spot.Lie, hole.LieAt(spot.At));
            // (Close to a tee a ball would be "on the tee": level and teed up.)
            Assert.True(spot.At.DistanceTo(hole.Tee) > Hole.TeeBoxRadius + Hole.FirstCutWidth + 50, spot.Name);
            Assert.All(g.Holes, h => Assert.True(spot.At.DistanceTo(h.Pin) > 1, $"{spot.Name} is on top of {h.Name}"));
        }
        Assert.Equal(g.Spots.Count, g.Spots.Select(s => s.Name).Distinct().Count());
    }

    [Fact]
    public void PuttingSpotsAreAllOnTheGreen()
    {
        var g = PracticeGreen.Of(PracticeKind.Putting);
        Assert.All(g.Spots, s => Assert.Equal(Lie.Green, s.Lie));
    }

    [Fact]
    public void ChippingGreenOffersEveryLieIncludingSand()
    {
        var lies = PracticeGreen.Of(PracticeKind.Chipping).Spots.Select(s => s.Lie).ToHashSet();
        Assert.Contains(Lie.Bunker, lies);
        Assert.Contains(Lie.Fairway, lies);
        Assert.Contains(Lie.Fringe, lies);
        Assert.Contains(Lie.Rough, lies);
        Assert.Contains(Lie.DeepRough, lies);
        Assert.Equal(2, PracticeGreen.Of(PracticeKind.Chipping).Holes[0].Bunkers.Count);
    }

    [Theory, MemberData(nameof(Kinds))]
    public void BunkersAndHolesKeepClearOfEachOther(PracticeKind kind)
    {
        var g = PracticeGreen.Of(kind);
        var hole = g.Holes[0];
        foreach (var bunker in hole.Bunkers)
            foreach (var green in hole.Greens)
            {
                // The sand starts well past the fringe, so nothing about the green's edge is sand.
                var p = ((Ellipse)bunker).Outline(120);
                Assert.True(p.All(q => green.DistanceTo(q) > Hole.FringeWidth + 0.8), "a bunker is touching the fringe");
            }
    }

    [Theory, MemberData(nameof(Kinds))]
    public void GreenSlopesBreakPuttsWithoutBeingAbsurd(PracticeKind kind)
    {
        var g = PracticeGreen.Of(kind);
        var terrain = g.Course.Terrain!;
        var green = g.Holes[0].Greens.Single();
        double steepest = 0, flattest = double.MaxValue;
        var box = green.Box;
        for (double x = box.Min.X; x <= box.Max.X; x += 0.5)
            for (double z = box.Min.Z; z <= box.Max.Z; z += 0.5)
            {
                var p = new P2(x, z);
                if (green.DistanceTo(p) > -0.5) continue;
                var s = terrain.Gradient(p);
                double slope = Math.Sqrt(s.X * s.X + s.Z * s.Z);
                steepest = Math.Max(steepest, slope);
                flattest = Math.Min(flattest, slope);
            }
        output.WriteLine($"{kind}: steepest {steepest * 100:0.0}%, flattest {flattest * 100:0.0}%");
        Assert.InRange(steepest, 0.02, 0.045);
        foreach (var hole in g.Holes)
        {
            var s = terrain.Gradient(hole.Pin);
            output.WriteLine($"  {hole.Name}: {Math.Sqrt(s.X * s.X + s.Z * s.Z) * 100:0.0}% at the cup");
            // Not so steep a ball placed beside the cup can't be putted, and the cup itself is on gentle ground.
            Assert.True(Math.Sqrt(s.X * s.X + s.Z * s.Z) < 0.035);
        }
    }

    [Fact]
    public void TheChippingGreenSitsAboveItsSurroundings()
    {
        var terrain = PracticeGreen.Of(PracticeKind.Chipping).Course.Terrain!;
        Assert.True(terrain.HeightAt(new P2(0, 0)) > terrain.HeightAt(new P2(-22, 0)) + 0.2);
        Assert.True(terrain.HeightAt(new P2(0, 0)) > terrain.HeightAt(new P2(0, 25)) + 0.2);
    }

    [Fact]
    public void BunkersAreDugIn()
    {
        var g = PracticeGreen.Of(PracticeKind.Chipping);
        var terrain = g.Course.Terrain!;
        foreach (var bunker in g.Holes[0].Bunkers.Cast<Ellipse>())
            Assert.True(terrain.HeightAt(bunker.Center) < terrain.HeightAt(bunker.Center + new P2(bunker.RadiusX + 2.5, 0)) - 0.25,
                "the sand floor should lie below the grass round it");
    }

    [Theory, MemberData(nameof(Kinds))]
    public void ABallPlacedOnASpotIsAFreshAttemptFromThere(PracticeKind kind)
    {
        var g = PracticeGreen.Of(kind);
        var round = new HoleRound(g.Holes, g.Course.Terrain, PracticeGreen.Rules);
        var spot = g.Spots[2];
        round.Place(spot.At);
        Assert.Equal(spot.At, round.Ball);
        Assert.Equal(spot.Lie, round.Lie);
        Assert.Equal(0, round.Strokes);
        Assert.False(round.Holed);
    }

    [Fact]
    public void PlacingTheBallAgainAfterHolingStartsOver()
    {
        var g = PracticeGreen.Of(PracticeKind.Putting);
        var round = new HoleRound(g.Holes, g.Course.Terrain, PracticeGreen.Rules);
        round.Place(round.Hole.Pin + new P2(-0.04, 0));   // a ball resting an inch from the cup falls in at the first touch
        var o = round.Play(new RangeShot(1.5 * Units.MsToMph, 0, 0, 0, 0, null, null), putt: true, HoleRound.Heading(round.Ball, round.Hole.Pin));
        Assert.True(o.Holed);
        Assert.True(round.Holed);
        round.Place(g.Spots[0].At);
        Assert.False(round.Holed);
        Assert.Equal(0, round.Strokes);
        Assert.Equal(g.Spots[0].At, round.Ball);
    }

    [Fact]
    public void NoGimmesOnAPracticeGreen()
    {
        var g = PracticeGreen.Of(PracticeKind.Putting);
        var round = new HoleRound(g.Holes, g.Course.Terrain, PracticeGreen.Rules);
        round.Place(round.Hole.Pin + new P2(-0.45, 0));
        // A putt that stops half a metre short isn't tapped in: practice holes everything out.
        var o = round.Play(new RangeShot(Math.Sqrt(2 * 0.55 * 0.3) * Units.MsToMph, 0, 0, 0, 0, null, null), putt: true, HoleRound.Heading(round.Ball, round.Hole.Pin));
        Assert.False(o.Holed);
        Assert.False(round.Holed);
    }

    [Fact]
    public void ABallInTheSandPlaysAsSand()
    {
        var g = PracticeGreen.Of(PracticeKind.Chipping);
        var round = new HoleRound(g.Holes, g.Course.Terrain, PracticeGreen.Rules);
        var bunker = g.Spots.First(s => s.Lie == Lie.Bunker);
        round.Place(bunker.At);
        Assert.Equal(Lie.Bunker, round.Lie);
        Assert.Equal(Physics.LieKind.Sand, CourseGround.ToLieKind(round.Lie));
    }

    [Fact]
    public void NextHoleGoesLeftToRightAndRoundAgain()
    {
        var g = PracticeGreen.Of(PracticeKind.Putting);
        // From the front centre the holes read left to right by their cross-green position; going right six times is a full circle.
        var ball = new P2(-14.5, 0.5);
        int hole = 0;
        var seen = new List<int>();
        for (int i = 0; i < 6; i++) { seen.Add(hole); hole = g.NextHole(ball, hole, +1); }
        Assert.Equal(0, hole);
        Assert.Equal(6, seen.Distinct().Count());
        // Each step right is to a hole that's further right (larger bearing) than the last, except the wrap round.
        double Bearing(int i) => HoleRound.Heading(ball, g.Holes[i].Pin);
        int wraps = 0;
        for (int i = 1; i < seen.Count; i++) if (Bearing(seen[i]) < Bearing(seen[i - 1])) wraps++;
        Assert.Equal(0, wraps);
        // Left is the way back.
        Assert.Equal(seen[2], g.NextHole(ball, seen[3], -1));
        Assert.Equal(seen[^1], g.NextHole(ball, seen[0], -1));
    }

    [Fact]
    public void SpotsCycleBothWaysAndRandomNeverRepeats()
    {
        var g = PracticeGreen.Of(PracticeKind.Chipping);
        Assert.Equal(1, g.NextSpot(0, 1));
        Assert.Equal(g.Spots.Count - 1, g.NextSpot(0, -1));
        Assert.Equal(0, g.NextSpot(g.Spots.Count - 1, 1));
        var random = new Random(3);
        for (int i = 0; i < 200; i++)
        {
            int from = i % g.Spots.Count, to = g.RandomSpot(from, random);
            Assert.NotEqual(from, to);
            Assert.InRange(to, 0, g.Spots.Count - 1);
        }
    }

    [Fact]
    public void HoleNearFindsTheCupUnderAClick()
    {
        var g = PracticeGreen.Of(PracticeKind.Putting);
        var pin = g.Holes[3].Pin;
        Assert.Equal(3, g.HoleNear(pin + new P2(0.8, -0.5), 2));
        Assert.Null(g.HoleNear(new P2(-14, 0), 2));
    }

    [Fact]
    public void ThePuttReadFollowsTheHoleAimedAt()
    {
        // The ball stays where it is while the target changes from a near hole to a far one: the read must be for the new hole.
        var g = PracticeGreen.Of(PracticeKind.Putting);
        var round = new HoleRound(g.Holes, g.Course.Terrain, PracticeGreen.Rules);
        var spot = g.Spots[1].At;
        round.StartHole(0);
        round.Place(spot);
        var near = round.ReadPutt();
        round.StartHole(5);
        round.Place(spot);
        var far = round.ReadPutt();
        Assert.True(round.DistanceToPin > 2 * g.Holes[0].Pin.DistanceTo(spot) * 0.9);
        Assert.True(far.Speed > near.Speed * 1.4, $"read at the far hole: {far.Speed:0.0} m/s, near: {near.Speed:0.0} m/s");
    }

    [Fact]
    public void PuttingGreenIsWhereTheBallBreaks()
    {
        // The same putt, aimed straight at each cup, finishes off line on the slopes: the green is not flat.
        var g = PracticeGreen.Of(PracticeKind.Putting);
        var broken = 0;
        foreach (var spot in g.Spots.Where((_, i) => i % 3 == 0))
            foreach (var hole in g.Holes)
            {
                var round = new HoleRound(g.Holes, g.Course.Terrain, PracticeGreen.Rules);
                round.StartHole(hole.Number - 1);
                round.Place(spot.At);
                double d = round.DistanceToPin;
                if (d is < 3 or > 12) continue;
                var read = round.ReadPutt();
                if (Math.Abs(Math.Sin(read.Heading - HoleRound.Heading(round.Ball, hole.Pin)) * d) > 0.15) broken++;
            }
        output.WriteLine($"{broken} of the putts between 3 and 12 m play more than 15 cm of break");
        Assert.True(broken >= 4, $"only {broken}");
    }
}

public class PracticeStatsTests
{
    private static PracticeAttempt Ball(double feet, int strokes, double leaveFeet = 0) =>
        new(feet * Units.FeetToMetres, strokes, leaveFeet * Units.FeetToMetres);

    [Fact]
    public void AnEmptySessionHasNothingToReport()
    {
        var stats = new PracticeStats();
        Assert.Equal(0, stats.Balls);
        Assert.Equal(0, stats.AverageStrokes);
        Assert.Null(stats.AverageLeaveMetres);
        var lines = stats.Lines(PracticeKind.Putting).ToDictionary(l => l.Caption, l => l.Value);
        Assert.Equal("0", lines["Holed out"]);
        Assert.Equal("–", lines["1-putts"]);
        Assert.Equal("–", lines["Putts per hole"]);
        Assert.Equal("–", lines["Longest 1-putt"]);
    }

    [Fact]
    public void PuttingSessionCountsOnePuttsAndThreePutts()
    {
        var stats = new PracticeStats();
        stats.Record(Ball(8, 1));
        stats.Record(Ball(20, 2, 3));
        stats.Record(Ball(30, 3, 9));
        stats.Record(Ball(15, 1));
        Assert.Equal(4, stats.Balls);
        Assert.Equal(7, stats.Strokes);
        Assert.Equal(2, stats.FirstStrokeHoles);
        Assert.Equal(1, stats.ThreeOrMore);
        Assert.Equal(3, stats.TwoOrFewer);
        Assert.Equal(1.75, stats.AverageStrokes, 3);
        Assert.Equal(15 * Units.FeetToMetres, stats.LongestFirstStrokeMetres, 3);
        // The leave is averaged over the balls the first stroke didn't hole.
        Assert.Equal(6 * Units.FeetToMetres, stats.AverageLeaveMetres!.Value, 3);
        var lines = stats.Lines(PracticeKind.Putting).ToDictionary(l => l.Caption, l => l.Value);
        Assert.Equal("4", lines["Holed out"]);
        Assert.Equal("2  ·  50%", lines["1-putts"]);
        Assert.Equal("1", lines["3-putts"]);
        Assert.Equal("1.75", lines["Putts per hole"]);
        Assert.Equal("15 ft", lines["Longest 1-putt"]);
    }

    [Fact]
    public void ChippingSessionCountsUpAndDowns()
    {
        var stats = new PracticeStats();
        stats.Record(Ball(30, 2, 4));
        stats.Record(Ball(45, 3, 11));
        stats.Record(Ball(20, 1));
        var lines = stats.Lines(PracticeKind.Chipping).ToDictionary(l => l.Caption, l => l.Value);
        Assert.Equal("2  ·  67%", lines["Up and downs"]);
        Assert.Equal("1", lines["Chip-ins"]);
        Assert.Equal("8 ft", lines["Average leave"]);
        Assert.Equal("2.0", lines["Strokes per ball"]);
    }

    [Fact]
    public void UndoTakesBackTheLastBallOnly()
    {
        var stats = new PracticeStats();
        stats.Record(Ball(8, 1));
        stats.Record(Ball(9, 2, 2));
        Assert.True(stats.UndoLast());
        Assert.Equal(1, stats.Balls);
        Assert.True(stats.UndoLast());
        Assert.False(stats.UndoLast());
        stats.Record(Ball(5, 1));
        stats.Clear();
        Assert.Equal(0, stats.Balls);
    }
}
