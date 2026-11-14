// The R10's protobuf messages, from LaunchMonitor.proto in gsp-r10-adapter (Copyright (c) 2022 mholow, MIT licence;
// see NOTICE), cross-checked against the tenover crate's schema. Only the fields the app uses are decoded; the rest
// are skipped. Field numbers are noted beside each message.
using System.Buffers.Binary;

namespace GrimesGolf.Core.Garmin;

/// <summary>The R10's own state (State.StateType).</summary>
public enum R10State { Standby = 0, InterferenceTest = 1, Waiting = 2, Recording = 3, Processing = 4, Error = 5 }

/// <summary>How the R10 got the spin (BallMetrics.SpinCalculationType). Only <see cref="Measured"/> is read off the ball.</summary>
public enum R10SpinSource { Ratio = 0, BallFlight = 1, Other = 2, Measured = 3 }

public enum R10ErrorCode { Unknown = 0, Overheating = 1, RadarSaturation = 2, PlatformTilted = 3 }
public enum R10Severity { Warning = 0, Serious = 1, Fatal = 2 }

/// <summary>BallMetrics (Metrics field 3): m/s, degrees, rpm, in the device's signs (spin axis positive = tilted left).</summary>
public sealed record R10Ball(
    float? LaunchAngle, float? LaunchDirection, float? BallSpeed, float? SpinAxis, float? TotalSpin,
    R10SpinSource? SpinSource, int? GolfBallType);

/// <summary>ClubMetrics (Metrics field 4): m/s and degrees.</summary>
public sealed record R10Club(float? HeadSpeed, float? Face, float? Path, float? AttackAngle);

/// <summary>Metrics (AlertDetails field 2): one shot.</summary>
/// <param name="ShotType">0 practice, 1 normal.</param>
public sealed record R10Metrics(uint? ShotId, int? ShotType, R10Ball? Ball, R10Club? Club);

/// <summary>Something the R10 said, decoded from one protobuf message.</summary>
public abstract record R10Event;
public sealed record R10StateEvent(R10State State) : R10Event;
public sealed record R10ShotEvent(R10Metrics Metrics) : R10Event;
public sealed record R10ErrorEvent(R10ErrorCode Code, R10Severity Severity, float? Roll, float? Pitch) : R10Event;
public sealed record R10TiltCalibrationEvent(int Status, int Result) : R10Event;
public sealed record R10SubscribeResult(bool Success) : R10Event;
/// <summary>WakeUpResponse: 0 woken, 1 already awake, 2 failed.</summary>
public sealed record R10WakeResult(int Status) : R10Event;
public sealed record R10TiltResult(float? Roll, float? Pitch) : R10Event;
public sealed record R10ShotConfigResult(bool Success) : R10Event;

/// <summary>The protobuf messages the app sends, the decoder for what comes back, and the device's side for the fake R10.</summary>
public static class R10Proto
{
    // WrapperProto: event = 30 (EventSharing), service = 38 (LaunchMonitorService).
    private const int EventField = 30, ServiceField = 38;

    // ---- requests ----------------------------------------------------------------------------

    /// <summary>LaunchMonitorService.status_request (1).</summary>
    public static byte[] StatusRequest() => Service(w => w.Message(1, _ => { }));
    /// <summary>LaunchMonitorService.wake_up_request (3).</summary>
    public static byte[] WakeUpRequest() => Service(w => w.Message(3, _ => { }));
    /// <summary>LaunchMonitorService.tilt_request (5).</summary>
    public static byte[] TiltRequest() => Service(w => w.Message(5, _ => { }));

    /// <summary>EventSharing.subscribe_request (1) for the launch monitor alerts (AlertType 8): states, shots and errors.</summary>
    public static byte[] SubscribeRequest() =>
        Wrap(EventField, w => w.Message(1, s => s.Message(1, a => a.Varint(1, 8))));

    /// <summary>
    /// LaunchMonitorService.shot_config_request (11). Not sent by the app: the sources disagree on the units (see the
    /// protocol notes), so it's here for experiments only.
    /// </summary>
    public static byte[] ShotConfigRequest(float temperature, float humidity, float altitude, float airDensity, float teeRange) =>
        Service(w => w.Message(11, c => c.Float(1, temperature).Float(2, humidity).Float(3, altitude).Float(4, airDensity).Float(5, teeRange)));

    private static byte[] Service(Action<ProtoWriter> body) => Wrap(ServiceField, body);

    private static byte[] Wrap(int field, Action<ProtoWriter> body) => new ProtoWriter().Message(field, body).ToArray();

