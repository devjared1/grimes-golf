namespace GrimesGolf.Core.Course;

/// <summary>The practice greens on the Practice menu.</summary>
public enum PracticeKind { Putting, Chipping }

/// <summary>A place to put the ball down on a practice green, and the lie it has there.</summary>
public sealed record PracticeSpot(string Name, P2 At, Lie Lie);

/// <summary>
/// A made-up green to practice on, built as an ordinary <see cref="Course"/> so the rest of the game plays it the way
/// it plays any course: one <see cref="Hole"/> per cup on the green (they share the green, bunkers and ground, so the whole
/// green is drawn once, every cup with its flag, and the one being aimed at is the "hole"), over ground with real contours
/// that the ball's roll follows.
/// <para>
/// Nothing here is a tee: the holes' tees sit well away from the green, and a ball is always put down with
/// <see cref="HoleRound.Place"/>. <see cref="Spots"/> are ready-made places to drop the ball; the player can also put
/// it anywhere <see cref="CanPlace"/> allows.
/// </para>
/// </summary>
public sealed class PracticeGreen
{
    private PracticeGreen(PracticeKind kind, string name, Course course, IReadOnlyList<PracticeSpot> spots, string cupNoun)
    {
        Kind = kind;
        Name = name;
        Course = course;
        Spots = spots;
        CupNoun = cupNoun;
    }

    public PracticeKind Kind { get; }
    /// <summary>"Putting Green": the name shots are saved under and the title on the screen.</summary>
    public string Name { get; }
    public Course Course { get; }
    public IReadOnlyList<Hole> Holes => Course.Holes;
    public IReadOnlyList<PracticeSpot> Spots { get; }
    /// <summary>What to call one of the balls' attempts on this green: "putt" or "chip".</summary>
    public string CupNoun { get; }

    /// <summary>The rules for practice: every ball is holed out (no gimmes), nothing is picked up and no strokes are limited.</summary>
    public static readonly HouseRules Rules = new(Mulligans: 0, GimmeUnits: 0);

    /// <summary>The flagstick's scale: the putting green has short ones.</summary>
    public const float MiniFlag = 0.42f, FullFlag = 1.2f;

    public float FlagScale => Kind == PracticeKind.Putting ? MiniFlag : FullFlag;

    public static PracticeGreen Of(PracticeKind kind) => kind == PracticeKind.Putting ? Putting() : Chipping();

    // ---- where the ball can go -----------------------------------------------------------------

    /// <summary>
    /// Whether the ball may be put down at <paramref name="at"/>: on the putting green, on the green or its fringe; on the
    /// chipping green, anywhere there is ground to play from (not water, out of bounds or a cart path).
    /// </summary>
    public bool CanPlace(P2 at)
    {
        var lie = Holes[0].LieAt(at);
        return Kind == PracticeKind.Putting ? lie is Lie.Green or Lie.Fringe : lie is not (Lie.Water or Lie.OutOfBounds or Lie.CartPath);
    }

    /// <summary>The place to drop the ball after <paramref name="current"/>, <paramref name="step"/> (+1 or −1) along the list.</summary>
    public int NextSpot(int current, int step) => ((current + step) % Spots.Count + Spots.Count) % Spots.Count;

    /// <summary>A spot other than <paramref name="current"/>, chosen at random.</summary>
    public int RandomSpot(int current, Random random)
    {
        if (Spots.Count < 2) return 0;
        int pick = random.Next(Spots.Count - 1);
        return pick >= current ? pick + 1 : pick;
    }

    /// <summary>
    /// The hole to the right (+1) or left (−1) of hole <paramref name="current"/> as seen from a ball at
    /// <paramref name="ball"/>, going round to the far side after the last one. Holes in the same line from the ball come
    /// nearest first.
    /// </summary>
    public int NextHole(P2 ball, int current, int step)
    {
        var holes = Holes;
        if (holes.Count < 2) return current;
        var center = new P2(holes.Average(h => h.Pin.X), holes.Average(h => h.Pin.Z));
        double reference = HoleRound.Heading(ball, center);
        var order = Enumerable.Range(0, holes.Count)
            .OrderBy(i => Math.Round(Math.IEEERemainder(HoleRound.Heading(ball, holes[i].Pin) - reference, 2 * Math.PI), 3))
            .ThenBy(i => holes[i].Pin.DistanceTo(ball))
            .ToList();
        int at = order.IndexOf(current);
        return order[((at + step) % order.Count + order.Count) % order.Count];
    }

