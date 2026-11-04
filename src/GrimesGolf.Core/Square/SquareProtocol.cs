namespace GrimesGolf.Core.Square;

/// <summary>GATT identifiers for the Square Omni (EXGOLF service).</summary>
public static class SquareUuids
{
    public static readonly Guid Service = Guid.Parse("86601001-6b7e-439a-bdd1-489a3213e9bb");
    public static readonly Guid Cmd = Guid.Parse("86602101-6b7e-439a-bdd1-489a3213e9bb");   // the official app writes without response
    public static readonly Guid Evt = Guid.Parse("86602102-6b7e-439a-bdd1-489a3213e9bb");   // notify
    public static readonly Guid DeviceId = Guid.Parse("86602001-6b7e-439a-bdd1-489a3213e9bb");        // read, ASCII serial
    public static readonly Guid HardwareVersion = Guid.Parse("86602002-6b7e-439a-bdd1-489a3213e9bb"); // read, e.g. "3.1"
    public static readonly Guid FirmwareVersion = Guid.Parse("86602003-6b7e-439a-bdd1-489a3213e9bb"); // read, JSON
}

/// <summary>Identity read from the EXGOLF service's read-only characteristics. Any field may be null if the read failed.</summary>
public sealed record SquareDeviceInfo(string? Serial, string? HardwareVersion, string? FirmwareRaw)
{
    /// <summary>Firmware components, e.g. {"launcher":"1.0.1","mmi":"1.4.0","lm":"1.5.8"}. Empty if the payload is not JSON.</summary>
    public IReadOnlyDictionary<string, string> FirmwareComponents { get; } = ParseFirmware(FirmwareRaw);

    public static string? DecodeText(byte[]? raw) =>
        raw == null ? null : System.Text.Encoding.UTF8.GetString(raw).TrimEnd('\0', ' ', '\r', '\n');

    private static IReadOnlyDictionary<string, string> ParseFirmware(string? raw)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw)) return result;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return result;
            foreach (var p in doc.RootElement.EnumerateObject())
                result[p.Name] = p.Value.ValueKind == System.Text.Json.JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText();
        }
        catch (System.Text.Json.JsonException) { }
        return result;
    }
}

public enum SquareDeviceState : byte
{
    None = 0, Idle = 1, Initialising = 2, Detecting = 3, Ready = 4, Shot = 5, Done = 6,
}

// ---- Decoded notifications -------------------------------------------------

public abstract record SquareEvent;

public sealed record SensorEvent(bool BallReady, bool BallDetected) : SquareEvent;

/// <summary>Raw device values: m/s, degrees, rpm. Spin axis / side spin use the DEVICE sign convention (negative = curves right).</summary>
public sealed record BallMetricsEvent(
    double BallSpeedMs, double LaunchDeg, double DirectionDeg,
    int TotalSpinRpm, double SpinAxisDeg, int BackSpinRpm, int SideSpinRpm) : SquareEvent;

public sealed record ClubMetricsEvent(
    byte Mask,
    double? PathDeg, double? FaceDeg, double? AttackDeg, double? DynamicLoftDeg,
    double? ImpactHorizontalMm, double? ImpactVerticalMm,
    double? ClubSpeedMs, double? Smash) : SquareEvent
{
    public bool HasAnyData => Mask != 0 && (PathDeg ?? FaceDeg ?? AttackDeg ?? DynamicLoftDeg ?? ClubSpeedMs) != null;
}

public sealed record HeartbeatAckEvent(SquareDeviceState State) : SquareEvent;
public sealed record BatteryEvent(int Level, int ChargingState) : SquareEvent;

/// <summary>Unsolicited ~3 s tick. Seconds since power-on unless a client has set the clock.</summary>
public sealed record ClockEvent(ulong Seconds) : SquareEvent;
public sealed record DisconnectedEvent : SquareEvent;
public sealed record UnknownEvent(byte[] Data) : SquareEvent;

/// <summary>Internal event used by the bridge to time out a pending club-metrics request.</summary>
public sealed record ClubTimeoutEvent(int PendingId) : SquareEvent;

// ---- Parser ----------------------------------------------------------------

public static class SquarePacketParser
{
    private const short Sentinel = -1; // 0xFFFF (Omni notes)
    private const short Invalid = short.MinValue; // 0x8000: Square's own software treats this as "no value"

    public static SquareEvent Parse(byte[] d)
    {
        if (d.Length == 0) return new DisconnectedEvent();

        if (d[0] == 0x91 && d.Length >= 3)
            return new BatteryEvent(d[1], d[2]);

        if (d[0] == 0x71 && d.Length >= 9)
            return new ClockEvent(System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(1, 8)));

