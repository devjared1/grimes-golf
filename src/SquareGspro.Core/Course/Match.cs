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
/// <para>
/// The players are grouped into <see cref="Sides"/> by the <see cref="Type"/> of game: on their own for stroke play
/// and two-player match play; for a scramble, best ball and four-player match play, teams of two with four players
/// (1 and 2 against 3 and 4) or everyone together with two or three. A side's score on a hole is its best player's
/// (<see cref="SideScore"/>). In a scramble a team plays one ball: everyone in it hits from the same spot, then the
/// best of their shots is picked (<see cref="LastPick"/>) and they all play the next one from there.
/// </para>
/// </summary>
public sealed class Match
{
    public const int MaxPlayers = 4;

    private readonly HoleRound[] _rounds;
    /// <summary>Players in teeing order for the current hole.</summary>
    private int[] _honour;
    private readonly int[] _sideOf;
    /// <summary>Scramble: who has hit from the team's spot this stroke, and where their shot left their round.</summary>
    private readonly bool[] _hit;
    private readonly HoleRound.State?[] _after;

    public Match(IReadOnlyList<Hole> holes, Terrain? terrain, IReadOnlyList<Golfer> players, HouseRules? rules = null, GameType type = GameType.Stroke)
    {
        if (players.Count is < 1 or > MaxPlayers) throw new ArgumentException($"A match has 1 to {MaxPlayers} players.", nameof(players));
        Holes = holes;
        Players = players;
        Rules = rules ?? HouseRules.Strict;
        Type = GameSetup.Problem(type, players.Count) == null ? type : GameType.Stroke;
        Sides = SidesFor(Type, players.Count);
        _sideOf = new int[players.Count];
        for (int s = 0; s < Sides.Count; s++) foreach (int p in Sides[s]) _sideOf[p] = s;
        _rounds = players.Select(_ => new HoleRound(holes, terrain, Rules)).ToArray();
        _honour = Enumerable.Range(0, players.Count).ToArray();
        _hit = new bool[players.Count];
        _after = new HoleRound.State?[players.Count];
    }

    public IReadOnlyList<Hole> Holes { get; }
    public IReadOnlyList<Golfer> Players { get; }
    public HouseRules Rules { get; }
    /// <summary>The game being played (stroke play when the chosen one doesn't suit the number of players).</summary>
    public GameType Type { get; }
    /// <summary>Who plays together, by player index (see the class notes).</summary>
    public IReadOnlyList<IReadOnlyList<int>> Sides { get; }
    /// <summary>Some side has more than one player in it.</summary>
    public bool Teams => Sides.Any(s => s.Count > 1);
    public IReadOnlyList<HoleRound> Rounds => _rounds;
    /// <summary>Whose shot it is.</summary>
    public int Turn { get; private set; }
    public HoleRound Current => _rounds[Turn];
    public Golfer Golfer => Players[Turn];
    /// <summary>The player who hit the last shot (null once it can no longer be taken back: a new hole, a mulligan, or a scramble's pick).</summary>
    public int? LastToPlay { get; private set; }
    public int HoleIndex => _rounds[0].HoleIndex;
    public Hole Hole => Holes[HoleIndex];
    public bool HoleComplete => _rounds.All(r => r.Holed);
    public bool RoundComplete => _rounds.All(r => r.RoundComplete);
    public IReadOnlyList<int> TeeOrder => _honour;

    /// <summary>
    /// Scramble: the last pick of the team's best shot (whose shot, and the message to show), set when the last
    /// player in the team has hit; null until the next one.
    /// </summary>
    public (int Side, int Player, string Message)? LastPick { get; private set; }

