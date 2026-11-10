using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GrimesGolf.Core.Square;

/// <summary>One Bluetooth packet as it went over the air: when, which way, and the bytes.</summary>
public sealed record CapturedPacket(DateTimeOffset At, bool Outgoing, byte[] Data)
{
    public string Hex => Convert.ToHexString(Data);
}

/// <summary>One GSPro Open Connect message, as text: when, which way (to GSPro or from it), and the JSON object.</summary>
public sealed record CapturedMessage(DateTimeOffset At, bool ToGsPro, string Json);

/// <summary>What a capture file says about itself (its first line). Device details go in as notes, as each connection is made.</summary>
public sealed record CaptureHeader(DateTimeOffset Started, string Mode, string? Note);

/// <summary>A free-text line in a capture: connections, club changes, notes typed while recording.</summary>
public sealed record CaptureNote(DateTimeOffset At, string Text);

/// <summary>
/// A raw capture: a JSON-lines file, a header line and then one line per Bluetooth packet or Open Connect message,
/// written as they arrive (flushed per line, so a crash loses at most the one in hand). Nothing is decoded on the way
/// in, so a capture stays useful whatever the parser later learns.
/// <c>{"t":"2026-11-09T19:30:01.1234567-06:00","dir":"rx","hex":"1102..."}</c>,
/// <c>{"t":"...","dir":"to-gspro","json":"{\"DeviceID\":...}"}</c>
/// </summary>
public sealed class PacketCaptureWriter : IDisposable
{
    /// <summary>Only what JSON requires escaped, so the lines read easily (no " for every quote inside a message).</summary>
    private static readonly JsonSerializerOptions Plain = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly StreamWriter _w;
    private readonly object _lock = new();

    public PacketCaptureWriter(string path, CaptureHeader header)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        FilePath = path;
        _w = new StreamWriter(path, append: false) { AutoFlush = true };
        var h = new JsonObject { ["capture"] = 1, ["started"] = header.Started.ToString("o"), ["mode"] = header.Mode, ["note"] = header.Note };
        _w.WriteLine(h.ToJsonString(Plain));
    }

    public string FilePath { get; }

    public void Write(CapturedPacket p)
    {
        string line = new JsonObject { ["t"] = p.At.ToString("o"), ["dir"] = p.Outgoing ? "tx" : "rx", ["hex"] = p.Hex }.ToJsonString(Plain);
        lock (_lock) _w.WriteLine(line);
    }

    public void Write(CapturedMessage m)
    {
        string line = new JsonObject { ["t"] = m.At.ToString("o"), ["dir"] = m.ToGsPro ? "to-gspro" : "from-gspro", ["json"] = m.Json }.ToJsonString(Plain);
        lock (_lock) _w.WriteLine(line);
    }

    /// <summary>A free-text marker in the capture (club changes, notes typed while recording).</summary>
    public void Note(DateTimeOffset at, string text)
    {
        string line = new JsonObject { ["t"] = at.ToString("o"), ["note"] = text }.ToJsonString(Plain);
        lock (_lock) _w.WriteLine(line);
    }

    public void Dispose() { lock (_lock) _w.Dispose(); }
}

/// <summary>
/// A shot found in a capture: its number (from 1), when, the ball data, and the club it was hit with as far as the
/// capture says. <see cref="Raw"/> is the Bluetooth packet it came from, or <see cref="Json"/> the Open Connect message.
/// </summary>
public sealed record CapturedShot(int Number, DateTimeOffset At, BallMetricsEvent Ball, ClubMetricsEvent? Club, string? ClubCode, byte[] Raw)
{
    public string? Json { get; init; }
}

/// <summary>Everything in a capture file.</summary>
public sealed record Capture(CaptureHeader? Header, List<CapturedPacket> Packets, List<CapturedMessage> Messages, List<CaptureNote> Notes)
{
    /// <summary>The shots: from the Open Connect messages if the capture has any, otherwise from the Bluetooth packets.</summary>
    public List<CapturedShot> Shots() => Messages.Count > 0 ? PacketCapture.Shots(Messages) : PacketCapture.Shots(Packets);
}

