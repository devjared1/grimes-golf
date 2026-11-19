using System.Text.Json.Serialization;

namespace GrimesGolf.Core.Course;

/// <summary>A tee box a hole can be played from: where the ball goes, and how far that is from the pin.</summary>
public sealed record TeeOption(string Name, P2 At, double Yards)
{
    /// <summary>"Back · 187 yd".</summary>
    public string Label => $"{Name} · {Yards:0} yd";
}

/// <summary>
/// The tee boxes of a hole. A mapped course has no named tees (white, blue, red), but it does have every tee box
/// drawn as an area: the ones lined up with a hole and close to where its line starts are that hole's tees,
/// listed from the longest to the shortest.
/// </summary>
public static class TeeBoxes
{
    /// <summary>A tee area counts for a hole if its middle is within this many metres of where the hole line starts...</summary>
    public const double ReachMetres = 90;
    /// <summary>...and the pin lies within this many degrees of the same direction from it.</summary>
    public const double AlignDegrees = 25;
    /// <summary>Tee areas this close together (m) are one tee.</summary>
    private const double SameTeeMetres = 4;

    /// <summary>
    /// The tees to choose from, longest first (at least one: the hole's own tee). Holes with no mapped tee areas
    /// (the hand-made test holes) have only that. <paramref name="course"/> is every hole of the course: a tee area
    /// goes to the hole whose own tee is nearest it, so a neighbour's tees aren't offered.
    /// </summary>
    public static IReadOnlyList<TeeOption> For(Hole hole, IReadOnlyList<Hole>? course = null)
    {
        double toPin = Bearing(hole.Tee, hole.Pin);
        var spots = new List<P2> { hole.Tee };
        foreach (var area in hole.Tees)
        {
            var at = area is Polygon poly ? poly.Centroid : area.Box.Center;
            if (at.DistanceTo(hole.Tee) > ReachMetres || at.DistanceTo(hole.Pin) < 20) continue;
            if (course != null && course.Any(h => h.Number != hole.Number && at.DistanceTo(h.Tee) < at.DistanceTo(hole.Tee))) continue;
            if (AngleBetween(Bearing(at, hole.Pin), toPin) > AlignDegrees * Math.PI / 180) continue;
            if (spots.Any(s => s.DistanceTo(at) < SameTeeMetres)) continue;
            spots.Add(at);
        }
        var ordered = spots.OrderByDescending(s => s.DistanceTo(hole.Pin)).ToList();
        return ordered.Select((at, i) => new TeeOption(NameFor(i, ordered.Count), at, at.DistanceTo(hole.Pin) * Units.MetresToYards)).ToList();
    }

    /// <summary>The tee that is where the hole's own line starts (what a round plays from): the index in <see cref="For"/>'s list.</summary>
    public static int DefaultIndex(Hole hole, IReadOnlyList<TeeOption> tees)
    {
        int best = 0;
        for (int i = 1; i < tees.Count; i++)
            if (tees[i].At.DistanceTo(hole.Tee) < tees[best].At.DistanceTo(hole.Tee)) best = i;
        return best;
    }

    /// <summary>The hole as played from <paramref name="tee"/>: the same hole with its tee (and the start of its line) moved there.</summary>
    public static Hole Play(Hole hole, TeeOption tee)
    {
        if (tee.At == hole.Tee) return hole;
        var route = hole.Route is { Count: >= 2 } r ? r.Skip(1).Prepend(tee.At).ToList() : new List<P2> { tee.At, hole.Pin };
        // A par 3 is aimed at the pin; a longer hole keeps the aim it had.
        return hole with { Tee = tee.At, TeeAim = hole.Par <= 3 ? hole.Pin : hole.TeeAim, Route = route };
    }

    /// <summary>
    /// The course with <paramref name="hole"/> (found by number) replaced by <paramref name="played"/>, so the 3D world and
    /// everything that reads the course's holes sees the moved tee.
    /// </summary>
    public static Course WithHole(Course course, Hole hole, Hole played) =>
        course with { Holes = course.Holes.Select(h => h.Number == hole.Number ? played : h).ToList() };

    private static string NameFor(int index, int count) => count switch
    {
        1 => "Tee",
        2 => index == 0 ? "Back" : "Front",
        3 => index switch { 0 => "Back", 1 => "Middle", _ => "Front" },
        _ => index == 0 ? "Back" : index == count - 1 ? "Front" : $"Tee {index + 1}",
    };

