// The R10's link layers, as worked out by the community: see docs/garmin-r10-protocol.md. The handshake bytes and
// the acknowledgement layout follow gsp-r10-adapter (Copyright (c) 2022 mholow, MIT licence; see NOTICE).
using System.Buffers.Binary;

namespace GrimesGolf.Core.Garmin;

/// <summary>GATT identifiers on the Garmin Approach R10.</summary>
public static class R10Uuids
{
    /// <summary>Garmin's MultiLink service: every GFDI message goes through it.</summary>
    public static readonly Guid MultiLinkService = Guid.Parse("6a4e2800-667b-11e3-949a-0800200c9a66");
    /// <summary>The channel pair gsp-r10-adapter uses: notifications in on 2812, writes out on 2822.</summary>
    public static readonly Guid Notify = Guid.Parse("6a4e2812-667b-11e3-949a-0800200c9a66");
    public static readonly Guid Write = Guid.Parse("6a4e2822-667b-11e3-949a-0800200c9a66");
    /// <summary>Channel 0 (tenover's choice): the fallback if the 2812/2822 pair isn't there.</summary>
    public static readonly Guid NotifyChannel0 = Guid.Parse("6a4e2810-667b-11e3-949a-0800200c9a66");
    public static readonly Guid WriteChannel0 = Guid.Parse("6a4e2820-667b-11e3-949a-0800200c9a66");

    /// <summary>The R10's measurement service. Its notifications aren't understood yet: they're only logged.</summary>
    public static readonly Guid MeasurementService = Guid.Parse("6a4e3400-667b-11e3-949a-0800200c9a66");
    public static readonly Guid Measurement = Guid.Parse("6a4e3401-667b-11e3-949a-0800200c9a66");
    public static readonly Guid ControlPoint = Guid.Parse("6a4e3402-667b-11e3-949a-0800200c9a66");
    public static readonly Guid Status = Guid.Parse("6a4e3403-667b-11e3-949a-0800200c9a66");

    public static readonly Guid BatteryService = Guid.Parse("0000180f-0000-1000-8000-00805f9b34fb");
    public static readonly Guid BatteryLevel = Guid.Parse("00002a19-0000-1000-8000-00805f9b34fb");
    public static readonly Guid DeviceInfoService = Guid.Parse("0000180a-0000-1000-8000-00805f9b34fb");
    public static readonly Guid ModelNumber = Guid.Parse("00002a24-0000-1000-8000-00805f9b34fb");
    public static readonly Guid SerialNumber = Guid.Parse("00002a25-0000-1000-8000-00805f9b34fb");
    public static readonly Guid FirmwareRevision = Guid.Parse("00002a28-0000-1000-8000-00805f9b34fb");
}

/// <summary>Garmin MultiLink: before anything else the host registers for the GFDI service and is given a handle byte.</summary>
public static class MultiLink
{
    /// <summary>
    /// REGISTER for GFDI: [0x00 handle][0x00 REGISTER][transaction u64][service u16 = 1][0x00]. Written to the write
    /// characteristic like everything else; the leading 0x00 is the handle every write starts with, 0 until one is given.
    /// </summary>
    public static byte[] Register(ulong transaction = 0)
    {
        var b = new byte[13];
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(2), transaction);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(10), 1);
        return b;
    }

    /// <summary>
    /// The answer to <see cref="Register"/>: [0x00][0x01 REGISTER_RESPONSE][transaction u64][service u16][status][handle].
    /// </summary>
    public static bool TryParseRegisterResponse(ReadOnlySpan<byte> n, out byte status, out byte handle)
    {
        status = handle = 0;
        if (n.Length < 14 || n[0] != 0 || n[1] != 1 || BinaryPrimitives.ReadUInt16LittleEndian(n[10..]) != 1) return false;
        status = n[12];
        handle = n[13];
        return true;
    }

    /// <summary>A register response from the device side (for the fake R10 and tests).</summary>
    public static byte[] RegisterResponse(byte handle, ulong transaction = 0)
    {
        var b = new byte[16];
        b[1] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(2), transaction);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(10), 1);
        b[13] = handle;
        return b;
    }
}

