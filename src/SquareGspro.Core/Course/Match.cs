namespace SquareGspro.Core.Course;

/// <summary>A player in a match.</summary>
public sealed record Golfer(string Name, bool LeftHanded = false);

/// <summary>
/// A hot-seat match: 1-4 players on the same course, each with their own ball and <see cref="HoleRound"/> (strokes,
/// scores, mulligans), all on the same hole. <see cref="Turn"/> is whose shot it is:
/// <list type="bullet">
/// <item>On the tee, players tee off in honour order: lowest score on the last hole first (ties keep the order before).</item>
/// <item>After that, whoever is farthest from the hole plays next (a player who has to replay from the tee after a
/// penalty counts as far away).</item>
/// </list>
/// The hole is over once every player has holed out or picked up.
/// </summary>
public sealed class Match
{
    public const int MaxPlayers = 4;

    private readonly HoleRound[] _rounds;
    /// <summary>Players in teeing order for the current hole.</summary>
    private int[] _honour;

    public Match(IReadOnlyList<Hole> holes, Terrain? terrain, IReadOnlyList<Golfer> players, HouseRules? rules = null)
    {
        if (players.Count is < 1 or > MaxPlayers) throw new ArgumentException($"A match has 1 to {MaxPlayers} players.", nameof(players));
        Holes = holes;
        Players = players;
        Rules = rules ?? HouseRules.Strict;
        _rounds = players.Select(_ => new HoleRound(holes, terrain, Rules)).ToArray();
        _honour = Enumerable.Range(0, players.Count).ToArray();
    }

    public IReadOnlyList<Hole> Holes { get; }
    public IReadOnlyList<Golfer> Players { get; }
    public HouseRules Rules { get; }
    public IReadOnlyList<HoleRound> Rounds => _rounds;
    /// <summary>Whose shot it is.</summary>
    public int Turn { get; private set; }
    public HoleRound Current => _rounds[Turn];
    public Golfer Golfer => Players[Turn];
    /// <summary>The player who hit the last shot (null once it can no longer be taken back: a new hole, or a mulligan).</summary>
    public int? LastToPlay { get; private set; }
    public int HoleIndex => _rounds[0].HoleIndex;
    public Hole Hole => Holes[HoleIndex];
    public bool HoleComplete => _rounds.All(r => r.Holed);
    public bool RoundComplete => _rounds.All(r => r.RoundComplete);
    public IReadOnlyList<int> TeeOrder => _honour;

    /// <summary>Every player to the tee of hole <paramref name="index"/>; the player with the honour is up.</summary>
    public void StartHole(int index)
    {
        _honour = HonourFor(index);
        foreach (var r in _rounds) r.StartHole(index);
        LastToPlay = null;
        Turn = _honour[0];
    }

    /// <summary>
    /// Teeing order for hole <paramref name="index"/>: by the score on the hole before (lowest first), ties keeping
    /// the order of the hole before that; player order on the first hole or when the hole before isn't scored.
    /// </summary>
    private int[] HonourFor(int index)
    {
        var order = Enumerable.Range(0, Players.Count).ToArray();
        for (int h = 0; h < index; h++)
        {
            int hole = h;
            if (_rounds.Any(r => r.Scores[hole] == null)) continue;
            order = order.OrderBy(p => _rounds[p].Scores[hole]!.Value).ToArray();   // stable: ties keep the order before
        }
        return order;
    }

    /// <summary>Plays the current player's shot. The turn stays with them until <see cref="AdvanceTurn"/>.</summary>
    public ShotOutcome Play(RangeShot shot, bool putt, double headingRad, string? club = null)
    {
        int before = Current.Strokes;
        var o = Current.Play(shot, putt, headingRad, club, Golfer.LeftHanded);
        if (Current.Strokes != before || o.HoleOver) LastToPlay = Turn;
        return o;
    }

    /// <summary>Who plays next: players yet to tee off in honour order, then whoever is farthest from the hole.</summary>
    public int NextToPlay()
    {
        var waiting = _honour.Where(p => !_rounds[p].Holed).ToList();
        if (waiting.Count == 0) return Turn;
        var toTee = waiting.Where(p => _rounds[p].Strokes == 0).ToList();
        if (toTee.Count > 0) return toTee[0];
        // Farthest away; ties go in honour order (OrderByDescending is stable).
        return waiting.OrderByDescending(p => Math.Round(_rounds[p].DistanceToPin, 2)).First();
    }

    public void AdvanceTurn() => Turn = NextToPlay();

    /// <summary>Hands the turn to <paramref name="player"/> (e.g. to replay their last shot).</summary>
    public void TakeTurn(int player) => Turn = Math.Clamp(player, 0, Players.Count - 1);

    /// <summary>Whether the last shot of the match can be taken back.</summary>
    public bool CanMulligan => LastToPlay is { } p && _rounds[p].CanMulligan;

    /// <summary>Takes back the last shot of the match; that player plays again. False if it can't be.</summary>
    public bool Mulligan()
    {
        if (!CanMulligan) return false;
        int p = LastToPlay!.Value;
        _rounds[p].Mulligan();
        Turn = p;
        LastToPlay = null;
        return true;
    }

    /// <summary>
    /// Takes a saved match back up: the hole, whose turn it is and each player's round (by index; the players must
    /// match the save's).
    /// </summary>
    public void Resume(int turn, IReadOnlyList<(IReadOnlyList<int?> Scores, HoleRound.State Current, int MulligansLeft, int FirstHole)> players)
    {
        for (int i = 0; i < _rounds.Length && i < players.Count; i++)
            _rounds[i].Resume(players[i].Scores, players[i].Current, players[i].MulligansLeft, players[i].FirstHole);
        _honour = HonourFor(HoleIndex);
        LastToPlay = null;
        Turn = Math.Clamp(turn, 0, Players.Count - 1);
    }

    /// <summary>
    /// The same round with players added or removed part-way through. <paramref name="from"/> gives, for each new
    /// player, the index of the player they were in this match (their round carries over) or null for someone joining:
    /// they start on the tee of the current hole, and the holes before don't count for them. Whoever should play next is up.
    /// </summary>
    public Match Regroup(IReadOnlyList<Golfer> players, IReadOnlyList<int?> from)
    {
        if (from.Count != players.Count) throw new ArgumentException("One entry per player.", nameof(from));
        var m = new Match(Holes, _rounds[0].Terrain, players, Rules);
        for (int i = 0; i < players.Count; i++)
        {
            if (from[i] is { } old)
            {
                var r = _rounds[old];
                m._rounds[i].Resume(r.Scores, r.Save(), r.MulligansLeft, r.FirstHole);
            }
            else m._rounds[i].JoinAt(HoleIndex);
        }
        m._honour = m.HonourFor(HoleIndex);
        m.Turn = m._honour[0];
        m.Turn = m.NextToPlay();
        return m;
    }

    /// <summary>Total strokes and score to par over the holes played, per player.</summary>
    public (int Strokes, int ToPar, int Thru) Standing(int player)
    {
        var r = _rounds[player];
        var played = Enumerable.Range(0, Holes.Count).Where(i => r.Scores[i] != null).ToList();
        int strokes = played.Sum(i => r.Scores[i]!.Value);
        return (strokes, strokes - played.Sum(i => Holes[i].Par), played.Count);
    }
}