public static class PacketCapture
{
    /// <summary>Reads a capture. Lines that don't parse (a line cut short by a crash) are skipped.</summary>
    public static Capture Read(string path)
    {
        CaptureHeader? header = null;
        var packets = new List<CapturedPacket>();
        var messages = new List<CapturedMessage>();
        var notes = new List<CaptureNote>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0) continue;
            JsonObject? o;
            try { o = JsonNode.Parse(line) as JsonObject; }
            catch (JsonException) { continue; }
            if (o == null) continue;
            if (o["capture"] != null)
            {
                DateTimeOffset.TryParse(Str(o, "started"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var started);
                header = new CaptureHeader(started, Str(o, "mode") ?? "?", Str(o, "note"));
                continue;
            }
            if (!DateTimeOffset.TryParse(Str(o, "t"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) continue;
            if (Str(o, "note") is { } note) { notes.Add(new CaptureNote(at, note)); continue; }
            if (Str(o, "json") is { } json) { messages.Add(new CapturedMessage(at, Str(o, "dir") == "to-gspro", json)); continue; }
            if (Str(o, "hex") is not { } hex) continue;
            try { packets.Add(new CapturedPacket(at, Str(o, "dir") == "tx", Convert.FromHexString(hex))); }
            catch (FormatException) { }
        }
        return new Capture(header, packets, messages, notes);
    }

    /// <summary>The shots in a stream of Bluetooth packets, in order (see <see cref="ShotTracker"/>).</summary>
    public static List<CapturedShot> Shots(IEnumerable<CapturedPacket> packets)
    {
        var tracker = new ShotTracker();
        foreach (var p in packets) tracker.Feed(p);
        return tracker.Shots.ToList();
    }

    /// <summary>The shots in a stream of Open Connect messages, in order (see <see cref="OpenConnectShotTracker"/>).</summary>
    public static List<CapturedShot> Shots(IEnumerable<CapturedMessage> messages)
    {
        var tracker = new OpenConnectShotTracker();
        foreach (var m in messages) tracker.Feed(m);
        return tracker.Shots.ToList();
    }

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

/// <summary>
/// Picks the shots out of a stream of packets, live or from a capture. A notification the monitor sends twice (byte
/// for byte) counts once. The club is the one the monitor last reported in a heartbeat reply, and club data is a reply
/// (0x11 0x07) that comes within 3 seconds of the ball data and before the next shot.
/// </summary>
public sealed class ShotTracker
{
    private readonly List<CapturedShot> _shots = new();
    private byte[]? _last;

    public IReadOnlyList<CapturedShot> Shots => _shots;

    /// <summary>The club the monitor last said it was set to (the app's code), if known.</summary>
    public string? Club { get; private set; }

    /// <summary>Takes the next packet; returns the shot it started or added club data to, if it did either.</summary>
    public CapturedShot? Feed(CapturedPacket p)
    {
        if (p.Outgoing) return null;
        if (_last != null && p.Data.AsSpan().SequenceEqual(_last)) return null;
        _last = p.Data;
        var d = p.Data;
        if (d.Length >= 7 && d[0] == 0x11 && d[1] == 0x03) Club = SquareClubMap.CodeFor(d[5], d[6]) ?? Club;
        switch (SquarePacketParser.Parse(d))
        {
            case BallMetricsEvent b:
                _shots.Add(new CapturedShot(_shots.Count + 1, p.At, b, null, Club, d));
                return _shots[^1];
            case ClubMetricsEvent c when _shots.Count > 0 && _shots[^1].Club == null && c.HasAnyData
                                         && (p.At - _shots[^1].At).TotalSeconds < 3:
                _shots[^1] = _shots[^1] with { Club = c };
                return _shots[^1];
        }
        return null;
    }
}

/// <summary>
/// Another simulator's numbers for a captured shot (GSPro's, typed in while recording): carry and total in yards,
/// apex in feet, offline in yards (positive = right). Any of them may be missing.
/// </summary>
public sealed record ReferenceShot(int Shot, double? CarryYds, double? TotalYds, double? ApexFt, double? OfflineYds)
{
    public const string CsvHeader = "shot,carry_yd,total_yd,apex_ft,offline_yd";

    /// <summary>
    /// Reads "carry total apex offline" as typed, e.g. <c>152 168 85 R4</c>. "-" or "?" skips a value. Apex is in feet;
    /// a <c>y</c> suffix (<c>28y</c>) means yards. Offline is <c>R4</c>/<c>L4</c> or signed, positive = right.
    /// </summary>
    public static bool TryParse(int shot, string text, out ReferenceShot reference, out string? error)
    {
        reference = new ReferenceShot(shot, null, null, null, null);
        error = null;
        var parts = text.Split(new[] { ' ', ',', '\t', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > 4) { error = "type carry, total, apex and offline (the last ones can be left off)"; return false; }
        var values = new double?[4];
        for (int i = 0; i < parts.Length; i++)
        {
            string s = parts[i].Trim().ToLowerInvariant();
            if (s is "-" or "?") continue;
            double scale = 1, sign = 1;
            if (i == 2)
            {
                if (s.EndsWith("ft")) s = s[..^2];
                else if (s.EndsWith('\'')) s = s[..^1];
                else if (s.EndsWith("yd") || s.EndsWith('y')) { s = s.TrimEnd('d').TrimEnd('y'); scale = 3; }
            }
            if (i == 3 && s.Length > 0 && (s[0] == 'r' || s[0] == 'l')) { sign = s[0] == 'l' ? -1 : 1; s = s[1..]; }
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) { error = $"'{parts[i]}' isn't a number"; return false; }
            values[i] = v * scale * sign;
        }
        reference = new ReferenceShot(shot, values[0], values[1], values[2], values[3]);
        return true;
    }

    public string ToCsv() => string.Join(",", Shot.ToString(CultureInfo.InvariantCulture), F(CarryYds), F(TotalYds), F(ApexFt), F(OfflineYds));

    /// <summary>Reads a reference file; for a shot listed twice, the later line wins (so a correction can just be appended).</summary>
    public static Dictionary<int, ReferenceShot> ReadCsv(string path)
    {
        var result = new Dictionary<int, ReferenceShot>();
        if (!File.Exists(path)) return result;
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var c = line.Split(',');
            if (c.Length < 5 || !int.TryParse(c[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int shot)) continue;
            result[shot] = new ReferenceShot(shot, P(c[1]), P(c[2]), P(c[3]), P(c[4]));
        }
        return result;
    }

    private static string F(double? v) => v?.ToString("0.#", CultureInfo.InvariantCulture) ?? "";
    private static double? P(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
}
