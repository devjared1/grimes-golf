using System.Collections.Concurrent;
using GrimesGolf.Core;
using GrimesGolf.Core.Course;
using GrimesGolf.Core.Garmin;
using GrimesGolf.Core.Monitors;
using Xunit;

namespace GrimesGolf.Tests;

// Where the fixture bytes come from:
//  [tenover]  divotmaker/10over, src/wire_golden.rs (Apache-2.0 / MIT): protobuf envelopes built from its schema,
//             plus the MultiLink register response in its client test. https://github.com/divotmaker/10over
//  [mholow]   mholow/gsp-r10-adapter, src/bluetooth/device/BaseDevice.cs (MIT): the handshake bytes it writes and the
//             reply it waits for. https://github.com/mholow/gsp-r10-adapter
//  [crc]      the CRC-16/ARC catalogue check value (also in tenover's docs/WIRE.md §4.1).
//  [cobs]     the worked examples in Cheshire and Baker's COBS paper, as listed on Wikipedia's "Consistent Overhead
//             Byte Stuffing" page.
// No real-device capture is in here yet: see docs/garmin-r10-protocol.md for how to make one.

public class GarminFramingTests
{
    [Fact]
    public void Crc16ArcMatchesTheCatalogueCheckValue() =>   // [crc]
        Assert.Equal(0xBB3D, Crc16Arc.Compute("123456789"u8));

    public static TheoryData<string, string> CobsExamples => new()   // [cobs]
    {
        { "00", "0101" },
        { "0000", "010101" },
        { "001100", "01021101" },
        { "11220033", "0311220233" },
        { "11223344", "0511223344" },
        { "11000000", "0211010101" },
    };

    [Theory, MemberData(nameof(CobsExamples))]
    public void CobsEncodesAndDecodesThePublishedExamples(string raw, string encoded)
    {
        Assert.Equal(encoded, Convert.ToHexString(Cobs.Encode(Convert.FromHexString(raw))));
        Assert.Equal(raw, Convert.ToHexString(Cobs.Decode(Convert.FromHexString(encoded))));
    }

    [Fact]
    public void CobsHandlesRunsOf254AndMore()   // [cobs] examples 7-10
    {
        byte[] Range(int from, int to) => Enumerable.Range(from, to - from + 1).Select(i => (byte)i).ToArray();
        var run = Range(1, 0xFE);
        Assert.Equal(new byte[] { 0xFF }.Concat(run), Cobs.Encode(run));
        Assert.Equal(new byte[] { 0x01, 0xFF }.Concat(run), Cobs.Encode(new byte[] { 0 }.Concat(run).ToArray()));
        Assert.Equal(new byte[] { 0xFF }.Concat(run).Concat(new byte[] { 0x02, 0xFF }), Cobs.Encode(Range(1, 0xFF)));
        Assert.Equal(new byte[] { 0xFF }.Concat(Range(2, 0xFF)).Concat(new byte[] { 0x01, 0x01 }),
                     Cobs.Encode(Range(2, 0xFF).Concat(new byte[] { 0 }).ToArray()));
        var random = new Random(7);
        for (int n = 0; n < 600; n += 37)
        {
            var data = new byte[n];
            random.NextBytes(data);
            Assert.Equal(data, Cobs.Decode(Cobs.Encode(data)));
            Assert.DoesNotContain((byte)0, Cobs.Encode(data));
        }
    }

    [Fact]
    public void CobsRejectsZerosAndOverruns()
    {
        Assert.Throws<FormatException>(() => Cobs.Decode(new byte[] { 0x02, 0x00 }));
        Assert.Throws<FormatException>(() => Cobs.Decode(new byte[] { 0x05, 0x11 }));
    }

    [Fact]
    public void RegisterIsTheHandshakeGspR10AdapterWrites() =>   // [mholow] PerformHandShake: header 00, then 000000000000000000010000
        Assert.Equal("00000000000000000000010000", Convert.ToHexString(MultiLink.Register()));