        if (d[0] == 0x11 && d.Length >= 2)
        {
            switch (d[1])
            {
                case 0x01 when d.Length >= 5:
                    return new SensorEvent(BallReady: d[3] == 0x01 || d[3] == 0x02, BallDetected: d[4] == 0x01);

                case 0x02 when d.Length >= 17:
                    return new BallMetricsEvent(
                        BallSpeedMs: I16(d, 3) / 100.0,
                        LaunchDeg: I16(d, 5) / 100.0,
                        DirectionDeg: I16(d, 7) / 100.0,
                        TotalSpinRpm: I16(d, 9),
                        SpinAxisDeg: I16(d, 11) / 100.0,
                        BackSpinRpm: I16(d, 13),
                        SideSpinRpm: I16(d, 15));

                case 0x03 when d.Length >= 4:
                    return new HeartbeatAckEvent((SquareDeviceState)d[3]);

                case 0x07 when d.Length >= 11: // hardware 1.5 sends only the first four fields
                    byte mask = d[2];
                    return new ClubMetricsEvent(mask,
                        Field(d, mask, 0, 3), Field(d, mask, 1, 5), Field(d, mask, 2, 7), Field(d, mask, 3, 9),
                        Field(d, mask, 4, 11), Field(d, mask, 5, 13), Field(d, mask, 6, 15), Field(d, mask, 7, 17));
            }
        }
        return new UnknownEvent(d);
    }

    /// <summary>A little-endian 16-bit field, with the "no value" marker read as 0.</summary>
    private static short I16(byte[] d, int offset)
    {
        short raw = (short)(d[offset] | (d[offset + 1] << 8));
        return raw == Invalid ? (short)0 : raw;
    }

    /// <summary>Value in hundredths, or null when the mask bit is clear or a "no value" marker is present.</summary>
    private static double? Field(byte[] d, byte mask, int bit, int offset)
    {
        if ((mask & (1 << bit)) == 0 || offset + 1 >= d.Length) return null;
        short raw = (short)(d[offset] | (d[offset + 1] << 8));
        return raw is Sentinel or Invalid ? null : raw / 100.0;
    }
}

// ---- Commands --------------------------------------------------------------

/// <summary>Builds the fixed 9-byte host-to-device commands: [0x00][type][seq][payload...] zero padded.</summary>
public sealed class SquareCommandBuilder
{
    private byte _seq;
    private readonly object _lock = new();

    public const byte CmdDetectBall = 0x81, CmdClubSelect = 0x82, CmdHeartbeat = 0x83,
                      CmdQuery = 0x86, CmdRequestClubMetrics = 0x87, CmdSessionInfo = 0x92;

    public byte[] Heartbeat() => Build(CmdHeartbeat);
    public byte[] Query() => Build(CmdQuery);
    /// <summary>Asks for the monitor's OS version, answered by 0x11 0x10 [seq] [major] [minor].</summary>
    public byte[] SessionInfo() => Build(CmdSessionInfo);
    public byte[] RequestClubMetrics() => Build(CmdRequestClubMetrics);

    /// <param name="advancedSpin">0x11 advanced spin, 0x10 standard.</param>
    public byte[] DetectBall(bool on, bool advancedSpin = true) =>
        Build(CmdDetectBall, on ? (byte)0x01 : (byte)0x00, advancedSpin ? (byte)0x11 : (byte)0x10);

    public byte[] ClubSelect(SquareClub club, bool leftHanded) =>
        Build(CmdClubSelect, club.Number, club.Category, leftHanded ? (byte)1 : (byte)0);

    private byte[] Build(byte type, params byte[] payload)
    {
        var b = new byte[9];
        lock (_lock) { b[2] = _seq++; }
        b[1] = type;
        Array.Copy(payload, 0, b, 3, payload.Length);
        return b;
    }
}

public readonly record struct SquareClub(byte Number, byte Category);

/// <summary>
/// Maps the app's club codes (<see cref="ClubPresets"/>: DR, W3, I7, PW, PT, ...) to Square club codes. These are the
/// real-club categories (4 driver, 5 wood/hybrid, 6 iron/wedge, 7 putter), the values Square's own software sends for
/// both Home and Omni. Categories 0-3 are the same clubs with a swing stick, where the monitor waits for the stick.
/// </summary>
public static class SquareClubMap
{
    private static readonly Dictionary<string, SquareClub> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DR"] = new(0x01, 4),
        ["W3"] = new(0x03, 5), ["W5"] = new(0x05, 5), ["W7"] = new(0x07, 5),
        ["H3"] = new(0x0d, 5), ["H4"] = new(0x0e, 5), ["H5"] = new(0x0f, 5),
        ["I3"] = new(0x03, 6), ["I4"] = new(0x04, 6), ["I5"] = new(0x05, 6), ["I6"] = new(0x06, 6),
        ["I7"] = new(0x07, 6), ["I8"] = new(0x08, 6), ["I9"] = new(0x09, 6),
        ["PW"] = new(0x0a, 6), ["GW"] = new(0x0b, 6), ["AW"] = new(0x0b, 6),
        ["SW"] = new(0x0c, 6), ["LW"] = new(0x11, 6),
        ["PT"] = new(0x01, 7),
    };

    public static bool TryGet(string? code, out SquareClub club)
    {
        club = default;
        return code != null && Map.TryGetValue(code.Trim(), out club);
    }
}