    private static double Bearing(P2 from, P2 to) => Math.Atan2(to.Z - from.Z, to.X - from.X);

    private static double AngleBetween(double a, double b) => Math.Abs(Math.IEEERemainder(a - b, 2 * Math.PI));
}

/// <summary>Who hits their shots in a row, and who takes turns.</summary>
public enum ShotOrder { TakeTurns, AllInARow }

/// <summary>
/// Everything chosen on Closest to the Pin's setup page, remembered for next time: the players (up to
/// <see cref="ClosestToPinGame.MaxPlayers"/>), the shots each, the order, the hole (by course name and hole number, with
/// the tee) and the conditions. The conditions are a <see cref="GameSetup"/> (only its weather and course settings count here).
/// </summary>
public sealed record ClosestToPinSetup(
    IReadOnlyList<Golfer>? Players = null, int ShotsEach = ClosestToPinSetup.DefaultShots, ShotOrder Order = ShotOrder.TakeTurns,
    string? Course = null, int HoleNumber = 0, int Tee = -1, GameSetup? Conditions = null)
{
    public const int DefaultShots = 3, MaxShots = 10;
    /// <summary>The shots each player can have to choose from.</summary>
    public static readonly int[] ShotChoices = { 1, 2, 3, 4, 5, 6, 8, 10 };

    /// <summary>The conditions, the default ones if none were chosen.</summary>
    [JsonIgnore] public GameSetup Game => Conditions ?? GameSetup.Default;

    /// <summary>The players: from 1 to <see cref="ClosestToPinGame.MaxPlayers"/>, blank names filled in.</summary>
    [JsonIgnore]
    public IReadOnlyList<Golfer> Golfers =>
        (Players is { Count: > 0 } p ? p.Take(ClosestToPinGame.MaxPlayers) : new[] { new Golfer("Player 1") })
        .Select((g, i) => string.IsNullOrWhiteSpace(g.Name) ? g with { Name = $"Player {i + 1}" } : g with { Name = g.Name.Trim() })
        .ToList();

    [JsonIgnore] public int Shots => Math.Clamp(ShotsEach, 1, MaxShots);

    /// <summary>"3 shots each, taking turns".</summary>
    public string Summary => $"{Shots} shot{(Shots == 1 ? "" : "s")} each, " + (Golfers.Count == 1 ? "one player" : Order == ShotOrder.TakeTurns ? "taking turns" : "all shots in a row");
}

/// <summary>One ball hit at the pin: how far it finished from it (null: no distance, it went in the water or out of bounds).</summary>
public sealed record PinShot(double? Metres, bool OnGreen, bool Holed, string Note)
{
    /// <summary>What a shot scores from how its ball finished (a ball holed is 0, one in a hazard or out of bounds has no distance).</summary>
    public static PinShot From(ShotOutcome o, Hole hole)
    {
        if (o.Holed) return new PinShot(0, true, true, "Holed!");
        if (o.Penalty > 0) return new PinShot(null, false, false, o.RestLie == Lie.Water ? "In the water" : "Out of bounds");
        return new PinShot(o.NextBall.DistanceTo(hole.Pin), o.RestLie == Lie.Green, false, HoleRound.LieName(o.RestLie));
    }

    /// <summary>"7 ft 4 in" (yards from 20 yd out, "Holed", and "Water" or "Out of bounds" for a shot with no distance).</summary>
    public string Text => Holed ? "Holed" : Metres is not { } m ? (Note == "In the water" ? "Water" : "Out of bounds") : Format(m);

    /// <summary>A distance the way a pin is measured: inches under a foot, feet and inches to 20 yd, yards beyond.</summary>
    public static string Format(double metres)
    {
        if (metres * Units.MetresToYards >= 20) return $"{metres * Units.MetresToYards:0} yd";
        int inches = (int)Math.Round(metres / Units.FeetToMetres * 12);
        int feet = inches / 12, rest = inches % 12;
        return feet == 0 ? $"{rest} in" : rest == 0 ? $"{feet} ft" : $"{feet} ft {rest} in";
    }

    /// <summary>For ranking: shots to a hundredth of a metre, nothing last.</summary>
    internal double Rank => Metres is { } m ? Math.Round(m, 2) : double.PositiveInfinity;
}