    /// <summary>The hole whose cup is within <paramref name="within"/> world units of <paramref name="at"/> (nearest first), or null.</summary>
    public int? HoleNear(P2 at, double within)
    {
        int best = -1;
        for (int i = 0; i < Holes.Count; i++)
            if (Holes[i].Pin.DistanceTo(at) <= within && (best < 0 || Holes[i].Pin.DistanceTo(at) < Holes[best].Pin.DistanceTo(at))) best = i;
        return best < 0 ? null : best;
    }

    // ---- the greens ----------------------------------------------------------------------------

    /// <summary>The ground a practice green sits on is a mowed square this size around the origin, then flat out to the horizon.</summary>
    private const double Cell = 0.5;

    /// <summary>A rounded, lumpy outline round <paramref name="center"/>: an ellipse (radii <paramref name="rx"/> along X, <paramref name="rz"/> along Z) with lobes in it.</summary>
    private static Polygon Blob(P2 center, double rx, double rz, (double Amplitude, int Lobes, double Phase)[] lobes, int segments = 72)
    {
        var points = new P2[segments];
        for (int i = 0; i < segments; i++)
        {
            double a = 2 * Math.PI * i / segments, k = 1;
            foreach (var (amplitude, count, phase) in lobes) k += amplitude * Math.Sin(count * a + phase);
            points[i] = new P2(center.X + Math.Cos(a) * rx * k, center.Z + Math.Sin(a) * rz * k);
        }
        return new Polygon(points);
    }

    /// <summary>A smooth mound (positive <paramref name="height"/>) or hollow (negative) at <paramref name="at"/>, <paramref name="sx"/> × <paramref name="sz"/> world units across (one standard deviation).</summary>
    private static double Bump(P2 p, P2 at, double sx, double sz, double height)
    {
        double dx = (p.X - at.X) / sx, dz = (p.Z - at.Z) / sz;
        return height * Math.Exp(-(dx * dx + dz * dz) / 2);
    }

    private static Hole Cup(int number, P2 pin, int par, Bounds bounds, P2 tee, IReadOnlyList<Shape> fairways, IReadOnlyList<Shape> greens, IReadOnlyList<Shape> bunkers) =>
        new(number, $"Hole {number}", par, tee, pin, pin, fairways, greens, bunkers, Array.Empty<Shape>(), bounds);

    /// <summary>
    /// A big putting green, 115 ft by 75 ft, with six cups spread over it and slopes that break
    /// putts (a mound and a hollow in the middle, a gentle fall to the front right), none steeper than 4%.
    /// </summary>
    public static PracticeGreen Putting()
    {
        var green = Blob(new P2(0, 0), 17.5, 11.5, new[] { (0.09, 2, 0.6), (0.06, 3, 1.9), (0.035, 5, 0.3) });
        var area = new Bounds(new P2(-50, -45), new P2(50, 45));
        double Height(P2 p) =>
            -0.006 * p.X + 0.004 * p.Z
            + Bump(p, new P2(-4, 2), 6.5, 5, 0.20) + Bump(p, new P2(7, -4), 6.5, 5, -0.13)
            + Bump(p, new P2(-10, 6.5), 4.5, 3.5, 0.11) + Bump(p, new P2(11, 6), 5, 4, 0.09)
            + Bump(p, new P2(10, -8), 4, 3.5, -0.06) + Bump(p, new P2(-9, -6), 4, 3.5, -0.07);
        var terrain = new Terrain(area, Cell, Height);
        var greens = new Shape[] { green };

        P2[] pins = { new(-9, -4.5), new(-8, 5.5), new(-0.5, -6.5), new(1, 5), new(8.5, -3.5), new(9, 4.5) };
        var tee = new P2(-300, 0);
        var holes = pins.Select((pin, i) => Cup(i + 1, pin, 2, area, tee, Array.Empty<Shape>(), greens, Array.Empty<Shape>())).ToList();
        var course = new Course("Putting Green", holes) { SharedLayout = true, Extent = area, Terrain = terrain, Practice = PracticeKind.Putting };

        (string, P2)[] spots =
        {
            ("Front left", new P2(-13, -5)), ("Front center", new P2(-14.5, 0.5)), ("Front right", new P2(-13, 5.5)),
            ("Left side", new P2(0.5, -9)), ("Center", new P2(0, 0)), ("Right side", new P2(1, 9)),
            ("Back left", new P2(13, -5.5)), ("Back center", new P2(14.5, 0)), ("Back right", new P2(13, 5.5)),
            ("Low edge", new P2(-5, -9.5)), ("High edge", new P2(6, 9.5)),
        };
        return new PracticeGreen(PracticeKind.Putting, course.Name, course, spots.Select(s => new PracticeSpot(s.Item1, s.Item2, Lie.Green)).ToList(), "putt");
    }

