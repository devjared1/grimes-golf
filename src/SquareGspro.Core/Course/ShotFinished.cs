using SquareGspro.Physics;

namespace SquareGspro.Core.Course;

/// <summary>Where a player stands in the match: strokes and score to par over the holes finished, and this hole so far.</summary>
public sealed record PlayerStanding(string Name, int Strokes, int ToPar, int Thru, int HoleStrokes, bool HoleOver);

/// <summary>
/// Everything about one shot, raised once per shot as soon as its result is known (at impact: the flight is worked
/// out up front and animated afterwards). Saving data hooks into it, and so will anything else that reacts to shots
/// (commentary, stats), without the game knowing about them.
/// </summary>
/// <param name="Player">Who hit it.</param>
/// <param name="Shot">The launch data (monitor, swing meter or demo).</param>
/// <param name="Flight">Carry, total, apex, offline and the path.</param>
/// <param name="Replay">The physics tuner's replay of an earlier shot.</param>
public sealed record ShotFinished(DateTime At, Golfer Player, string Club, RangeShot Shot, FlightResult Flight, bool Replay)
{
    /// <summary>Course name (null on the range).</summary>
    public string? Course { get; init; }
    public Hole? Hole { get; init; }
    /// <summary>Which stroke this was on the hole (1 = tee shot).</summary>
    public int? Stroke { get; init; }
    /// <summary>Lie and stance it was played from.</summary>
    public Lie? Lie { get; init; }
    public LieCondition? Stance { get; init; }
    /// <summary>Distance to the pin before the shot, and what the target played (elevation included), in yards.</summary>
    public double? ToPinYards { get; init; }
    public double? PlaysLikeYards { get; init; }
    /// <summary>Where it finished: lie, penalty, holed, the message shown.</summary>
    public ShotOutcome? Outcome { get; init; }
    /// <summary>Everybody's standing after the shot (course only).</summary>
    public IReadOnlyList<PlayerStanding>? Match { get; init; }

    /// <summary>Standings for every player in <paramref name="m"/>.</summary>
    public static IReadOnlyList<PlayerStanding> Standings(Match m) =>
        m.Players.Select((g, i) =>
        {
            var (strokes, toPar, thru) = m.Standing(i);
            return new PlayerStanding(g.Name, strokes, toPar, thru, m.Rounds[i].Strokes, m.Rounds[i].Holed);
        }).ToList();
}