    // ---- decoding ----------------------------------------------------------------------------

    /// <summary>Everything one WrapperProto says. Unknown fields are skipped.</summary>
    /// <exception cref="FormatException">Not valid protobuf.</exception>
    public static IReadOnlyList<R10Event> Decode(ReadOnlySpan<byte> wrapper)
    {
        var events = new List<R10Event>();
        foreach (var f in ProtoReader.Read(wrapper))
        {
            if (f.Number == EventField && f.IsMessage) DecodeEventSharing(f.Bytes, events);
            else if (f.Number == ServiceField && f.IsMessage) DecodeService(f.Bytes, events);
        }
        return events;
    }

    private static void DecodeEventSharing(byte[] m, List<R10Event> events)
    {
        foreach (var f in ProtoReader.Read(m))
        {
            if (!f.IsMessage) continue;
            if (f.Number == 2)   // subscribe response: alert_status (1) { subscribe_status (1): 0 success }
                events.Add(new R10SubscribeResult(ProtoReader.Read(f.Bytes).Where(s => s.Number == 1 && s.IsMessage)
                    .All(s => ProtoReader.Read(s.Bytes).FirstOrDefault(x => x.Number == 1).Varint == 0)));
            else if (f.Number == 3)   // notification: AlertDetails at 1001
                foreach (var n in ProtoReader.Read(f.Bytes))
                    if (n.Number == 1001 && n.IsMessage) DecodeAlertDetails(n.Bytes, events);
        }
    }

    /// <summary>AlertDetails: state (1), metrics (2), error (3), tilt_calibration (4).</summary>
    private static void DecodeAlertDetails(byte[] m, List<R10Event> events)
    {
        foreach (var f in ProtoReader.Read(m))
        {
            if (!f.IsMessage) continue;
            switch (f.Number)
            {
                case 1: events.Add(new R10StateEvent(StateOf(f.Bytes))); break;
                case 2: events.Add(new R10ShotEvent(DecodeMetrics(f.Bytes))); break;
                case 3:
                    int code = 0, severity = 0;
                    (float? roll, float? pitch) tilt = (null, null);
                    foreach (var e in ProtoReader.Read(f.Bytes))
                    {
                        if (e.Number == 1) code = (int)e.Varint;
                        else if (e.Number == 2) severity = (int)e.Varint;
                        else if (e.Number == 3 && e.IsMessage) tilt = TiltOf(e.Bytes);
                    }
                    events.Add(new R10ErrorEvent((R10ErrorCode)code, (R10Severity)severity, tilt.roll, tilt.pitch));
                    break;
                case 4:
                    int status = 0, result = 0;
                    foreach (var c in ProtoReader.Read(f.Bytes))
                    {
                        if (c.Number == 1) status = (int)c.Varint;
                        else if (c.Number == 2) result = (int)c.Varint;
                    }
                    events.Add(new R10TiltCalibrationEvent(status, result));
                    break;
            }
        }
    }

    /// <summary>LaunchMonitorService responses: status (2), wake-up (4), tilt (6), shot config (12).</summary>
    private static void DecodeService(byte[] m, List<R10Event> events)
    {
        foreach (var f in ProtoReader.Read(m))
        {
            if (!f.IsMessage) continue;
            switch (f.Number)
            {
                case 2:
                    var state = ProtoReader.Read(f.Bytes).FirstOrDefault(x => x.Number == 1 && x.IsMessage);
                    events.Add(new R10StateEvent(state.Bytes is { } s ? StateOf(s) : R10State.Standby));
                    break;
                case 4: events.Add(new R10WakeResult((int)ProtoReader.Read(f.Bytes).FirstOrDefault(x => x.Number == 1).Varint)); break;
                case 6:
                    var tilt = ProtoReader.Read(f.Bytes).FirstOrDefault(x => x.Number == 1 && x.IsMessage);
                    var (roll, pitch) = tilt.Bytes is { } t ? TiltOf(t) : (null, null);
                    events.Add(new R10TiltResult(roll, pitch));
                    break;
                case 12: events.Add(new R10ShotConfigResult(ProtoReader.Read(f.Bytes).FirstOrDefault(x => x.Number == 1).Varint != 0)); break;
            }
        }
    }

    /// <summary>State { state (1) }: absent means 0, standby.</summary>
    private static R10State StateOf(byte[] m) => (R10State)(int)ProtoReader.Read(m).FirstOrDefault(x => x.Number == 1).Varint;