    /// <summary>The sides for a game of <paramref name="type"/> (one that suits this many players) between <paramref name="players"/> players.</summary>
    public static IReadOnlyList<IReadOnlyList<int>> SidesFor(GameType type, int players)
    {
        var all = Enumerable.Range(0, players).ToArray();
        if (type == GameType.Stroke || (type == GameType.MatchPlay && players == 2))
            return all.Select(p => (IReadOnlyList<int>)new[] { p }).ToList();
        if (players == 4) return new IReadOnlyList<int>[] { new[] { 0, 1 }, new[] { 2, 3 } };
        return new IReadOnlyList<int>[] { all };
    }

    public int SideOf(int player) => _sideOf[player];

    /// <summary>"Ann & Bob", or the player's name for a side of one.</summary>
    public string SideName(int side) => string.Join(" & ", Sides[side].Select(p => Players[p].Name));

    private bool Scramble(int side) => Type == GameType.Scramble && Sides[side].Count > 1;

    /// <summary>Every player to the tee of hole <paramref name="index"/>; the player with the honour is up.</summary>
    public void StartHole(int index)
    {
        _honour = HonourFor(index);
        foreach (var r in _rounds) r.StartHole(index);
        Array.Clear(_hit);
        Array.Clear(_after);
        LastToPlay = null;
        LastPick = null;
        Turn = _honour[0];
        Turn = NextToPlay();
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

    /// <summary>
    /// Plays the current player's shot. The turn stays with them until <see cref="AdvanceTurn"/>. In a scramble, once
    /// everyone in the team has hit (or one of them holes) the best shot is picked and the whole team plays from it.
    /// </summary>
    public ShotOutcome Play(RangeShot shot, bool putt, double headingRad, string? club = null)
    {
        int before = Current.Strokes;
        LastPick = null;
        var o = Current.Play(shot, putt, headingRad, club, Golfer.LeftHanded);
        bool counted = Current.Strokes != before || o.HoleOver;
        if (counted) LastToPlay = Turn;
        int side = _sideOf[Turn];
        if (counted && Scramble(side))
        {
            _hit[Turn] = true;
            _after[Turn] = Current.Save();
            if (o.HoleOver || Sides[side].All(p => _hit[p])) Pick(side);
        }
        return o;
    }

    /// <summary>Scramble: the team's best shot of this stroke, which every player in it now plays from.</summary>
    private void Pick(int side)
    {
        var members = Sides[side];
        int best = members.Where(p => _after[p] != null).OrderBy(p => Rank(_after[p]!)).First();
        var chosen = _after[best]!;
        foreach (int p in members)
        {
            _rounds[p].Restore(chosen);
            _hit[p] = false;
            _after[p] = null;
        }
        LastToPlay = null;   // the pick can't be taken back
        string where = chosen.Holed ? (chosen.PickedUp ? "picked up" : "holed")
                     : $"{HoleRound.LieName(chosen.Lie).ToLowerInvariant()}, {chosen.Ball.DistanceTo(Hole.Pin) * Units.MetresToYards:0} yd to the pin";
        LastPick = (side, best, members.Count == 1 ? "" : $"{SideName(side)} play {Players[best].Name}'s ball ({where})");
    }

    /// <summary>How good a scramble shot left the team (lower is better): holed first, then fewest strokes, then nearest the pin from a good lie.</summary>
    private double Rank(HoleRound.State s)
    {
        if (s.Holed && !s.PickedUp) return -1e9 + s.Strokes;
        double lie = s.Lie switch
        {
            Lie.Rough => 1.15, Lie.CartPath => 1.1, Lie.Bunker => 1.3, Lie.DeepRough => 1.35, Lie.Fringe => 1.05, _ => 1.0,
        };
        return s.Strokes * 1e5 + s.Ball.DistanceTo(Hole.Pin) * lie;
    }

    /// <summary>
    /// Who plays next: players yet to tee off in honour order, then whoever is farthest from the hole. In a scramble,
    /// a team part-way through a stroke carries on (its players in honour order), then teams go by the same rules.
    /// </summary>
    public int NextToPlay()
    {
        var waiting = _honour.Where(p => !_rounds[p].Holed).ToList();
        if (waiting.Count == 0) return Turn;
        if (Type == GameType.Scramble)
        {
            var started = waiting.FirstOrDefault(p => Scramble(_sideOf[p]) && !_hit[p] && Sides[_sideOf[p]].Any(q => _hit[q]), -1);
            if (started >= 0) return started;
        }
        var toTee = waiting.Where(p => _rounds[p].Strokes == 0).ToList();
        if (toTee.Count > 0) return toTee[0];
        // Farthest away; ties go in honour order (OrderByDescending is stable).
        return waiting.OrderByDescending(p => Math.Round(_rounds[p].DistanceToPin, 2)).First();
    }

    public void AdvanceTurn() => Turn = NextToPlay();

    /// <summary>The wind on the hole being played (everyone plays in the same wind).</summary>
    public CourseWind Wind => _rounds[0].Wind;

    public void SetWind(CourseWind wind)
    {
        foreach (var r in _rounds) r.Wind = wind;
    }

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
        _hit[p] = false;
        _after[p] = null;
        Turn = p;
        LastToPlay = null;
        return true;
    }