/// <summary>One decoded GFDI message.</summary>
/// <param name="TransactionId">Set when the frame used the compressed header.</param>
public sealed record GfdiFrame(ushort Type, byte[] Payload, byte? TransactionId = null)
{
    /// <summary>For an acknowledgement (5000): the type it acknowledges and its status.</summary>
    public (ushort Type, byte Status)? Acknowledges =>
        Type == Gfdi.Ack && Payload.Length >= 3 ? (BinaryPrimitives.ReadUInt16LittleEndian(Payload), Payload[2]) : null;
}

/// <summary>
/// GFDI (Garmin Framework Device Interface) frames: [length u16][type u16][payload][CRC-16/ARC u16], little-endian,
/// the length counting the whole frame. Sent COBS-encoded between 0x00 bytes.
/// </summary>
public static class Gfdi
{
    public const ushort Ack = 5000, FitDefinition = 5011, FitData = 5012, DeviceInformation = 5024,
                        ProtobufRequest = 5043, ProtobufResponse = 5044, Configuration = 5050;

    /// <summary>Bytes in front of the protobuf in a 5043/5044 payload: request id u16, offset u32, total u32, length u32.</summary>
    public const int ProtobufHeaderLength = 14;

    public static byte[] BuildFrame(ushort type, ReadOnlySpan<byte> payload)
    {
        var f = new byte[6 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(f, (ushort)f.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(2), type);
        payload.CopyTo(f.AsSpan(4));
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(f.Length - 2), Crc16Arc.Compute(f.AsSpan(0, f.Length - 2)));
        return f;
    }

    /// <summary>
    /// A decoded frame, with either header: the standard one (type u16) or the compressed one some Garmin devices
    /// switch to after a protocol 150 handshake (type − 5000 in one byte, then a transaction id with its top bit set).
    /// </summary>
    /// <exception cref="FormatException">Too short, or the CRC doesn't match.</exception>
    public static GfdiFrame ParseFrame(ReadOnlySpan<byte> f)
    {
        if (f.Length < 6) throw new FormatException($"GFDI frame of {f.Length} bytes is too short.");
        ushort crc = BinaryPrimitives.ReadUInt16LittleEndian(f[^2..]), want = Crc16Arc.Compute(f[..^2]);
        if (crc != want) throw new FormatException($"GFDI CRC {crc:X4} doesn't match {want:X4}.");
        var payload = f[4..^2].ToArray();
        return (f[3] & 0x80) != 0
            ? new GfdiFrame((ushort)(f[2] + 5000), payload, (byte)(f[3] & 0x7F))
            : new GfdiFrame(BinaryPrimitives.ReadUInt16LittleEndian(f[2..]), payload);
    }

    /// <summary>An acknowledgement: [type acknowledged u16][status][payload].</summary>
    public static byte[] BuildAck(ushort acknowledged, byte status, ReadOnlySpan<byte> payload = default)
    {
        var p = new byte[3 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(p, acknowledged);
        p[2] = status;
        payload.CopyTo(p.AsSpan(3));
        return BuildFrame(Ack, p);
    }

    /// <summary>
    /// How a 5043/5044 is acknowledged: status 0, then its request id and seven zero bytes (offset and protobuf
    /// status), as gsp-r10-adapter does.
    /// </summary>
    public static byte[] BuildProtobufAck(ushort type, ushort requestId)
    {
        var p = new byte[9];
        BinaryPrimitives.WriteUInt16LittleEndian(p, requestId);
        return BuildAck(type, 0, p);
    }

    /// <summary>A whole protobuf message in one 5043/5044 (the R10's messages are all small enough).</summary>
    public static byte[] BuildProtobuf(ushort type, ushort requestId, ReadOnlySpan<byte> proto)
    {
        var p = new byte[ProtobufHeaderLength + proto.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(p, requestId);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(6), (uint)proto.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(10), (uint)proto.Length);
        proto.CopyTo(p.AsSpan(ProtobufHeaderLength));
        return BuildFrame(type, p);
    }

    /// <summary>The request id and protobuf bytes of a 5043/5044 payload.</summary>
    /// <exception cref="FormatException">Too short, or a fragment of a longer message (not seen from the R10).</exception>
    public static (ushort RequestId, byte[] Proto) ParseProtobuf(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < ProtobufHeaderLength) throw new FormatException($"Protobuf payload of {payload.Length} bytes is too short.");
        ushort id = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(payload[2..]), total = BinaryPrimitives.ReadUInt32LittleEndian(payload[6..]),
             length = BinaryPrimitives.ReadUInt32LittleEndian(payload[10..]);
        if (offset != 0 || length != total) throw new FormatException($"Protobuf message {id} arrived in parts (offset {offset}, {length} of {total} bytes).");
        if (ProtobufHeaderLength + length > payload.Length) throw new FormatException($"Protobuf message {id} claims {length} bytes but carries {payload.Length - ProtobufHeaderLength}.");
        return (id, payload.Slice(ProtobufHeaderLength, (int)length).ToArray());
    }