/// <summary>A player's place in the game.</summary>
/// <param name="Player">Index into the players.</param>
/// <param name="Place">1 for the leader. Players who can't be told apart (same best shot, same next best, and so on) share a place.</param>
/// <param name="Best">Their closest shot, null if none of theirs counted.</param>
public sealed record PinStanding(int Player, int Place, PinShot? Best, int ShotsHit);

/// <summary>
/// A closest-to-the-pin game: a few players, a few shots each at the same pin, and the closest shot wins. Ties go to the
/// player with the next-closest shot, and so on. <see cref="Order"/> says whether a player hits all their shots in a row
/// or the players take turns. Nothing here knows about the course: <see cref="Record"/> takes what each shot scored.
/// </summary>
public sealed class ClosestToPinGame
{
    public const int MaxPlayers = 6;

    private readonly int[] _sequence;
    private readonly List<PinShot>[] _shots;
    private int _next;

    public ClosestToPinGame(IReadOnlyList<Golfer> players, int shotsEach, ShotOrder order)
    {
        if (players.Count is < 1 or > MaxPlayers) throw new ArgumentException($"A game has 1 to {MaxPlayers} players.", nameof(players));
        if (shotsEach is < 1 or > ClosestToPinSetup.MaxShots) throw new ArgumentOutOfRangeException(nameof(shotsEach));
        Players = players;
        ShotsEach = shotsEach;
        Order = players.Count == 1 ? ShotOrder.AllInARow : order;
        _shots = players.Select(_ => new List<PinShot>()).ToArray();
        _sequence = Order == ShotOrder.AllInARow
            ? Enumerable.Range(0, players.Count).SelectMany(p => Enumerable.Repeat(p, shotsEach)).ToArray()
            : Enumerable.Range(0, shotsEach).SelectMany(_ => Enumerable.Range(0, players.Count)).ToArray();
    }

    public IReadOnlyList<Golfer> Players { get; }
    public int ShotsEach { get; }
    public ShotOrder Order { get; }
    public bool Complete => _next >= _sequence.Length;
    public int ShotsPlayed => _next;
    public int TotalShots => _sequence.Length;

    /// <summary>Who hits next (the last player once the game is complete).</summary>
    public int Turn => _sequence[Math.Min(_next, _sequence.Length - 1)];

    /// <summary>Which of their shots the player up hits next (1 for the first); <see cref="ShotsEach"/> once the game is complete.</summary>
    public int ShotNumber => Complete ? ShotsEach : _shots[Turn].Count + 1;

    public IReadOnlyList<PinShot> ShotsOf(int player) => _shots[player];

    /// <summary>Counts the shot the player up has hit and moves on to the next.</summary>
    public void Record(PinShot shot)
    {
        if (Complete) throw new InvalidOperationException("The game is finished.");
        _shots[Turn].Add(shot);
        _next++;
    }

    /// <summary>The player's closest shot so far (null: nothing that counted yet).</summary>
    public PinShot? BestOf(int player) => _shots[player].Where(s => s.Metres != null).OrderBy(s => s.Rank).FirstOrDefault();

    /// <summary>Everyone, best first. Players who have hit nothing that counted come last, in the order they play.</summary>
    public IReadOnlyList<PinStanding> Standings()
    {
        double[] Ranked(int p) => _shots[p].Select(s => s.Rank).OrderBy(r => r).ToArray();
        int Compare(int a, int b)
        {
            double[] x = Ranked(a), y = Ranked(b);
            for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                double xi = i < x.Length ? x[i] : double.PositiveInfinity, yi = i < y.Length ? y[i] : double.PositiveInfinity;
                if (xi != yi) return xi.CompareTo(yi);
            }
            return 0;
        }
        // OrderBy is stable: players who can't be told apart stay in the order they play.
        var order = Enumerable.Range(0, Players.Count).OrderBy(p => p, Comparer<int>.Create(Compare)).ToList();
        var result = new List<PinStanding>();
        for (int i = 0; i < order.Count; i++)
        {
            int place = i > 0 && Compare(order[i - 1], order[i]) == 0 ? result[i - 1].Place : i + 1;
            result.Add(new PinStanding(order[i], place, BestOf(order[i]), _shots[order[i]].Count));
        }
        return result;
    }

    /// <summary>Who has won once the game is complete: more than one player if nothing separates them (null before then).</summary>
    public IReadOnlyList<int>? Winners =>
        !Complete ? null : Standings().Where(s => s.Place == 1).Select(s => s.Player).ToList();

}
