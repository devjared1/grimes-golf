using System.Text.Json;
using System.Text.Json.Serialization;

namespace GrimesGolf.Core.Course;

/// <summary>
/// Everything about a <see cref="Match"/> part-way through (every player's round, whose turn it is, mulligans, a
/// scramble's stroke in progress, who is away, the wind), as plain data that survives a JSON round trip exactly.
/// <para>
/// Online play sends one with every shot: the host's PC is the only one that simulates, and every other PC puts
/// its match into the host's state with <see cref="Match.Restore"/>. The same snapshot covers rejoining, a resync,
/// mulligans and the host carrying on a paused round. (The course, players, rules and game type aren't in it: they
/// are fixed for the round and the match is built from them first.)
/// </para>
/// </summary>
public sealed record MatchSnapshot(
    int Turn, int? LastToPlay, int[] Honour, MatchSnapshot.Player[] Players, MatchSnapshot.WindState Wind, MatchSnapshot.Pick? LastPick)
{
    /// <summary>Where one player's round stands on the current hole (see <see cref="HoleRound.State"/>).</summary>
    public sealed record Position(int HoleIndex, double BallX, double BallZ, Lie Lie, int Strokes, bool Holed, int? Score, bool PickedUp)
    {
        public static Position Of(HoleRound.State s) => new(s.HoleIndex, s.Ball.X, s.Ball.Z, s.Lie, s.Strokes, s.Holed, s.Score, s.PickedUp);
        public HoleRound.State ToState() => new(HoleIndex, new P2(BallX, BallZ), Lie, Strokes, Holed, Score, PickedUp);
    }

    /// <param name="Hit">Scramble: has hit from the team's spot this stroke (<see cref="After"/> is where it left them).</param>
    /// <param name="Away">Dropped out online: skipped, and their holes picked up, until they're back.</param>
    public sealed record Player(int?[] Scores, Position Current, int MulligansLeft, int FirstHole, Position? BeforeLast, bool Hit, Position? After, bool Away);

    public sealed record WindState(double SpeedMph, double FromHeading, double GustMph)
    {
        public static WindState Of(CourseWind w) => new(w.SpeedMph, w.FromHeading, w.GustMph);
        public CourseWind ToWind() => new(SpeedMph, FromHeading, GustMph);
    }

    /// <summary>A scramble's last pick (<see cref="Match.LastPick"/>; a tuple, which JSON would leave out).</summary>
    public sealed record Pick(int Side, int Player, string Message);

    public int HoleIndex => Players.Length == 0 ? 0 : Players[0].Current.HoleIndex;

    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static MatchSnapshot FromJson(string json) =>
        JsonSerializer.Deserialize<MatchSnapshot>(json, Options) ?? throw new JsonException("Empty match snapshot.");

    /// <summary>Same state as <paramref name="other"/> (records holding arrays don't compare by value).</summary>
    public bool SameAs(MatchSnapshot? other) => other != null && ToJson() == other.ToJson();
}