    /// <summary>
    /// A chipping green on a low plateau that falls away to rough on every side: a kidney-shaped green about 27 yd by 20 yd with four
    /// cups, a short fairway running up to it, and a bunker on the front left and one at the back right. The green slopes back
    /// to front, so shots from the front play uphill and from behind, downhill.
    /// </summary>
    public static PracticeGreen Chipping()
    {
        var green = Blob(new P2(0, 0), 12.5, 9, new[] { (0.10, 2, 2.2), (0.06, 3, 0.7), (0.03, 5, 1.1) });
        var bunkers = new Shape[]
        {
            new Ellipse(new P2(-6.5, -14.5), 5, 3.4, 20),
            new Ellipse(new P2(8, 13.5), 4.4, 3.2, -25),
        };
        var fairway = new Shape[] { new Strip(new P2[] { new(-62, -1), new(-26, 0), new(-12, 0.5) }, 15) };
        var area = new Bounds(new P2(-70, -45), new P2(45, 45));
        double Height(P2 p)
        {
            // The plateau (flat on top, falling away over a few world units), then the green's own slope and lumps, and mounds and hollows around it.
            double ux = p.X / 23, uz = p.Z / 18;
            double plateau = 0.40 * Math.Exp(-(Math.Pow(ux, 4) + Math.Pow(uz, 4)));
            return plateau + 0.013 * p.X * Math.Exp(-Math.Pow(p.X / 35, 2))
                + Bump(p, new P2(-3, -3), 4.5, 3.5, 0.16) + Bump(p, new P2(4, 4), 4, 3.5, -0.14)
                + Bump(p, new P2(-9, -18), 4, 3, 0.45) + Bump(p, new P2(6, 20), 4, 4, 0.55)
                + Bump(p, new P2(-17, 13), 4, 4, -0.30) + Bump(p, new P2(16, -13), 4.5, 3.5, 0.40)
                + Bump(p, new P2(15, 5), 4.5, 4, -0.15);
        }
        var terrain = new Terrain(area, Cell, Height);
        var greens = new Shape[] { green };
        terrain.Sculpt(greens, bunkers, Array.Empty<Shape>(), shapeGreens: false);

        P2[] pins = { new(-5.5, -3.5), new(-4.5, 4.5), new(4.5, -4), new(5, 3.5) };
        var tee = new P2(-300, 0);
        var holes = pins.Select((pin, i) => Cup(i + 1, pin, 3, area, tee, fairway, greens, bunkers)).ToList();
        var course = new Course("Chipping Green", holes) { SharedLayout = true, Extent = area, Terrain = terrain, Practice = PracticeKind.Chipping };

        (string, P2, Lie)[] spots =
        {
            ("Front fringe", new P2(-13.8, 1), Lie.Fringe),
            ("Short fairway", new P2(-22, 0), Lie.Fairway),
            ("Fairway, 30 yd", new P2(-40, 0.5), Lie.Fairway),
            ("Front-right rough", new P2(-12, 14), Lie.Rough),
            ("Left bunker", new P2(-6.5, -14.5), Lie.Bunker),
            ("Right bunker", new P2(8, 13.5), Lie.Bunker),
            ("Back rough", new P2(18.5, -1), Lie.Rough),
            ("Left rough", new P2(2, -16.5), Lie.Rough),
            ("Deep rough", new P2(-2, 26), Lie.DeepRough),
            ("Back fringe", new P2(13.8, 3), Lie.Fringe),
        };
        return new PracticeGreen(PracticeKind.Chipping, course.Name, course, spots.Select(s => new PracticeSpot(s.Item1, s.Item2, s.Item3)).ToList(), "chip");
    }
}