    /// <summary>Tilt { roll (1), pitch (2) }, degrees.</summary>
    private static (float? Roll, float? Pitch) TiltOf(byte[] m)
    {
        float? roll = null, pitch = null;
        foreach (var f in ProtoReader.Read(m))
        {
            if (f.Number == 1) roll = f.Float;
            else if (f.Number == 2) pitch = f.Float;
        }
        return (roll, pitch);
    }

    /// <summary>Metrics: shot_id (1), shot_type (2), ball_metrics (3), club_metrics (4); swing_metrics (5) isn't used.</summary>
    private static R10Metrics DecodeMetrics(byte[] m)
    {
        uint? id = null;
        int? type = null;
        R10Ball? ball = null;
        R10Club? club = null;
        foreach (var f in ProtoReader.Read(m))
        {
            switch (f.Number)
            {
                case 1: id = (uint)f.Varint; break;
                case 2: type = (int)f.Varint; break;
                case 3 when f.IsMessage:
                    float? la = null, ld = null, bs = null, sa = null, ts = null;
                    int? src = null, ballType = null;
                    foreach (var b in ProtoReader.Read(f.Bytes))
                    {
                        switch (b.Number)
                        {
                            case 1: la = b.Float; break;
                            case 2: ld = b.Float; break;
                            case 3: bs = b.Float; break;
                            case 4: sa = b.Float; break;
                            case 5: ts = b.Float; break;
                            case 6: src = (int)b.Varint; break;
                            case 7: ballType = (int)b.Varint; break;
                        }
                    }
                    ball = new R10Ball(la, ld, bs, sa, ts, (R10SpinSource?)src, ballType);
                    break;
                case 4 when f.IsMessage:
                    float? hs = null, face = null, path = null, attack = null;
                    foreach (var c in ProtoReader.Read(f.Bytes))
                    {
                        switch (c.Number)
                        {
                            case 1: hs = c.Float; break;
                            case 2: face = c.Float; break;
                            case 3: path = c.Float; break;
                            case 4: attack = c.Float; break;
                        }
                    }
                    club = new R10Club(hs, face, path, attack);
                    break;
            }
        }
        return new R10Metrics(id, type, ball, club);
    }

    // ---- the device's side (fake R10, tests) -------------------------------------------------

    /// <summary>An alert notification carrying a state change (EventSharing.notification → AlertDetails.state).</summary>
    public static byte[] StateNotification(R10State state) => Notification(d => d.Message(1, s => s.Varint(1, (ulong)state)));

    /// <summary>An alert notification carrying a shot.</summary>
    public static byte[] ShotNotification(R10Metrics m) => Notification(d => d.Message(2, w => WriteMetrics(w, m)));

    public static byte[] ErrorNotification(R10ErrorCode code, R10Severity severity) =>
        Notification(d => d.Message(3, e => e.Varint(1, (ulong)code).Varint(2, (ulong)severity)));

    private static byte[] Notification(Action<ProtoWriter> details) =>
        Wrap(EventField, e => e.Message(3, n => n.Varint(1, 8).Message(1001, details)));

    public static byte[] StatusResponse(R10State state) => Service(w => w.Message(2, r => r.Message(1, s => s.Varint(1, (ulong)state))));
    public static byte[] WakeUpResponse(int status) => Service(w => w.Message(4, r => r.Varint(1, (ulong)status)));
    public static byte[] TiltResponse(float roll, float pitch) => Service(w => w.Message(6, r => r.Message(1, t => t.Float(1, roll).Float(2, pitch))));
    public static byte[] SubscribeResponse(bool success) =>
        Wrap(EventField, e => e.Message(2, r => r.Message(1, s => s.Varint(1, success ? 0u : 1u).Message(2, a => a.Varint(1, 8)))));

    /// <summary>Which request a WrapperProto is (for the fake R10): "status", "wake", "tilt", "subscribe", "shot-config" or null.</summary>
    public static string? RequestKind(ReadOnlySpan<byte> wrapper)
    {
        foreach (var f in ProtoReader.Read(wrapper))
        {
            if (!f.IsMessage) continue;
            foreach (var g in ProtoReader.Read(f.Bytes))
            {
                if (f.Number == ServiceField)
                    return g.Number switch { 1 => "status", 3 => "wake", 5 => "tilt", 7 => "tilt-calibrate", 9 => "tilt-reset", 11 => "shot-config", _ => null };
                if (f.Number == EventField && g.Number == 1) return "subscribe";
            }
        }
        return null;
    }