    /// <summary>
    /// Takes a saved match back up: the hole, whose turn it is and each player's round (by index; the players must
    /// match the save's). A scramble saved part-way through a stroke goes on from the best shot hit so far.
    /// </summary>
    public void Resume(int turn, IReadOnlyList<(IReadOnlyList<int?> Scores, HoleRound.State Current, int MulligansLeft, int FirstHole)> players)
    {
        for (int i = 0; i < _rounds.Length && i < players.Count; i++)
            _rounds[i].Resume(players[i].Scores, players[i].Current, players[i].MulligansLeft, players[i].FirstHole);
        _honour = HonourFor(HoleIndex);
        LastToPlay = null;
        Settle();
        Turn = Math.Clamp(turn, 0, Players.Count - 1);
        if (_rounds[Turn].Holed) Turn = NextToPlay();
    }

    /// <summary>Scramble teams whose players aren't all at the same spot (a stroke left part-way): on from the best shot hit.</summary>
    private void Settle()
    {
        for (int side = 0; side < Sides.Count; side++)
        {
            if (!Scramble(side)) continue;
            var members = Sides[side];
            int most = members.Max(p => _rounds[p].Strokes);
            if (members.All(p => _rounds[p].Save() == _rounds[members[0]].Save())) continue;
            foreach (int p in members)
            {
                bool hit = _rounds[p].Strokes == most || _rounds[p].Holed;
                _hit[p] = hit;
                _after[p] = hit ? _rounds[p].Save() : null;
            }
            Pick(side);
        }
        LastPick = null;
    }