    [Fact]
    public void ReadsTheRegisterResponse()
    {
        // [tenover] client test: transaction 1, service 1, status 0, handle 1, flags 0.
        Assert.True(MultiLink.TryParseRegisterResponse(Convert.FromHexString("00010100000000000000010000010000"), out var status, out var handle));
        Assert.Equal((0, 1), (status, handle));
        // [mholow] the reply it waits for after its header byte: 01, transaction 0, service 01 00, status 00, then the handle.
        Assert.True(MultiLink.TryParseRegisterResponse(Convert.FromHexString("00" + "010000000000000000010000" + "07"), out status, out handle));
        Assert.Equal((0, 7), (status, handle));
        Assert.False(MultiLink.TryParseRegisterResponse(Convert.FromHexString("0001"), out _, out _));
        Assert.Equal(Convert.FromHexString("00010000000000000000010000050000"), MultiLink.RegisterResponse(5));
    }

    [Fact]
    public void BuildsAndParsesGfdiFrames()
    {
        var frame = Gfdi.BuildFrame(Gfdi.Configuration, new byte[] { 1, 2, 0, 3 });
        Assert.Equal(10, BitConverter.ToUInt16(frame, 0));   // the length counts the whole frame, CRC included
        Assert.Equal(Gfdi.Configuration, BitConverter.ToUInt16(frame, 2));
        Assert.Equal(Crc16Arc.Compute(frame.AsSpan(0, 8)), BitConverter.ToUInt16(frame, 8));
        var parsed = Gfdi.ParseFrame(frame);
        Assert.Equal(Gfdi.Configuration, parsed.Type);
        Assert.Equal(new byte[] { 1, 2, 0, 3 }, parsed.Payload);

        frame[5] ^= 0xFF;
        Assert.Throws<FormatException>(() => Gfdi.ParseFrame(frame));
        Assert.Throws<FormatException>(() => Gfdi.ParseFrame(new byte[] { 5, 0, 0x88, 0x13, 0 }));
    }

