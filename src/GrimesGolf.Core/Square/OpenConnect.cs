using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GrimesGolf.Core.Course;

namespace GrimesGolf.Core.Square;

/// <summary>
/// GSPro Open Connect messages pass over TCP as bare JSON objects with nothing between them, so one read may hold
/// part of an object or several. This splits the text into whole top-level objects, ignoring braces inside strings.
/// </summary>
public sealed class JsonObjectFramer
{
    private readonly StringBuilder _sb = new();
    private int _depth;
    private bool _inString, _escape;

    public IReadOnlyList<string> Feed(string chunk)
    {
        var result = new List<string>();
        foreach (char c in chunk)
        {
            if (_depth == 0 && c != '{') continue;   // whitespace or noise between objects
            _sb.Append(c);
            if (_inString)
            {
                if (_escape) _escape = false;
                else if (c == '\\') _escape = true;
                else if (c == '"') _inString = false;
                continue;
            }
            if (c == '"') _inString = true;
            else if (c == '{') _depth++;
            else if (c == '}' && --_depth == 0)
            {
                result.Add(_sb.ToString());
                _sb.Clear();
            }
        }
        return result;
    }
}

/// <summary>
/// Picks the shots out of the Open Connect messages between a launch monitor connector and GSPro. A shot is a message
/// to GSPro that says it holds ball data and has a ball speed (Square's connector flags its shots as heartbeats too, so
/// that flag is ignored); one resent with the same shot number counts once. Club data sent on its own just after, under
/// the same shot number, goes with that shot. The club is the one GSPro last named in a reply (it says which club the
/// player has in hand), or, for a shot sent before GSPro has named one, the one it names in its answer.
/// </summary>
/// <remarks>
/// Open Connect conventions: speeds in mph, HLA and spin axis positive = right. The ball data is turned back into the
/// monitor's terms (<see cref="BallMetricsEvent"/>: m/s, spin axis negative = right) so it flies exactly like a shot
/// read off the monitor.
/// </remarks>
public sealed class OpenConnectShotTracker
{
    private readonly List<CapturedShot> _shots = new();
    private int? _lastShotNumber;

    public IReadOnlyList<CapturedShot> Shots => _shots;

    /// <summary>The club GSPro last said the player has (its code: DR, W3, I7, PT, ...), if it has said.</summary>
    public string? Club { get; private set; }

    /// <summary>Takes the next message; returns the shot it started or named the club for, if it did either.</summary>
    public CapturedShot? Feed(CapturedMessage m)
    {
        JsonObject? o;
        try { o = JsonNode.Parse(m.Json) as JsonObject; }
        catch (JsonException) { return null; }
        if (o == null) return null;

        if (!m.ToGsPro)
        {
            if (o["Player"]?["Club"] is JsonValue c && c.TryGetValue<string>(out var club) && club.Length > 0)
            {
                Club = club.ToUpperInvariant();
                if (_shots.Count > 0 && _shots[^1].ClubCode == null)
                {
                    _shots[^1] = _shots[^1] with { ClubCode = Club };
                    return _shots[^1];
                }
            }
            return null;
        }

        // IsHeartBeat isn't looked at: Square's connector sets it on its real shots too. A status message carries no
        // ball data (or ball data of zeros), and that is what tells it apart.
        var options = o["ShotDataOptions"];
        int? number = o["ShotNumber"] is JsonValue n && n.TryGetValue<int>(out var sn) ? sn : null;

        // Square's connector sends the club data on its own, just after the ball data and under the same shot number.
        if (!Bool(options, "ContainsBallData"))
        {
            if (Bool(options, "ContainsClubData") && o["ClubData"] is JsonObject later && ReadClub(later) is { } c2
                && _shots.Count > 0 && _shots[^1].Club == null && number == _lastShotNumber)
            {
                _shots[^1] = _shots[^1] with { Club = c2 };
                return _shots[^1];
            }
            return null;
        }
        if (o["BallData"] is not JsonObject ball) return null;
        double mph = Num(ball, "Speed") ?? 0;
        if (mph <= 0) return null;
        if (number != null && number == _lastShotNumber) return null;
        _lastShotNumber = number;

        var b = new BallMetricsEvent(
            BallSpeedMs: mph / Units.MsToMph,
            LaunchDeg: Num(ball, "VLA") ?? 0,
            DirectionDeg: Num(ball, "HLA") ?? 0,
            TotalSpinRpm: (int)Math.Round(Num(ball, "TotalSpin") ?? 0),
            SpinAxisDeg: -(Num(ball, "SpinAxis") ?? 0),
            BackSpinRpm: (int)Math.Round(Num(ball, "BackSpin") ?? 0),
            SideSpinRpm: -(int)Math.Round(Num(ball, "SideSpin") ?? 0));

        ClubMetricsEvent? clubData = Bool(options, "ContainsClubData") && o["ClubData"] is JsonObject cd ? ReadClub(cd) : null;

        _shots.Add(new CapturedShot(_shots.Count + 1, m.At, b, clubData, Club, Array.Empty<byte>()) { Json = m.Json });
        return _shots[^1];
    }

    /// <summary>Club data, if any of it is filled in. Square's connector sends a club speed of 0 (its monitor has none).</summary>
    private static ClubMetricsEvent? ReadClub(JsonObject cd)
    {
        double? Val(string key) => Num(cd, key) is { } v && v != 0 ? v : null;
        var c = new ClubMetricsEvent(0xFF, Val("Path"), Val("FaceToTarget"), Val("AngleOfAttack"), Val("Loft"),
            null, null, Val("Speed") / Units.MsToMph, null);
        return c.HasAnyData ? c : null;
    }

    private static bool Bool(JsonNode? o, string key) => o?[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static double? Num(JsonNode o, string key) =>
        o[key] is JsonValue v && (v.TryGetValue<double>(out var d) || (v.TryGetValue<string>(out var s) && double.TryParse(s,
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d))) ? d : null;
}