    private static void WriteMetrics(ProtoWriter w, R10Metrics m)
    {
        if (m.ShotId is { } id) w.Varint(1, id);
        if (m.ShotType is { } t) w.Varint(2, (ulong)t);
        if (m.Ball is { } b)
            w.Message(3, x =>
            {
                x.Float(1, b.LaunchAngle).Float(2, b.LaunchDirection).Float(3, b.BallSpeed).Float(4, b.SpinAxis).Float(5, b.TotalSpin);
                if (b.SpinSource is { } s) x.Varint(6, (ulong)s);
                if (b.GolfBallType is { } g) x.Varint(7, (ulong)g);
            });
        if (m.Club is { } c)
            w.Message(4, x => x.Float(1, c.HeadSpeed).Float(2, c.Face).Float(3, c.Path).Float(4, c.AttackAngle));
    }
}

/// <summary>One field read from a protobuf message.</summary>
/// <param name="Bytes">Length-delimited (wire type 2) or fixed-width (1, 5) contents; null for a varint.</param>
public readonly record struct ProtoField(int Number, int WireType, ulong Varint, byte[]? Bytes)
{
    [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(true, nameof(Bytes))]
    public bool IsMessage => WireType == 2 && Bytes != null;

    /// <summary>A 32-bit float (wire type 5), or null if the field isn't one.</summary>
    public float? Float => WireType == 5 && Bytes is { Length: 4 } b ? BinaryPrimitives.ReadSingleLittleEndian(b) : null;
}

/// <summary>Just enough protobuf reading for the R10: varints, 32/64-bit fixed and length-delimited fields.</summary>
public static class ProtoReader
{
    /// <exception cref="FormatException">Truncated, or a wire type protobuf 3 doesn't use.</exception>
    public static List<ProtoField> Read(ReadOnlySpan<byte> data)
    {
        var fields = new List<ProtoField>();
        int i = 0;
        while (i < data.Length)
        {
            ulong key = ReadVarint(data, ref i);
            int number = (int)(key >> 3), wire = (int)(key & 7);
            if (number <= 0) throw new FormatException($"Protobuf field number {number} at {i}.");
            switch (wire)
            {
                case 0: fields.Add(new ProtoField(number, 0, ReadVarint(data, ref i), null)); break;
                case 1: fields.Add(new ProtoField(number, 1, 0, Take(data, ref i, 8))); break;
                case 2:
                    ulong length = ReadVarint(data, ref i);
                    if (length > (ulong)(data.Length - i)) throw new FormatException($"Protobuf field {number} runs past the end.");
                    fields.Add(new ProtoField(number, 2, 0, Take(data, ref i, (int)length)));
                    break;
                case 5: fields.Add(new ProtoField(number, 5, 0, Take(data, ref i, 4))); break;
                default: throw new FormatException($"Protobuf wire type {wire} (field {number}) isn't supported.");
            }
        }
        return fields;
    }

    private static byte[] Take(ReadOnlySpan<byte> data, ref int i, int n)
    {
        if (n > data.Length - i) throw new FormatException("Protobuf field runs past the end.");
        var b = data.Slice(i, n).ToArray();
        i += n;
        return b;
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int i)
    {
        ulong value = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            if (i >= data.Length) throw new FormatException("Protobuf varint runs past the end.");
            byte b = data[i++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
        }
        throw new FormatException("Protobuf varint is too long.");
    }
}

/// <summary>Just enough protobuf writing for the R10's requests (and the fake R10's replies).</summary>
public sealed class ProtoWriter
{
    private readonly List<byte> _bytes = new();

    public ProtoWriter Varint(int field, ulong value)
    {
        Key(field, 0);
        Raw(value);
        return this;
    }

    /// <summary>A float field; nothing when <paramref name="value"/> is null.</summary>
    public ProtoWriter Float(int field, float? value)
    {
        if (value is not { } v) return this;
        Key(field, 5);
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(b, v);
        _bytes.AddRange(b.ToArray());
        return this;
    }

    public ProtoWriter Message(int field, Action<ProtoWriter> body)
    {
        var inner = new ProtoWriter();
        body(inner);
        Key(field, 2);
        Raw((ulong)inner._bytes.Count);
        _bytes.AddRange(inner._bytes);
        return this;
    }

    public byte[] ToArray() => _bytes.ToArray();

    private void Key(int field, int wire) => Raw((ulong)field << 3 | (uint)wire);

    private void Raw(ulong v)
    {
        while (v >= 0x80) { _bytes.Add((byte)(v | 0x80)); v >>= 7; }
        _bytes.Add((byte)v);
    }
}