    [Fact]
    public void ReadsTheCompressedHeader()
    {
        // tenover WIRE.md §5.2: byte 2 = type − 5000, byte 3 = transaction id with the top bit set.
        var f = new byte[] { 8, 0, 44, 0x85, 0xAA, 0xBB, 0, 0 };
        BitConverter.GetBytes(Crc16Arc.Compute(f.AsSpan(0, 6))).CopyTo(f, 6);
        var parsed = Gfdi.ParseFrame(f);
        Assert.Equal(Gfdi.ProtobufResponse, parsed.Type);
        Assert.Equal((byte)5, parsed.TransactionId);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, parsed.Payload);
    }

    [Fact]
    public void AcknowledgementsAreLaidOutAsGspR10AdapterSendsThem()
    {
        // [mholow] AcknowledgeMessage: 8813 (5000), the type acknowledged, then 00 (and, for protobuf, the request id and seven zeros).
        var ack = Gfdi.ParseFrame(Gfdi.BuildAck(Gfdi.DeviceInformation, 0));
        Assert.Equal("A01300", Convert.ToHexString(ack.Payload));
        Assert.Equal((Gfdi.DeviceInformation, (byte)0), ack.Acknowledges);
        var pb = Gfdi.ParseFrame(Gfdi.BuildProtobufAck(Gfdi.ProtobufRequest, 0x0102));
        Assert.Equal("B313" + "00" + "0201" + "00000000000000", Convert.ToHexString(pb.Payload));
    }

    [Fact]
    public void ProtobufMessagesCarryTheFourteenByteHeader()
    {
        // tenover WIRE.md §8.1: request id u16, offset u32, total u32, chunk u32. gsp-r10-adapter skips 16 bytes
        // (these 14 behind the 2-byte type) to reach the protobuf.
        var frame = Gfdi.ParseFrame(Gfdi.BuildProtobuf(Gfdi.ProtobufRequest, 9, R10Proto.WakeUpRequest()));
        Assert.Equal("0900" + "00000000" + "05000000" + "05000000" + "B202021A00", Convert.ToHexString(frame.Payload));
        var (id, proto) = Gfdi.ParseProtobuf(frame.Payload);
        Assert.Equal(9, id);
        Assert.Equal(R10Proto.WakeUpRequest(), proto);
        Assert.Throws<FormatException>(() => Gfdi.ParseProtobuf(new byte[10]));
    }

    [Fact]
    public void ChunksAreTwentyBytesWithTheHandleOnEach()
    {
        var frame = Gfdi.BuildProtobuf(Gfdi.ProtobufRequest, 1, R10Proto.SubscribeRequest());
        var chunks = Gfdi.Chunks(4, frame).ToList();
        Assert.All(chunks, c => { Assert.Equal(4, c[0]); Assert.InRange(c.Length, 2, 20); });
        var wire = chunks.SelectMany(c => c.Skip(1)).ToArray();
        Assert.Equal(0, wire[0]);
        Assert.Equal(0, wire[^1]);
        Assert.Equal(frame, Cobs.Decode(wire.AsSpan(1, wire.Length - 2)));
    }

    [Fact]
    public void ReceiverRebuildsFramesSplitAcrossOrSharingNotifications()
    {
        var a = Gfdi.BuildFrame(Gfdi.DeviceInformation, new byte[] { 1, 0, 2 });
        var b = Gfdi.BuildFrame(Gfdi.Configuration, Enumerable.Range(0, 40).Select(i => (byte)i).ToArray());
        var rx = new GfdiReceiver();
        var got = new List<GfdiFrame>();
        // Split: every chunk of both frames, one at a time.
        foreach (var c in Gfdi.Chunks(1, a).Concat(Gfdi.Chunks(1, b)))
            got.AddRange(rx.Accept(c).Select(r => r.Frame!));
        Assert.Equal(new[] { Gfdi.DeviceInformation, Gfdi.Configuration }, got.Select(f => f.Type));
        // Sharing: both whole frames in one notification.
        var both = new byte[] { 1, 0 }.Concat(Cobs.Encode(a)).Concat(new byte[] { 0, 0 }).Concat(Cobs.Encode(b)).Concat(new byte[] { 0 }).ToArray();
        Assert.Equal(2, new GfdiReceiver().Accept(both).Count(r => r.Frame != null));
        // A damaged frame is reported, and the next one still comes through.
        var bad = (byte[])a.Clone();
        bad[4] ^= 0x55;
        var results = new GfdiReceiver().Accept(new byte[] { 1, 0 }.Concat(Cobs.Encode(bad)).Concat(new byte[] { 0 }).Concat(Cobs.Encode(b)).Concat(new byte[] { 0 }).ToArray());
        Assert.NotNull(results[0].Error);
        Assert.Equal(Gfdi.Configuration, results[1].Frame!.Type);
    }

    [Fact]
    public void IdentityMessagesAreKeptOutOfLogs()
    {
        foreach (ushort t in new ushort[] { 5003, 5004, 5011, 5012, 5024, 5101, 5112 }) Assert.True(Gfdi.CarriesIdentity(t));
        foreach (ushort t in new ushort[] { 5000, 5043, 5044, 5050 }) Assert.False(Gfdi.CarriesIdentity(t));
    }
}

