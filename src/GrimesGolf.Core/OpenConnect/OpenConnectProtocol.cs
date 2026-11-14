using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GrimesGolf.Core.OpenConnect;

// GSPro Open Connect v1 (gsprogolf.com/GSProConnectV1.html): the simulator listens on TCP port 921, a launch
// monitor's connector app connects with no authentication and sends JSON objects (shots, ready signals, heartbeats),
// and the simulator answers 200 (shot received), 201 (player information: hand and club) or 5xx (failure).
// The spec leaves some things out, so these follow what the open-source connectors send and expect
// (mholow/gsp-r10-adapter, Duwaynef/MLM2PRO-BT-APP): no framing between objects, Player.DistanceToTarget,
// and the signs. Speeds are mph with Units "Yards" (the default); angles are degrees, positive = right (HLA, spin
// axis, path, face) or up (VLA, attack angle); spin is rpm, and a positive spin axis or sidespin curves the ball right.

/// <summary>One message from a connector: a shot, a heartbeat, or the monitor's ready state.</summary>
public sealed record OpenConnectMessage
{
    public string? DeviceID { get; init; }
    public string? Units { get; init; }
    public int ShotNumber { get; init; }
    public string? APIversion { get; init; }
    public OpenConnectBallData? BallData { get; init; }
    public OpenConnectClubData? ClubData { get; init; }
    public OpenConnectShotDataOptions? ShotDataOptions { get; init; }

    /// <summary>
    /// It carries a shot: ball data flagged as present, with a ball speed. (Not "not a heartbeat": one connector sends
    /// its shots with <c>IsHeartBeat</c> true.)
    /// </summary>
    [JsonIgnore]
    public bool IsShot => ShotDataOptions?.ContainsBallData == true && BallData is { Speed: > 0 };
}

public sealed record OpenConnectBallData
{
    public double Speed { get; init; }
    public double SpinAxis { get; init; }
    public double TotalSpin { get; init; }
    public double BackSpin { get; init; }
    public double SideSpin { get; init; }
    public double HLA { get; init; }
    public double VLA { get; init; }
    public double CarryDistance { get; init; }
}

public sealed record OpenConnectClubData
{
    public double Speed { get; init; }
    public double AngleOfAttack { get; init; }
    public double FaceToTarget { get; init; }
    public double Lie { get; init; }
    public double Loft { get; init; }
    public double Path { get; init; }
    public double SpeedAtImpact { get; init; }
    public double VerticalFaceImpact { get; init; }
    public double HorizontalFaceImpact { get; init; }
    public double ClosureRate { get; init; }
}

public sealed record OpenConnectShotDataOptions
{
    public bool ContainsBallData { get; init; }
    public bool ContainsClubData { get; init; }
    public bool? LaunchMonitorIsReady { get; init; }
    public bool? LaunchMonitorBallDetected { get; init; }
    public bool? IsHeartBeat { get; init; }
}

/// <summary>The player up, as sent to the connector (code 201): it sets the monitor's club and hand from it.</summary>
public sealed record OpenConnectPlayer(string Handed, string Club, double DistanceToTarget);

/// <summary>A reply to the connector: 200 shot received, 201 player information, 501 shot not used.</summary>
public sealed record OpenConnectResponse(int Code, string Message, OpenConnectPlayer? Player = null);

/// <summary>A connector's shot, checked and in the app's display conventions.</summary>
public sealed record OpenConnectShot(RangeShot Shot, string? Device, int ShotNumber, IReadOnlyList<string> Warnings);

public static class OpenConnectProtocol
{
    public const int DefaultPort = 921;
    public const int ShotReceived = 200, PlayerInformation = 201, ShotFailed = 501;