    /// <summary>
    /// Messages that carry the device's identity (serial, unit id, names) or keys, and so are kept out of raw logs
    /// that may be shared: device information, FIT and file transfers, authentication.
    /// </summary>
    public static bool CarriesIdentity(ushort type) =>
        type is DeviceInformation or FitDefinition or FitData or 5003 or 5004 || type is >= 5101 and <= 5112;

    /// <summary>A frame ready to write: COBS between 0x00 bytes, cut into chunks of <paramref name="chunk"/> bytes, each behind the handle.</summary>
    public static IEnumerable<byte[]> Chunks(byte handle, byte[] frame, int chunk = 19)
    {
        var encoded = Cobs.Encode(frame);
        var wire = new byte[encoded.Length + 2];
        encoded.CopyTo(wire, 1);
        for (int at = 0; at < wire.Length; at += chunk)
        {
            int n = Math.Min(chunk, wire.Length - at);
            var c = new byte[n + 1];
            c[0] = handle;
            Array.Copy(wire, at, c, 1, n);
            yield return c;
        }
    }
}

/// <summary>
/// Rebuilds GFDI frames from notifications: each starts with the MultiLink handle, and the rest is a stream of
/// COBS-encoded frames between 0x00 bytes, which can be split across notifications or share one.
/// </summary>
public sealed class GfdiReceiver
{
    /// <summary>A frame (or the junk that couldn't be read as one) longer than this is dropped.</summary>
    public const int MaxFrameBytes = 8192;

    private readonly List<byte> _buffer = new();
    private bool _inFrame;

    /// <summary>A complete frame, or a description of what went wrong with one.</summary>
    public readonly record struct Result(GfdiFrame? Frame, string? Error, byte[] Raw);

    /// <summary>Takes one notification (handle byte first) and returns the frames it completes.</summary>
    public IReadOnlyList<Result> Accept(ReadOnlySpan<byte> notification)
    {
        var results = new List<Result>();
        foreach (byte b in notification.Length > 0 ? notification[1..] : notification)
        {
            if (b != 0)
            {
                if (!_inFrame) continue;   // bytes before the first delimiter belong to nothing
                _buffer.Add(b);
                if (_buffer.Count > MaxFrameBytes) { _buffer.Clear(); _inFrame = false; results.Add(new Result(null, "Frame too long: dropped.", Array.Empty<byte>())); }
                continue;
            }
            // A delimiter ends the frame in progress (if any) and starts the next.
            if (_buffer.Count > 0) results.Add(Complete(_buffer.ToArray()));
            _buffer.Clear();
            _inFrame = true;
        }
        return results;
    }

    private static Result Complete(byte[] cobs)
    {
        try
        {
            var raw = Cobs.Decode(cobs);
            try { return new Result(Gfdi.ParseFrame(raw), null, raw); }
            catch (FormatException ex) { return new Result(null, ex.Message, raw); }
        }
        catch (FormatException ex) { return new Result(null, ex.Message, cobs); }
    }
}