public class GarminProtobufTests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    [Fact]
    public void RequestsMatchTheGoldenBytes()   // [tenover] SUBSCRIBE, WAKEUP, ENVIRONMENT_ALL
    {
        Assert.Equal(Hex("f2 01 06 0a 04 0a 02 08 08"), R10Proto.SubscribeRequest());
        Assert.Equal(Hex("b2 02 02 1a 00"), R10Proto.WakeUpRequest());
        Assert.Equal(Hex("b2 02 1b 5a 19 0d 00 00 a8 41 15 66 66 e6 3e 1d 00 00 f0 42 25 9a 99 99 3f 2d 00 00 c0 40"),
                     R10Proto.ShotConfigRequest(21, 0.45f, 120, 1.2f, 6));
        Assert.Equal(Hex("b2 02 02 0a 00"), R10Proto.StatusRequest());
        Assert.Equal(Hex("b2 02 02 2a 00"), R10Proto.TiltRequest());
    }

    /// <summary>[tenover] SHOT_FULL: a regular shot with every ball, club and swing metric.</summary>
    private static readonly byte[] ShotFull = Hex(
        "f2 01 53 1a 51 08 08 ca 3e 4c 12 4a 08 2a 10 01 1a 1d 0d 00 00 48 41 15 9a 99 99 bf 1d 00 00 86 42 25 00 00 a0 40 2d 00 00 2f 45 30 00 38 01 22" +
        "14 0d 00 00 30 42 15 00 00 c0 3f 1d 00 00 00 c0 25 00 00 60 c0 2a 0f 08 e8 07 10 dc 0b 18 88 0e 20 98 11 28 c4 13");

    /// <summary>[tenover] SHOT_BALL_MEASURED: practice shot, ball only, spin measured off a marked ball.</summary>
    private static readonly byte[] ShotBallMeasured = Hex(
        "f2 01 2c 1a 2a 08 08 ca 3e 25 12 23 08 07 10 00 1a 1d 0d 00 00 f2 41 15 00 00 20 40 1d 00 00 cc 41 25 00 00 20 c1 2d 00 80 bb 45 30 03 38 02");

    /// <summary>[tenover] SHOT_FLIGHT_MODEL: ball speed only.</summary>
    private static readonly byte[] ShotFlightModel = Hex("f2 01 14 1a 12 08 08 ca 3e 0d 12 0b 08 08 1a 07 1d 00 00 48 42 30 01");

    [Fact]
    public void DecodesAFullShot()
    {
        var m = Assert.IsType<R10ShotEvent>(Assert.Single(R10Proto.Decode(ShotFull))).Metrics;
        Assert.Equal(42u, m.ShotId);
        Assert.Equal(1, m.ShotType);
        Assert.Equal(new R10Ball(12.5f, -1.2f, 67f, 5f, 2800f, R10SpinSource.Ratio, 1), m.Ball);
        Assert.Equal(new R10Club(44f, 1.5f, -2f, -3.5f), m.Club);
    }

    [Fact]
    public void AFullShotConvertsToLaunchMonitorInput()
    {
        var m = ((R10ShotEvent)R10Proto.Decode(ShotFull)[0]).Metrics;
        var shot = R10Shots.ToMonitorShot(m, leftHanded: true);
        var i = shot.Input;
        Assert.Equal(R10Shots.Device, i.Device);
        Assert.Equal(67 * Units.MsToMph, i.BallSpeedMph!.Value, 4);
        Assert.Equal(12.5, i.LaunchAngleDegrees!.Value, 4);
        Assert.Equal(-1.2, i.LaunchDirectionDegrees!.Value, 4);   // left, as the R10 says
        Assert.Equal(2800, i.TotalSpinRpm);
        Assert.Equal(-5, i.SpinAxisDegrees);                       // the R10's 5 (tilted left) is −5 here (curving left)
        Assert.Null(i.BackspinRpm);                                // not reported: left for the adapter to derive
        Assert.Null(i.SidespinRpm);
        Assert.Equal(44 * Units.MsToMph, i.ClubSpeedMph!.Value, 4);
        Assert.Equal((-2.0, 1.5, -3.5), (i.ClubPathDegrees!.Value, i.FaceAngleDegrees!.Value, i.AttackAngleDegrees!.Value));
        Assert.Null(i.DynamicLoftDegrees);
        Assert.Null(i.ImpactToeMillimetres);
        Assert.True(i.LeftHanded);
        Assert.Contains(shot.Notes!, n => n.Contains("estimated", StringComparison.OrdinalIgnoreCase));

        var range = RangeShot.FromLaunchMonitor(shot, out var check)!;
        Assert.True(check.IsValid);
        Assert.Equal(-5, range.SpinAxisDeg);
        Assert.Equal(2800, range.SpinRpm);
        Assert.Equal(67 / 44.0, range.Smash!.Value, 4);
        Assert.Equal(ShotSource.Monitor, range.Source);
    }

    [Fact]
    public void AMeasuredSpinShotWithoutClubData()
    {
        var m = ((R10ShotEvent)R10Proto.Decode(ShotBallMeasured)[0]).Metrics;
        Assert.Equal((7u, 0), (m.ShotId!.Value, m.ShotType!.Value));
        Assert.Null(m.Club);
        var shot = R10Shots.ToMonitorShot(m);
        Assert.Equal(10, shot.Input.SpinAxisDegrees);   // the R10's −10 (tilted right)
        Assert.Equal(6000, shot.Input.TotalSpinRpm);
        Assert.Null(shot.Input.ClubSpeedMph);
        Assert.Empty(shot.Notes!);                      // measured spin: nothing to say
    }

    [Fact]
    public void AShotWithoutLaunchCantBeFlown()
    {
        var m = ((R10ShotEvent)R10Proto.Decode(ShotFlightModel)[0]).Metrics;
        Assert.Equal(50f, m.Ball!.BallSpeed);
        Assert.Null(m.Ball.LaunchAngle);
        var shot = R10Shots.ToMonitorShot(m);
        Assert.Null(RangeShot.FromLaunchMonitor(shot, out var check));
        Assert.False(check.IsValid);
    }

    [Fact]
    public void ADrawIsTiltedLeft()
    {
        // [tenover] a_draw_decodes_tilted_left: a captured draw had face 3.9°, path 16.8° and a wire tilt of +22.2.
        var draw = new R10Metrics(5, 1, new R10Ball(14, 3, 60, 22.2f, 3000, R10SpinSource.Ratio, 1), new R10Club(40, 3.9f, 16.8f, -1));
        var decoded = ((R10ShotEvent)R10Proto.Decode(R10Proto.ShotNotification(draw))[0]).Metrics;
        Assert.Equal(draw, decoded);
        var range = RangeShot.FromLaunchMonitor(R10Shots.ToMonitorShot(decoded), out _)!;
        Assert.Equal(-22.2, range.SpinAxisDeg, 4);
        Assert.True(BallFlight.Simulate(range.BallMph, range.LaunchDeg, 0, range.SpinRpm, range.SpinAxisDeg).LateralYards < 0);   // curves left
    }

    [Fact]
    public void UntrackedClubAnglesAreLeftUnmeasured()
    {
        // tenover's docs: on a slow swing the R10 reports face, path and attack angle all exactly 0.
        var m = new R10Metrics(3, 1, new R10Ball(20, 0, 15, 0, 0, R10SpinSource.Ratio, 1), new R10Club(12, 0, 0, 0));
        var shot = R10Shots.ToMonitorShot(m);
        Assert.NotNull(shot.Input.ClubSpeedMph);
        Assert.Null(shot.Input.ClubPathDegrees);
        Assert.Null(shot.Input.FaceAngleDegrees);
        Assert.Null(shot.Input.AttackAngleDegrees);
        Assert.Null(shot.Input.SpinAxisDegrees);         // no spin, so no axis
        Assert.Equal(0, shot.Input.TotalSpinRpm);
        var range = RangeShot.FromLaunchMonitor(shot, out var check)!;
        Assert.Equal((0.0, 0.0), (range.SpinRpm, range.SpinAxisDeg));
        Assert.Contains(check.Warnings, w => w.Contains("Spin axis not measured"));
        // One angle not zero: it was tracked, and a square face is a real 0.
        Assert.Equal(0, R10Shots.ToMonitorShot(m with { Club = new R10Club(12, 0, 0, -4) }).Input.FaceAngleDegrees);
    }

    [Theory]
    [InlineData(0, R10State.Standby), InlineData(1, R10State.InterferenceTest), InlineData(2, R10State.Waiting),
     InlineData(3, R10State.Recording), InlineData(4, R10State.Processing), InlineData(5, R10State.Error)]
    public void DecodesStateNotifications(byte value, R10State state)   // [tenover] state_frame
    {
        var frame = Hex("f2 01 0b 1a 09 08 08 ca 3e 04 0a 02 08").Append(value).ToArray();
        Assert.Equal(new R10StateEvent(state), Assert.Single(R10Proto.Decode(frame)));
        Assert.Equal(frame, R10Proto.StateNotification(state));
    }

    [Fact]
    public void DecodesErrors()   // [tenover] ERROR_NOT_LEVEL, ERROR_OVERHEAT, ERROR_SATURATION
    {
        Assert.Equal(new R10ErrorEvent(R10ErrorCode.PlatformTilted, R10Severity.Fatal, 4.5f, -6.25f),
            Assert.Single(R10Proto.Decode(Hex("f2 01 19 1a 17 08 08 ca 3e 12 1a 10 08 03 10 02 1a 0a 0d 00 00 90 40 15 00 00 c8 c0"))));
        Assert.Equal(new R10ErrorEvent(R10ErrorCode.Overheating, R10Severity.Serious, null, null),
            Assert.Single(R10Proto.Decode(Hex("f2 01 0d 1a 0b 08 08 ca 3e 06 1a 04 08 01 10 01"))));
        Assert.Equal(new R10ErrorEvent(R10ErrorCode.RadarSaturation, R10Severity.Warning, null, null),
            Assert.Single(R10Proto.Decode(Hex("f2 01 0d 1a 0b 08 08 ca 3e 06 1a 04 08 02 10 00"))));
    }

    [Fact]
    public void DecodesReplies()   // [tenover] SUBSCRIBE_OK/FAIL, wake_result_frame, STATE_REPLY, LEVEL_REPLY, ENVIRONMENT_ACCEPTED, LEVEL_CHECK
    {
        Assert.Equal(new R10SubscribeResult(true), Assert.Single(R10Proto.Decode(Hex("f2 01 0a 12 08 0a 06 08 00 12 02 08 08"))));
        Assert.Equal(new R10SubscribeResult(false), Assert.Single(R10Proto.Decode(Hex("f2 01 0a 12 08 0a 06 08 01 12 02 08 08"))));
        for (byte s = 0; s < 3; s++)
            Assert.Equal(new R10WakeResult(s), Assert.Single(R10Proto.Decode(Hex("b2 02 04 22 02 08").Append(s).ToArray())));
        Assert.Equal(new R10StateEvent(R10State.Waiting), Assert.Single(R10Proto.Decode(Hex("b2 02 06 12 04 0a 02 08 02"))));
        Assert.Equal(new R10TiltResult(1f, -2f), Assert.Single(R10Proto.Decode(Hex("b2 02 0e 32 0c 0a 0a 0d 00 00 80 3f 15 00 00 00 c0"))));
        Assert.Equal(new R10ShotConfigResult(true), Assert.Single(R10Proto.Decode(Hex("b2 02 04 62 02 08 01"))));
        Assert.Equal(new R10TiltCalibrationEvent(2, 2), Assert.Single(R10Proto.Decode(Hex("f2 01 0d 1a 0b 08 08 ca 3e 06 22 04 08 02 10 02"))));
        Assert.Empty(R10Proto.Decode(Hex("f2 01 04 1a 02 08 08")));   // a notification with only its topic
        Assert.Empty(R10Proto.Decode(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void RecognisesRequestsForTheFakeDevice()
    {
        Assert.Equal("wake", R10Proto.RequestKind(R10Proto.WakeUpRequest()));
        Assert.Equal("status", R10Proto.RequestKind(R10Proto.StatusRequest()));
        Assert.Equal("tilt", R10Proto.RequestKind(R10Proto.TiltRequest()));
        Assert.Equal("subscribe", R10Proto.RequestKind(R10Proto.SubscribeRequest()));
    }

    [Fact]
    public void RejectsBrokenProtobuf()
    {
        Assert.Throws<FormatException>(() => R10Proto.Decode(Hex("f2 01 09 1a")));   // claims 9 bytes, has 1
        Assert.Throws<FormatException>(() => R10Proto.Decode(Hex("f3")));            // wire type 3 (groups)
        Assert.Throws<FormatException>(() => R10Proto.Decode(Hex("08 ff ff")));      // varint runs off the end
    }
}

public class R10SessionTests
{
    /// <summary>A session over the fake R10, with everything it reported.</summary>
    private sealed class Run
    {
        public readonly FakeR10Device Device = new() { StepMs = 0 };
        public readonly R10Session Session;
        public readonly ConcurrentQueue<string> Log = new(), Raw = new();
        public readonly TaskCompletionSource<MonitorDeviceInfo> Connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<string> Ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly BlockingCollection<MonitorShot> Shots = new();
        public readonly BlockingCollection<MonitorReadiness> Readiness = new();
        public MonitorBattery? Battery;
        public Task Running = Task.CompletedTask;

        public Run(Action<FakeR10Device>? setUp = null, TimeSpan? registerTimeout = null)
        {
            setUp?.Invoke(Device);
            Session = new R10Session(() => Device, Log.Enqueue, Raw.Enqueue)
            {
                RegisterTimeout = registerTimeout ?? TimeSpan.FromSeconds(5),
                KeepAliveInterval = TimeSpan.FromMinutes(5),
            };
            Session.Connected += i => Connected.TrySetResult(i);
            Session.Ended += r => Ended.TrySetResult(r);
            Session.Shot += Shots.Add;
            Session.ReadinessChanged += Readiness.Add;
            Session.Battery += b => Battery = b;
        }

        public Run Start()
        {
            Running = Task.Run(() => Session.RunAsync("I7", leftHanded: false));
            return this;
        }

        public MonitorReadiness NextReadiness() =>
            Readiness.TryTake(out var r, TimeSpan.FromSeconds(5)) ? r : throw new TimeoutException("No readiness: " + string.Join(" | ", Log));

        public void WaitUntilReady()
        {
            while (!NextReadiness().Ready) { }
        }
    }

    private static async Task<T> Soon<T>(Task<T> task) =>
        await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))) == task ? await task : throw new TimeoutException();

    [Fact]
    public async Task ConnectsArmsAndReportsAShot()
    {
        var run = new Run().Start();
        var info = await Soon(run.Connected.Task);
        Assert.Equal("Garmin Approach R10", info.Description);
        Assert.Contains(new MonitorStat("Model", "Approach R10"), info.Stats);
        run.WaitUntilReady();
        Assert.Equal(87, run.Battery?.Level);
        // gsp-r10-adapter's order: wake, status, tilt, then the alert subscription.
        Assert.Equal(new[] { "wake", "status", "tilt", "subscribe" }, run.Device.Requests.Take(4));
        // It acknowledged the handshake messages.
        Assert.Contains(run.Device.Received, f => f.Acknowledges is (Gfdi.DeviceInformation, 0));
        Assert.Contains(run.Device.Received, f => f.Acknowledges is (Gfdi.Configuration, 0));

        run.Device.Hit(new RangeShot(150, 12, -1.5, 2800, 4, 102, null));
        Assert.True(run.Shots.TryTake(out var shot, TimeSpan.FromSeconds(5)));
        var range = RangeShot.FromLaunchMonitor(shot!, out _)!;
        Assert.Equal(150, range.BallMph, 3);
        Assert.Equal(12, range.LaunchDeg, 3);
        Assert.Equal(-1.5, range.DirectionDeg, 3);
        Assert.Equal(2800, range.SpinRpm, 1);
        Assert.Equal(4, range.SpinAxisDeg, 3);   // the same shot back: sign flips both ways cancel
        Assert.Equal(102, range.ClubMph!.Value, 3);
        // Every alert the device sent was acknowledged with its request id (else it sends it again).
        await Task.Delay(100);
        Assert.Contains(run.Device.Received, f => f.Acknowledges is (Gfdi.ProtobufRequest, 0) && f.Payload.Length == 12);
        Assert.True(run.NextReadiness() is { Ready: false });   // measuring...
        run.WaitUntilReady();                                    // ...and armed again by itself

        run.Session.Stop();
        Assert.Equal("Disconnected", await Soon(run.Ended.Task));
    }

    [Fact]
    public async Task IgnoresARepeatedShot()
    {
        var run = new Run().Start();
        await Soon(run.Connected.Task);
        run.WaitUntilReady();
        var shot = R10Proto.ShotNotification(new R10Metrics(77, 1, new R10Ball(12, 0, 60, 2, 3000, R10SpinSource.Ratio, 1), null));
        run.Device.SendAlert(shot);
        run.Device.SendAlert(shot);
        Assert.True(run.Shots.TryTake(out _, TimeSpan.FromSeconds(5)));
        Assert.False(run.Shots.TryTake(out _, TimeSpan.FromMilliseconds(500)));
        Assert.Contains(run.Log, l => l.Contains("Shot 77 repeated"));
        run.Session.Stop();
    }

    [Fact]
    public async Task WakesItWhenItGoesToSleep()
    {
        var run = new Run().Start();
        await Soon(run.Connected.Task);
        run.WaitUntilReady();
        run.Device.Sleep();
        Assert.Equal("Monitor asleep", run.NextReadiness().Text);
        run.WaitUntilReady();
        Assert.Equal(2, run.Device.Requests.Count(r => r == "wake"));
        run.Session.Stop();
    }

    [Fact]
    public async Task ReportsATiltedMonitor()
    {
        var run = new Run().Start();
        await Soon(run.Connected.Task);
        run.WaitUntilReady();
        run.Device.SendAlert(R10Proto.ErrorNotification(R10ErrorCode.PlatformTilted, R10Severity.Serious));
        var r = run.NextReadiness();
        Assert.True(r.Problem);
        Assert.Contains("tilted", r.Text);
        run.Session.Stop();
    }

    [Fact]
    public async Task EndsWhenTheLinkDrops()
    {
        var run = new Run().Start();
        await Soon(run.Connected.Task);
        run.WaitUntilReady();
        run.Device.Drop();
        Assert.Equal("Link dropped", await Soon(run.Ended.Task));

        // A new session on the same device registers again from scratch (as a reconnect does).
        var again = new R10Session(() => run.Device, _ => { }) { KeepAliveInterval = TimeSpan.FromMinutes(5) };
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        again.Connected += _ => connected.TrySetResult();
        _ = Task.Run(() => again.RunAsync("DR", false));
        await Soon(connected.Task.ContinueWith(_ => true));
        again.Stop();
    }

    [Fact]
    public async Task FailsClearlyWhenTheHandshakeGoesUnanswered()
    {
        var run = new Run(d => d.IgnoreRegistration = true, registerTimeout: TimeSpan.FromMilliseconds(300)).Start();
        var reason = await Soon(run.Ended.Task);
        Assert.StartsWith("Connection failed: The R10 didn't answer the handshake", reason);
        Assert.False(run.Connected.Task.IsCompleted);
    }

    [Fact]
    public async Task TheRawLogHasFramesButNoIdentity()
    {
        var run = new Run().Start();
        await Soon(run.Connected.Task);
        run.WaitUntilReady();
        run.Session.Stop();
        await Soon(run.Ended.Task);
        Assert.Contains(run.Raw, l => l.StartsWith("<- 5043: "));
        Assert.Contains(run.Raw, l => l.StartsWith("-> 5043: "));
        Assert.Contains(run.Raw, l => l.StartsWith("<- 5024 (") && l.Contains("not logged"));
        // The fake's made-up unit id (0x00C0FFEE, little-endian) never appears.
        Assert.DoesNotContain(run.Raw, l => l.Contains("EEFFC000"));
    }

    [Fact]
    public void ReadinessFollowsTheState()
    {
        Assert.True(R10Session.Readiness(R10State.Waiting).Ready);
        foreach (var s in new[] { R10State.Standby, R10State.InterferenceTest, R10State.Recording, R10State.Processing, R10State.Error })
            Assert.False(R10Session.Readiness(s).Ready);
        Assert.True(R10Session.Readiness(R10State.Error).Problem);
    }
}