    /// <summary>The club codes GSPro uses (the connectors map them to their monitor's clubs). The app's codes are a subset.</summary>
    public static readonly IReadOnlySet<string> Clubs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "DR", "W2", "W3", "W4", "W5", "W6", "W7", "I1", "I2", "I3", "I4", "I5", "I6", "I7", "I8", "I9",
        "H2", "H3", "H4", "H5", "H6", "H7", "PW", "GW", "SW", "LW", "PT",
    };

    private static readonly JsonSerializerOptions Read = new()
    {
        PropertyNameCaseInsensitive = true,   // "APIversion" and "APIVersion" are both sent
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonSerializerOptions Write = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>Parses one JSON object; null if it isn't a message (bad JSON, or not an object).</summary>
    public static OpenConnectMessage? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<OpenConnectMessage>(json, Read); }
        catch (JsonException) { return null; }
    }

    public static string Serialize(OpenConnectResponse response) => JsonSerializer.Serialize(response, Write);

    public static OpenConnectResponse Received() => new(ShotReceived, "Shot received successfully");

    public static OpenConnectResponse Failed(string why) => new(ShotFailed, why);

    /// <summary>
    /// The player up: hand, club (the app's codes are GSPro's; anything else goes as "unknown") and the distance to the
    /// target in yards. The distance is never sent as 0: the MLM2PRO connector only arms the monitor when it isn't.
    /// </summary>
    public static OpenConnectResponse PlayerInfo(bool leftHanded, string clubCode, double distanceYards) =>
        new(PlayerInformation, "GSPro Player Information",
            new OpenConnectPlayer(leftHanded ? "LH" : "RH", Clubs.Contains(clubCode) ? clubCode.ToUpperInvariant() : "unknown",
                                  Math.Round(Math.Max(1, double.IsFinite(distanceYards) ? distanceYards : 1), 1)));

    // Limits for a shot to be believed (a connector passing on a misread, or a bad conversion, gets a 501 instead).
    private const double MaxBallMph = 250, MaxClubMph = 200, MaxSpinRpm = 20000, MinLaunch = -20, MaxLaunch = 90, MaxSide = 60;

    /// <summary>
    /// The shot in display conventions, or why it can't be used. Total spin and axis are used when given; otherwise
    /// they come from backspin and sidespin.
    /// </summary>
    public static (OpenConnectShot? Shot, string? Error) ToShot(OpenConnectMessage m)
    {
        if (!m.IsShot) return (null, "No ball data in the message");
        var b = m.BallData!;
        var warnings = new List<string>();
        double[] numbers = { b.Speed, b.VLA, b.HLA, b.TotalSpin, b.SpinAxis, b.BackSpin, b.SideSpin, m.ClubData?.Speed ?? 0 };
        if (numbers.Any(v => !double.IsFinite(v))) return (null, "A number in the shot isn't a number");
        if (m.Units is { } u && !u.Equals("Yards", StringComparison.OrdinalIgnoreCase))
            warnings.Add($"Units \"{u}\": speeds taken as mph anyway (only \"Yards\" is known).");

        double total, axis;
        if (b.TotalSpin > 0) (total, axis) = (b.TotalSpin, b.SpinAxis);
        else if (b.BackSpin != 0 || b.SideSpin != 0)
        {
            total = Math.Sqrt(b.BackSpin * b.BackSpin + b.SideSpin * b.SideSpin);
            axis = Math.Atan2(b.SideSpin, b.BackSpin) * 180 / Math.PI;
        }
        else (total, axis) = (0, 0);   // a putt, or a monitor that didn't read the spin
        if (b.TotalSpin <= 0 && b.BackSpin == 0 && b.SideSpin == 0 && b.Speed > 20) warnings.Add("No spin in the shot.");

        if (b.Speed > MaxBallMph) return (null, $"Ball speed {b.Speed:0.#} mph is beyond what's believable");
        if (b.VLA < MinLaunch || b.VLA > MaxLaunch) return (null, $"Launch angle {b.VLA:0.#}° is out of range");
        if (Math.Abs(b.HLA) > MaxSide) return (null, $"Launch direction {b.HLA:0.#}° is out of range");
        if (total < 0 || total > MaxSpinRpm) return (null, $"Spin {total:0} rpm is out of range");
        if (Math.Abs(axis) > 90) return (null, $"Spin axis {axis:0.#}° is out of range");

        double? club = m.ShotDataOptions!.ContainsClubData && m.ClubData is { Speed: > 0 and <= MaxClubMph } c ? c.Speed : null;
        double? smash = club > 0 ? b.Speed / club : null;
        var shot = new RangeShot(b.Speed, b.VLA, b.HLA, total, axis, club, smash) { Source = ShotSource.Monitor };
        return (new OpenConnectShot(shot, m.DeviceID, m.ShotNumber, warnings), null);
    }
}

/// <summary>
/// Splits a TCP byte stream into whole JSON objects. Connectors send objects back to back, with or without newlines
/// between, and one read can hold several objects or part of one. Braces inside strings don't count.
/// </summary>
public sealed class JsonObjectFramer
{
    /// <summary>A message that grows past this without closing is junk: it's dropped.</summary>
    public const int MaxObjectChars = 64 * 1024;

    private readonly Decoder _utf8 = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _current = new();
    private int _depth;
    private bool _inString, _escaped;

    /// <summary>Raised when an over-long object is thrown away.</summary>
    public event Action? Overflowed;

    /// <summary>Feeds received bytes; returns the objects they complete, in order.</summary>
    public List<string> Push(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[_utf8.GetCharCount(bytes, flush: false)];
        _utf8.GetChars(bytes, chars, flush: false);
        return Push(chars);
    }

    public List<string> Push(ReadOnlySpan<char> text)
    {
        var done = new List<string>();
        foreach (char ch in text)
        {
            if (_depth == 0)
            {
                if (ch != '{') continue;   // whitespace, newlines or junk between objects
                _depth = 1;
                _current.Clear().Append(ch);
                continue;
            }
            _current.Append(ch);
            if (_inString)
            {
                if (_escaped) _escaped = false;
                else if (ch == '\\') _escaped = true;
                else if (ch == '"') _inString = false;
            }
            else if (ch == '"') _inString = true;
            else if (ch == '{') _depth++;
            else if (ch == '}' && --_depth == 0)
            {
                done.Add(_current.ToString());
                _current.Clear();
            }
            if (_current.Length > MaxObjectChars)
            {
                _current.Clear();
                _depth = 0;
                _inString = _escaped = false;
                Overflowed?.Invoke();
            }
        }
        return done;
    }
}