/// <summary>One ball played to the hole: where it started, the strokes it took, and how far from the hole the first stroke left it.</summary>
/// <param name="LeaveUnits">Distance to the hole after the first stroke (0 when it went straight in).</param>
public sealed record PracticeAttempt(double StartUnits, int Strokes, double LeaveUnits);

/// <summary>A practice session's results: every ball holed out, and what they say.</summary>
public sealed class PracticeStats
{
    private readonly List<PracticeAttempt> _attempts = new();

    public int Balls => _attempts.Count;
    public int Strokes => _attempts.Sum(a => a.Strokes);
    /// <summary>Balls that went in with the first stroke (a chip-in, or a one-putt).</summary>
    public int FirstStrokeHoles => _attempts.Count(a => a.Strokes == 1);
    /// <summary>Three strokes or more to hole out.</summary>
    public int ThreeOrMore => _attempts.Count(a => a.Strokes >= 3);
    /// <summary>Holed in two strokes or fewer: an up and down, from off the green, a one- or two-putt from on it.</summary>
    public int TwoOrFewer => _attempts.Count(a => a.Strokes <= 2);
    public double AverageStrokes => Balls == 0 ? 0 : (double)Strokes / Balls;
    /// <summary>The farthest hole made with the first stroke (wu; 0 if there hasn't been one).</summary>
    public double LongestFirstStrokeUnits => _attempts.Where(a => a.Strokes == 1).Select(a => a.StartUnits).DefaultIfEmpty(0).Max();
    /// <summary>How far the first stroke left the ball from the hole, on average, over the balls it didn't hole (null if it holed them all).</summary>
    public double? AverageLeaveUnits
    {
        get
        {
            var leaves = _attempts.Where(a => a.Strokes > 1).Select(a => a.LeaveUnits).ToList();
            return leaves.Count == 0 ? null : leaves.Average();
        }
    }

    public void Record(PracticeAttempt attempt) => _attempts.Add(attempt);

    /// <summary>Takes back the last ball (it was holed again from where it was, after a retry).</summary>
    public bool UndoLast()
    {
        if (_attempts.Count == 0) return false;
        _attempts.RemoveAt(_attempts.Count - 1);
        return true;
    }

    public void Clear() => _attempts.Clear();

    private static string Feet(double units) => $"{units / Units.FeetToWorld:0} ft";
    private static string Percent(int part, int whole) => whole == 0 ? "–" : $"{100.0 * part / whole:0}%";

    /// <summary>The session as captions and values for a small panel, for this kind of green.</summary>
    public IReadOnlyList<(string Caption, string Value)> Lines(PracticeKind kind)
    {
        var lines = new List<(string, string)> { ("Holed out", Balls.ToString()) };
        if (kind == PracticeKind.Putting)
        {
            lines.Add(("1-putts", Balls == 0 ? "–" : $"{FirstStrokeHoles}  ·  {Percent(FirstStrokeHoles, Balls)}"));
            lines.Add(("3-putts", ThreeOrMore.ToString()));
            lines.Add(("Putts per hole", Balls == 0 ? "–" : $"{AverageStrokes:0.0#}"));
            lines.Add(("Longest 1-putt", FirstStrokeHoles == 0 ? "–" : Feet(LongestFirstStrokeUnits)));
        }
        else
        {
            lines.Add(("Up and downs", Balls == 0 ? "–" : $"{TwoOrFewer}  ·  {Percent(TwoOrFewer, Balls)}"));
            lines.Add(("Chip-ins", FirstStrokeHoles.ToString()));
            lines.Add(("Average leave", AverageLeaveUnits is { } leave ? Feet(leave) : "–"));
            lines.Add(("Strokes per ball", Balls == 0 ? "–" : $"{AverageStrokes:0.0#}"));
        }
        return lines;
    }
}
