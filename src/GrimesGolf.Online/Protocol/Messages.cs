using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using GrimesGolf.Core;
using GrimesGolf.Core.Course;

namespace GrimesGolf.Online.Protocol;

/// <summary>
/// The messages between PCs in an online round (inside the relay's envelope, which never looks at them).
/// Friends' PCs send <see cref="Hello"/>, <see cref="Status"/>, <see cref="ShotRequest"/>, <see cref="ShotShown"/>,
/// <see cref="Aim"/>, <see cref="MulliganRequest"/> and <see cref="Bye"/>; the host's PC answers with the rest.
/// The host's PC is the only one that plays shots: everyone else is sent the outcome and the whole match after it
/// (<see cref="MatchSnapshot"/>), and animates that.
/// <para>
/// <c>Seq</c> counts the turns the host has opened. A shot or mulligan is only taken for the turn that's open, so
/// one that crosses another on the network is turned down rather than played twice or out of order.
/// </para>
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Hello), "hello")]
[JsonDerivedType(typeof(Status), "status")]
[JsonDerivedType(typeof(ShotRequest), "shot")]
[JsonDerivedType(typeof(ShotShown), "shown")]
[JsonDerivedType(typeof(Aim), "aim")]
[JsonDerivedType(typeof(MulliganRequest), "mulligan")]
[JsonDerivedType(typeof(Bye), "bye")]
[JsonDerivedType(typeof(LobbyUpdate), "lobby")]
[JsonDerivedType(typeof(Refused), "refused")]
[JsonDerivedType(typeof(RoundStart), "start")]
[JsonDerivedType(typeof(ShotResult), "result")]
[JsonDerivedType(typeof(TurnOpen), "turn")]
[JsonDerivedType(typeof(Sync), "sync")]
[JsonDerivedType(typeof(AwayNotice), "away")]
[JsonDerivedType(typeof(Notice), "notice")]
[JsonDerivedType(typeof(RoundOver), "over")]
public abstract record OnlineMessage
{
    public static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Encode() => JsonSerializer.Serialize(this, Json);

    /// <summary>The message in <paramref name="json"/>, or null if it isn't one (from a newer app, or damaged).</summary>
    public static OnlineMessage? Decode(string json)
    {
        try { return JsonSerializer.Deserialize<OnlineMessage>(json, Json); }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException or InvalidOperationException) { return null; }
    }
}

public static class OnlineProtocol
{
    /// <summary>Bumped whenever a message changes. Every PC in a lobby must match, and run the same app version.</summary>
    public const int Version = 1;
    public const int MaxNameLength = 16;
}

// ---- lobby -------------------------------------------------------------------------------------

/// <summary>A player sitting at one of the PCs. Seat order is teeing order on the first hole, and sets the teams.</summary>
public sealed record Seat(string Pc, string Name, bool LeftHanded);

/// <summary>A PC in the lobby. <paramref name="Pc"/> is a public id derived from its secret relay token.</summary>
public sealed record PcInfo(string Pc, string Label, bool Host, bool Connected, bool MonitorLive);

public sealed record LobbyState(
    string Code, PcInfo[] Pcs, Seat[] Seats, string? Course, GameSetup Setup, HouseRules Rules, bool CanStart, string? WhyNot, bool InRound);

/// <summary>Everything fixed for an online round, sent to every PC when it starts.</summary>
/// <param name="Fingerprint">The course as the host built it (<see cref="CourseFingerprint"/>): every PC must build the same.</param>
/// <param name="TuningJson">The host's physics tuning, used on every PC for aim lines and putt reads.</param>
/// <param name="HoleNumbers">The holes played, in order.</param>
public sealed record RoundConfig(
    string Course, string Fingerprint, GameSetup Setup, HouseRules Rules, string TuningJson, Seat[] Seats, DateTime Started, int[] HoleNumbers)
{
    public IReadOnlyList<Golfer> Golfers => Seats.Select(s => new Golfer(s.Name, s.LeftHanded)).ToList();
}

// ---- friend -> host ----------------------------------------------------------------------------

/// <summary>A friend's PC introducing itself (and again after every reconnection).</summary>
public sealed record Hello(int Protocol, string AppVersion, string Pc, Seat[] Players, bool MonitorLive) : OnlineMessage;

/// <summary>A friend's PC: its monitor is (or isn't) live, and the course it built for the round.</summary>
public sealed record Status(bool MonitorLive, string? Fingerprint) : OnlineMessage;

/// <summary>
/// A shot from the monitor of the PC whose turn it is. <paramref name="Id"/> is the same if it's sent again after
/// a network blip, so a real ball is neither lost nor played twice.
/// </summary>
public sealed record ShotRequest(Guid Id, int Seq, int Player, RangeShot Shot, string Club, double Heading, bool Putt) : OnlineMessage;

/// <summary>This PC has finished showing the shot (the ball has stopped and the result is up).</summary>
public sealed record ShotShown(int Seq) : OnlineMessage;

/// <summary>Where the player whose turn it is is aiming, with what club and power (both ways; at most 10 a second).</summary>
public sealed record Aim(int Seq, int Player, double Heading, string Club, double Power) : OnlineMessage;

/// <summary>Take back the last shot (only from the PC whose player hit it, before the next shot is taken).</summary>
public sealed record MulliganRequest(int Seq) : OnlineMessage;

/// <summary>Leaving the round for good.</summary>
public sealed record Bye : OnlineMessage;

// ---- host -> friends ---------------------------------------------------------------------------

public sealed record LobbyUpdate(LobbyState State) : OnlineMessage;

/// <summary>The host won't have this PC (different version, lobby full, round under way, different course data).</summary>
public sealed record Refused(string Reason) : OnlineMessage;