    /// <summary>
    /// The same round with players added or removed part-way through. <paramref name="from"/> gives, for each new
    /// player, the index of the player they were in this match (their round carries over) or null for someone joining:
    /// they start on the tee of the current hole, and the holes before don't count for them. Whoever should play next
    /// is up. The game stays the same if it suits the new number of players (stroke play otherwise).
    /// </summary>
    public Match Regroup(IReadOnlyList<Golfer> players, IReadOnlyList<int?> from)
    {
        if (from.Count != players.Count) throw new ArgumentException("One entry per player.", nameof(from));
        var m = new Match(Holes, _rounds[0].Terrain, players, Rules, Type);
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
        m.Settle();
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

    // ---- sides: team scores and match play ----

    /// <summary>
    /// A side's score on hole <paramref name="hole"/>: its best player's (a scramble team's players all share theirs).
    /// Null until every player on the side who's playing that hole has finished it.
    /// </summary>
    public int? SideScore(int side, int hole)
    {
        var members = Sides[side];
        var scores = members.Select(p => _rounds[p].Scores[hole]).ToList();
        if (scores.All(s => s == null)) return null;
        if (hole == HoleIndex && members.Any(p => !_rounds[p].Holed && _rounds[p].FirstHole <= hole)) return null;
        return scores.Where(s => s != null).Min();
    }

    /// <summary>A side's total strokes and score to par over the holes it has finished.</summary>
    public (int Strokes, int ToPar, int Thru) SideStanding(int side)
    {
        var played = Enumerable.Range(0, Holes.Count).Where(i => SideScore(side, i) != null).ToList();
        int strokes = played.Sum(i => SideScore(side, i)!.Value);
        return (strokes, strokes - played.Sum(i => Holes[i].Par), played.Count);
    }

    /// <summary>Match play: who won hole <paramref name="hole"/> (side 0 or 1), -1 if it was halved, null if it isn't finished.</summary>
    public int? HoleWinner(int hole)
    {
        if (Type != GameType.MatchPlay) return null;
        if (SideScore(0, hole) is not { } a || SideScore(1, hole) is not { } b) return null;
        return a < b ? 0 : b < a ? 1 : -1;
    }

    /// <summary>
    /// Match play: where the match stands. <see cref="MatchState.Lead"/> is side 0's holes won minus side 1's;
    /// <see cref="MatchState.Remaining"/> the holes left to play; once one side is further ahead than there are holes
    /// left it has won (<see cref="MatchState.Decided"/>), and the score is frozen there ("3 & 2").
    /// </summary>
    public MatchState MatchStatus()
    {
        int lead = 0, played = 0;
        for (int i = 0; i < Holes.Count; i++)
        {
            if (HoleWinner(i) is not { } w) continue;
            lead += w == 0 ? 1 : w == 1 ? -1 : 0;
            played++;
            int remaining = Holes.Count - played;
            if (Math.Abs(lead) > remaining) return new MatchState(lead, played, remaining, true);
        }
        return new MatchState(lead, played, Holes.Count - played, false);
    }

    /// <summary>"Ann 2 UP", "All square", "Ann & Bob win 3 & 2" (match play only; empty otherwise).</summary>
    public string MatchText()
    {
        if (Type != GameType.MatchPlay) return "";
        var m = MatchStatus();
        if (m.Lead == 0) return m.Played == 0 ? "All square" : m.Remaining == 0 ? "Match halved" : "All square";
        int leader = m.Lead > 0 ? 0 : 1, by = Math.Abs(m.Lead);
        string name = SideName(leader), win = Sides[leader].Count > 1 ? "win" : "wins";
        if (m.Decided) return m.Remaining == 0 ? $"{name} {win} {by} up" : $"{name} {win} {by} & {m.Remaining}";
        return $"{name} {by} UP{(by == m.Remaining ? " (dormie)" : "")}";
    }

    /// <summary>Match play from one side's point of view: "2 UP", "1 DN", "AS".</summary>
    public string MatchTextFor(int side)
    {
        var m = MatchStatus();
        int lead = side == 0 ? m.Lead : -m.Lead;
        return lead == 0 ? "AS" : lead > 0 ? $"{lead} UP" : $"{-lead} DN";
    }

    /// <summary>The result of the round as one line: each player or side's total (to par), or the match.</summary>
    public string Summary()
    {
        if (Type == GameType.MatchPlay) return MatchText();
        if (Teams)
            return string.Join("   ", Enumerable.Range(0, Sides.Count).Select(s =>
            {
                var (total, toPar, _) = SideStanding(s);
                return $"{SideName(s)} {total} ({ToPar(toPar)})";
            }));
        return string.Join("   ", Players.Select((g, i) =>
        {
            var (total, toPar, _) = Standing(i);
            return $"{(Players.Count > 1 ? g.Name + " " : "")}{total} ({ToPar(toPar)})";
        }));
    }

    public static string ToPar(int d) => d == 0 ? "E" : d > 0 ? $"+{d}" : d.ToString();
}

/// <param name="Lead">Side 0's holes won minus side 1's.</param>
public readonly record struct MatchState(int Lead, int Played, int Remaining, bool Decided);