/// <summary>The round starts: build the course and match from <paramref name="Config"/>, then put it as <paramref name="Snapshot"/>.</summary>
public sealed record RoundStart(RoundConfig Config, MatchSnapshot Snapshot, int Seq) : OnlineMessage;

/// <summary>A shot the host played: its outcome, and the match straight after it (the turn still with the player who hit).</summary>
public sealed record ShotResult(int Seq, Guid Id, int Player, RangeShot Shot, string Club, double Heading, bool Putt, OutcomeData Outcome, MatchSnapshot Snapshot) : OnlineMessage;

public enum TurnReason { Start, Shot, NextHole, Mulligan, Skip, Resync }

/// <summary>A new turn: the match as it now stands (whose turn it is included). Shots for any other turn are turned down.</summary>
public sealed record TurnOpen(int Seq, MatchSnapshot Snapshot, TurnReason Reason) : OnlineMessage;

/// <summary>
/// Everything about the round, for a PC (re)joining part-way through. <paramref name="Animating"/>: a shot is still
/// being shown (the snapshot is after it); the next turn opens soon.
/// </summary>
public sealed record Sync(RoundConfig Config, int Seq, MatchSnapshot Snapshot, bool Animating, ShotResult? LastShot) : OnlineMessage;

/// <summary>A player has dropped out (<paramref name="CountdownSeconds"/> until they're skipped), been skipped (away), or is back.</summary>
public sealed record AwayNotice(int Player, bool Away, double? CountdownSeconds, string Text) : OnlineMessage;

/// <summary>Something to show as a toast; <paramref name="Shot"/> when it says why that shot (from this PC) wasn't played.</summary>
public sealed record Notice(string Text, Guid? Shot = null) : OnlineMessage;

/// <summary>The round is over: finished, or the host stopped it (<paramref name="Paused"/>: to carry on later).</summary>
public sealed record RoundOver(bool Paused, string Summary, MatchSnapshot? Snapshot) : OnlineMessage;

// ---- shot outcomes on the wire -----------------------------------------------------------------

/// <summary>
/// A <see cref="ShotOutcome"/> as plain data. The ball's path goes as float32 numbers (time, x, height, z per
/// point) in base64: about 16 KB for a 15-second shot.
/// </summary>
public sealed record OutcomeData(
    string Path, int LandingIndex, double CarryYards, double TotalYards, double ApexYards, double LateralYards, double HangTimeSec, double DescentDeg,
    double StartX, double StartZ, double RestX, double RestZ, Lie RestLie, double NextX, double NextZ,
    bool Holed, bool PickedUp, int Penalty, int Strokes, string Message, string LieNote)
{
    public static OutcomeData Of(ShotOutcome o) =>
        new(EncodePath(o.Flight.Points), o.Flight.LandingIndex, o.Flight.CarryYards, o.Flight.TotalYards, o.Flight.ApexYards,
            o.Flight.LateralYards, o.Flight.HangTimeSec, o.Flight.DescentDeg, o.Start.X, o.Start.Z, o.Rest.X, o.Rest.Z, o.RestLie,
            o.NextBall.X, o.NextBall.Z, o.Holed, o.PickedUp, o.Penalty, o.Strokes, o.Message, o.LieText);

    public ShotOutcome ToOutcome()
    {
        var points = DecodePath(Path);
        int landing = Math.Clamp(LandingIndex, 0, Math.Max(0, points.Count - 1));
        var flight = new FlightResult(points, landing, CarryYards, TotalYards, ApexYards, LateralYards, HangTimeSec, DescentDeg);
        return new ShotOutcome(flight, new P2(StartX, StartZ), new P2(RestX, RestZ), RestLie, new P2(NextX, NextZ), Holed, Penalty, Strokes, Message)
        {
            PickedUp = PickedUp, LieNote = LieNote,
        };
    }

    /// <summary>How long the shot takes to play out, s (the last point's time).</summary>
    public double Duration => DecodePath(Path) is { Count: > 0 } p ? p[^1].T : 0;

    public static string EncodePath(IReadOnlyList<FlightPoint> points)
    {
        var bytes = new byte[points.Count * 16];
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            var span = bytes.AsSpan(i * 16);
            BinaryPrimitives.WriteSingleLittleEndian(span, (float)p.T);
            BinaryPrimitives.WriteSingleLittleEndian(span[4..], (float)p.X);
            BinaryPrimitives.WriteSingleLittleEndian(span[8..], (float)p.Y);
            BinaryPrimitives.WriteSingleLittleEndian(span[12..], (float)p.Z);
        }
        return Convert.ToBase64String(bytes);
    }

    /// <summary>The points in <paramref name="base64"/>; anything that isn't a whole, finite path gives what's good of it.</summary>
    public static IReadOnlyList<FlightPoint> DecodePath(string base64)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException) { return Array.Empty<FlightPoint>(); }
        var list = new List<FlightPoint>(bytes.Length / 16);
        for (int i = 0; i + 16 <= bytes.Length; i += 16)
        {
            var span = bytes.AsSpan(i);
            var p = new FlightPoint(BinaryPrimitives.ReadSingleLittleEndian(span), BinaryPrimitives.ReadSingleLittleEndian(span[4..]),
                                    BinaryPrimitives.ReadSingleLittleEndian(span[8..]), BinaryPrimitives.ReadSingleLittleEndian(span[12..]));
            if (!double.IsFinite(p.T) || !double.IsFinite(p.X) || !double.IsFinite(p.Y) || !double.IsFinite(p.Z)) break;
            list.Add(p);
        }
        return list;
    }
}
