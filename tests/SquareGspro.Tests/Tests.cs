using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SquareGspro.Core;
using SquareGspro.Core.GsPro;
using SquareGspro.Core.Square;
using SquareGspro.Core.Course;
using Xunit;

namespace SquareGspro.Tests;

public class ParserTests
{
    private static byte[] Ball(short speed, short launch, short dir, short total, short axis, short back, short side)
    {
        var b = new byte[17];
        b[0] = 0x11; b[1] = 0x02; b[2] = 0x37;
        void Put(int o, short v) { b[o] = (byte)(v & 0xFF); b[o + 1] = (byte)((v >> 8) & 0xFF); }
        Put(3, speed); Put(5, launch); Put(7, dir); Put(9, total); Put(11, axis); Put(13, back); Put(15, side);
        return b;
    }

    private static byte[] Club(byte mask, params short[] fields)
    {
        var b = new byte[19];
        b[0] = 0x11; b[1] = 0x07; b[2] = mask;
        for (int i = 0; i < 8; i++)
        {
            short v = i < fields.Length ? fields[i] : (short)-1;
            b[3 + i * 2] = (byte)(v & 0xFF); b[4 + i * 2] = (byte)((v >> 8) & 0xFF);
        }
        return b;
    }

    [Fact]
    public void ParsesBallMetrics()
    {
        var ev = Assert.IsType<BallMetricsEvent>(SquarePacketParser.Parse(Ball(6700, 1250, -150, 2800, -500, 2750, -240)));
        Assert.Equal(67.0, ev.BallSpeedMs);
        Assert.Equal(12.5, ev.LaunchDeg);
        Assert.Equal(-1.5, ev.DirectionDeg);
        Assert.Equal(2800, ev.TotalSpinRpm);
        Assert.Equal(-5.0, ev.SpinAxisDeg);
        Assert.Equal(-240, ev.SideSpinRpm);
    }

    [Fact]
    public void ClubMetrics_SentinelAndMask()
    {
        var full = Assert.IsType<ClubMetricsEvent>(SquarePacketParser.Parse(Club(0xFF, 150, -200, -300, 1800, 0, 0, 4500, 148)));
        Assert.Equal(1.5, full.PathDeg);
        Assert.Equal(-2.0, full.FaceDeg);
        Assert.Equal(45.0, full.ClubSpeedMs);
        Assert.True(full.HasAnyData);

        var untracked = Assert.IsType<ClubMetricsEvent>(SquarePacketParser.Parse(Club(0x00)));
        Assert.False(untracked.HasAnyData);

        // Mask bit set but sentinel value present => still null
        var partial = Assert.IsType<ClubMetricsEvent>(SquarePacketParser.Parse(Club(0xFF, -1, -1, -1, -1, -1, -1, 4500, -1)));
        Assert.Null(partial.PathDeg);
        Assert.Equal(45.0, partial.ClubSpeedMs);
    }

    [Fact]
    public void ZeroLengthIsDisconnect() =>
        Assert.IsType<DisconnectedEvent>(SquarePacketParser.Parse(Array.Empty<byte>()));

    [Fact]
    public void ParsesSensorHeartbeatBattery()
    {
        var s = Assert.IsType<SensorEvent>(SquarePacketParser.Parse(new byte[] { 0x11, 0x01, 5, 0x01, 0x01, 0, 0, 0, 0 }));
        Assert.True(s.BallReady); Assert.True(s.BallDetected);
        var h = Assert.IsType<HeartbeatAckEvent>(SquarePacketParser.Parse(new byte[] { 0x11, 0x03, 9, 0x04, 0, 0, 0, 0, 0 }));
        Assert.Equal(SquareDeviceState.Ready, h.State);
        var b = Assert.IsType<BatteryEvent>(SquarePacketParser.Parse(new byte[] { 0x91, 87, 2 }));
        Assert.Equal(87, b.Level);
    }

    [Fact]
    public void CommandsAre9BytesWithSequence()
    {
        var c = new SquareCommandBuilder();
        var a = c.Heartbeat(); var b = c.DetectBall(true, true);
        Assert.Equal(9, a.Length);
        Assert.Equal(new byte[] { 0x00, 0x83, 0x00, 0, 0, 0, 0, 0, 0 }, a);
        Assert.Equal(new byte[] { 0x00, 0x81, 0x01, 0x01, 0x11, 0, 0, 0, 0 }, b);
        Assert.True(SquareClubMap.TryGet("PT", out var pt));
        Assert.Equal(new byte[] { 0x00, 0x82, 0x02, 0x01, 0x03, 0x01, 0, 0, 0 }, c.ClubSelect(pt, leftHanded: true));
    }
}

public class MapperTests
{
    [Fact]
    public void ConvertsUnitsAndNegatesSpinAxis()
    {
        var shot = ShotMapper.Map(new ConnectorOptions(), 3,
            new BallMetricsEvent(67.056, 12.5, 1.2, 2800, -5.0, 2790, -240),
            new ClubMetricsEvent(0xFF, 1.5, -2, -3, 18, null, null, 44.7, 1.5));
        Assert.Equal(3, shot.ShotNumber);
        Assert.Equal(150.0, shot.BallData.Speed, 1);          // 67.056 m/s ≈ 150 mph
        Assert.Equal(5.0, shot.BallData.SpinAxis);            // sign flipped
        Assert.Equal(240, shot.BallData.SideSpin);
        Assert.Equal(1.2, shot.BallData.HLA);
        Assert.True(shot.ShotDataOptions.ContainsClubData);
        Assert.Equal(100.0, shot.ClubData.Speed, 0);
        Assert.Equal(-3, shot.ClubData.AngleOfAttack);
    }

    [Fact]
    public void NoClubData_FlagFalse()
    {
        var shot = ShotMapper.Map(new ConnectorOptions(), 1, new BallMetricsEvent(30, 5, 0, 100, 0, 100, 0), null);
        Assert.False(shot.ShotDataOptions.ContainsClubData);
        Assert.True(shot.ShotDataOptions.ContainsBallData);
    }
}

public class FramerTests
{
    [Fact]
    public void HandlesSplitConcatenatedAndBracesInStrings()
    {
        var f = new JsonObjectFramer();
        var all = new List<string>();
        all.AddRange(f.Feed("{\"Code\":200,\"Message\":\"a } b\"}{\"Code\":2"));
        all.AddRange(f.Feed("01,\"Player\":{\"Handed\":\"RH\",\"Club\":\"DR\"}}\n"));
        Assert.Equal(2, all.Count);
        Assert.Contains("a } b", all[0]);
        Assert.Equal("DR", JsonSerializer.Deserialize<GsProResponse>(all[1])!.Player!.Club);
    }
}

// ---- End-to-end with a fake Square link and a mock GSPro TCP server --------

internal sealed class FakeLink : ISquareLink
{
    public event Action<byte[]>? DataReceived;
    public event Action? Disconnected;
    public List<byte[]> Written { get; } = new();
    public Func<byte[], Task>? OnWrite;

    public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;
    public Task DisconnectAsync() => Task.CompletedTask;
    public async Task WriteAsync(byte[] command, CancellationToken ct)
    {
        lock (Written) Written.Add(command);
        if (OnWrite != null) await OnWrite(command);
    }
    // Emit twice, like the real device.
    public void Emit(byte[] data) { DataReceived?.Invoke(data); DataReceived?.Invoke((byte[])data.Clone()); }
    public void Drop() => Disconnected?.Invoke();
    public bool Wrote(byte type) { lock (Written) return Written.Any(w => w[1] == type); }
}

internal sealed class MockGsPro : IAsyncDisposable
{
    private readonly TcpListener _l = new(IPAddress.Loopback, 0);
    private TcpClient? _client;
    private readonly StringBuilder _buf = new();
    public int Port => ((IPEndPoint)_l.LocalEndpoint).Port;
    public List<JsonDocument> Received { get; } = new();

    public MockGsPro() { _l.Start(); _ = AcceptAsync(); }

    private async Task AcceptAsync()
    {
        _client = await _l.AcceptTcpClientAsync();
        var s = _client.GetStream();
        var framer = new JsonObjectFramer();
        var buf = new byte[4096];
        try
        {
            while (true)
            {
                int n = await s.ReadAsync(buf);
                if (n == 0) return;
                foreach (var j in framer.Feed(Encoding.UTF8.GetString(buf, 0, n)))
                    lock (Received) Received.Add(JsonDocument.Parse(j));
            }
        }
        catch { }
    }

    public async Task SendAsync(string json)
    {
        while (_client == null) await Task.Delay(10);
        await _client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(json));
    }

    public List<JsonDocument> Shots()
    {
        lock (Received)
            return Received.Where(d => d.RootElement.GetProperty("ShotDataOptions").GetProperty("ContainsBallData").GetBoolean()).ToList();
    }

    public ValueTask DisposeAsync() { _client?.Dispose(); _l.Stop(); return ValueTask.CompletedTask; }
}

public class EndToEndTests
{
    private static async Task<bool> WaitFor(Func<bool> cond, int ms = 5000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { if (cond()) return true; await Task.Delay(20); }
        return cond();
    }

    private static byte[] BallPacket() => new byte[]
    {
        0x11, 0x02, 0x37,
        0x58, 0x1A,   // 6744 -> 67.44 m/s
        0xE2, 0x04,   // 1250 -> 12.50 deg
        0x6A, 0x00,   // 106 -> 1.06 deg
        0xF0, 0x0A,   // 2800 rpm
        0x0C, 0xFE,   // -500 -> -5.00 deg
        0xDE, 0x0A,   // 2782
        0x10, 0xFF,   // -240
    };

    private static byte[] ClubPacket()
    {
        var b = new byte[19]; b[0] = 0x11; b[1] = 0x07; b[2] = 0xFF;
        void Put(int o, short v) { b[o] = (byte)(v & 0xFF); b[o + 1] = (byte)((v >> 8) & 0xFF); }
        Put(3, 150); Put(5, -200); Put(7, -300); Put(9, 1800); Put(11, 0); Put(13, 0); Put(15, 4500); Put(17, 148);
        return b;
    }

    [Fact]
    public async Task ShotFlowsToGsPro_WithClubData_AndDedupe()
    {
        await using var gs = new MockGsPro();
        var log = new List<string>();
        var opts = new ConnectorOptions { GsProPort = gs.Port, HeartbeatSeconds = 60 };
        var client = new GsProClient("127.0.0.1", gs.Port, m => { lock (log) log.Add(m); });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _ = client.RunAsync(cts.Token);
        Assert.True(await WaitFor(() => client.IsConnected));

        var bridge = new ShotBridge(opts, client, m => { lock (log) log.Add(m); });
        var link = new FakeLink();
        // Answer club-metrics requests like the real device would.
        link.OnWrite = c => { if (c[1] == 0x87) link.Emit(ClubPacket()); return Task.CompletedTask; };

        var session = bridge.RunSessionAsync(link, cts.Token);
        Assert.True(await WaitFor(() => link.Wrote(0x81)));

        // GSPro switches to the putter, left-handed -> ClubSelect (0x82) with putter code and LH flag
        await gs.SendAsync("{\"Code\":201,\"Message\":\"GSPro Player Information\",\"Player\":{\"Handed\":\"LH\",\"Club\":\"PT\"}}");
        Assert.True(await WaitFor(() => { lock (link.Written) return link.Written.Any(w => w[1] == 0x82 && w[3] == 0x01 && w[4] == 0x03 && w[5] == 0x01); }));

        link.Emit(new byte[] { 0x11, 0x01, 1, 0x01, 0x01, 0, 0, 0, 0 });
        link.Emit(BallPacket());

        Assert.True(await WaitFor(() => gs.Shots().Count >= 1));
        await Task.Delay(300); // make sure the duplicate notification did not yield a 2nd shot
        var shots = gs.Shots();
        Assert.Single(shots);

        var root = shots[0].RootElement;
        Assert.Equal(1, root.GetProperty("ShotNumber").GetInt32());
        Assert.Equal("1", root.GetProperty("APIversion").GetString());
        Assert.True(root.GetProperty("ShotDataOptions").GetProperty("ContainsClubData").GetBoolean());
        var ball = root.GetProperty("BallData");
        Assert.Equal(150.86, ball.GetProperty("Speed").GetDouble(), 1);
        Assert.Equal(12.5, ball.GetProperty("VLA").GetDouble());
        Assert.Equal(1.06, ball.GetProperty("HLA").GetDouble());
        Assert.Equal(5.0, ball.GetProperty("SpinAxis").GetDouble());
        Assert.Equal(2800, ball.GetProperty("TotalSpin").GetDouble());
        Assert.Equal(100.66, root.GetProperty("ClubData").GetProperty("Speed").GetDouble(), 1);

        cts.Cancel();
        try { await session; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task ClubTimeout_SendsBallOnly_AndSessionEndsOnDisconnect()
    {
        await using var gs = new MockGsPro();
        var opts = new ConnectorOptions { GsProPort = gs.Port, HeartbeatSeconds = 60, ClubMetricsTimeoutMs = 150 };
        var client = new GsProClient("127.0.0.1", gs.Port, _ => { });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _ = client.RunAsync(cts.Token);
        Assert.True(await WaitFor(() => client.IsConnected));

        var bridge = new ShotBridge(opts, client, _ => { });
        var link = new FakeLink(); // never answers 0x87
        var session = bridge.RunSessionAsync(link, cts.Token);
        Assert.True(await WaitFor(() => link.Wrote(0x81)));

        link.Emit(BallPacket());
        Assert.True(await WaitFor(() => gs.Shots().Count == 1));
        Assert.False(gs.Shots()[0].RootElement.GetProperty("ShotDataOptions").GetProperty("ContainsClubData").GetBoolean());

        // A late/stale club packet must not create another shot
        link.Emit(ClubPacket());
        await Task.Delay(300);
        Assert.Single(gs.Shots());

        link.Drop();
        await session.WaitAsync(TimeSpan.FromSeconds(5)); // session returns cleanly on link drop
        cts.Cancel();
    }
}

public class DeviceInfoTests
{
    [Fact]
    public void ParsesClockTick() =>
        Assert.Equal(new ClockEvent(0x0CA0), SquarePacketParser.Parse(Convert.FromHexString("710000000000000CA0")));

    [Fact]
    public void ParsesFirmwareJson()
    {
        var info = new SquareDeviceInfo("4c00145001d38872050", "1.5",
            SquareDeviceInfo.DecodeText(Encoding.UTF8.GetBytes("{\"launcher\":\"1.0.0\",\"mmi\":\"1.2.0\",\"lm\":\"1.10.29\"}\0")));
        Assert.Equal("1.10.29", info.FirmwareComponents["lm"]);
        Assert.Equal(3, info.FirmwareComponents.Count);
        Assert.Empty(new SquareDeviceInfo(null, null, "not json").FirmwareComponents);
    }
}

public class BallFlightTests
{
    [Theory]
    // mph, VLA, spin, expected carry range (yd) — typical tour-ish numbers
    [InlineData(167, 10.9, 2686, 255, 290)]   // driver
    [InlineData(120, 16.3, 7097, 150, 180)]   // 7-iron
    [InlineData(102, 24.2, 9304, 115, 145)]   // PW
    public void CarryIsRealistic(double mph, double vla, double spin, double min, double max)
    {
        var r = BallFlight.Simulate(mph, vla, 0, spin, 0);
        Assert.InRange(r.CarryYards, min, max);
        Assert.True(r.TotalYards >= r.CarryYards - 3);   // wedges can check back a little
        Assert.Equal(0, r.LateralYards, 3);
    }

    [Fact]
    public void PositiveSpinAxisCurvesRight()
    {
        Assert.True(BallFlight.Simulate(150, 12, 0, 3000, 10).LateralYards > 5);
        Assert.True(BallFlight.Simulate(150, 12, 0, 3000, -10).LateralYards < -5);
    }
}

public class RangeShotTests
{
    [Fact]
    public void FromSquareConvertsUnitsAndFlipsSpinAxis()
    {
        var ball = new BallMetricsEvent(50, 15, 2, 6000, -8, 5900, -800);
        var club = new ClubMetricsEvent(0xff, 1, 2, -3, 20, null, null, 37.5, null);
        var s = RangeShot.FromSquare(ball, club);
        Assert.Equal(50 * ShotMapper.MsToMph, s.BallMph, 6);
        Assert.Equal(8, s.SpinAxisDeg);           // device -8 (curves right) -> +8
        Assert.Equal(2, s.DirectionDeg);
        Assert.Equal(50 / 37.5, s.Smash!.Value, 6); // derived when the device sends none
    }

    [Fact]
    public void DemoShotsStayNearTheClubPreset()
    {
        var rng = new Random(1);
        var dr = ClubPresets.All[ClubPresets.IndexOf("DR")];
        for (int i = 0; i < 20; i++)
        {
            var s = RangeShot.Demo(dr, rng);
            Assert.True(s.IsDemo);
            Assert.InRange(s.Fly().CarryYards, 220, 300);
        }
    }

    [Fact]
    public void SideFormatting()
    {
        Assert.Equal("R 2.5", RangeShot.Side(2.5));
        Assert.Equal("L 3", RangeShot.Side(-3.2, "0"));
        Assert.Equal("0.0", RangeShot.Side(0.01));
    }
}

public class ScreenShotTests
{
    private static readonly ClubPreset SevenIron = ClubPresets.Find("I7")!, Pw = ClubPresets.Find("PW")!;

    [Fact]
    public void AFullStraightMediumSpinSwingIsTheClubsStockShot()
    {
        var s = Caddie.SwingShot(SevenIron, new Swing(1));
        Assert.Equal(SevenIron.BallMph, s.BallMph, 9);
        Assert.Equal(SevenIron.LaunchDeg, s.LaunchDeg, 9);
        Assert.Equal(SevenIron.SpinRpm, s.SpinRpm, 9);
        Assert.Equal(0, s.DirectionDeg, 9);
        Assert.Equal(0, s.SpinAxisDeg, 9);
    }

    [Fact]
    public void PowerSetsTheShareOfTheClubsCarry()
    {
        double full = Caddie.Carry("PW") * Units.MetresToYards;
        foreach (double p in new[] { 0.6, 0.8 })
            Assert.InRange(Caddie.SwingShot(Pw, new Swing(p)).Fly().CarryYards, full * p * 0.93, full * p * 1.07);
        Assert.True(Caddie.SwingShot(Pw, new Swing(0.5)).SpinRpm < Pw.SpinRpm, "a partial swing spins less");
        Assert.Equal(0.5, Caddie.PowerFor("PW", Caddie.Carry("PW") / 2), 6);
        Assert.Equal(1, Caddie.PowerFor("PW", Caddie.Carry("PW") * 3));
    }

    [Fact]
    public void ADrawStartsRightAndCurvesLeftAndAFadeTheOpposite()
    {
        var draw = Caddie.SwingShot(SevenIron, new Swing(1, ShotShape.Draw));
        var fade = Caddie.SwingShot(SevenIron, new Swing(1, ShotShape.Fade));
        Assert.True(draw.DirectionDeg > 0 && draw.SpinAxisDeg < 0, "draw (right-hander): starts right, curves left");
        Assert.True(fade.DirectionDeg < 0 && fade.SpinAxisDeg > 0, "fade: starts left, curves right");
        Assert.True(Caddie.SwingShot(SevenIron, new Swing(1, ShotShape.Draw), leftHanded: true).SpinAxisDeg > 0, "a left-hander's draw curves right");
        // Both curve back to finish near the aim line (within a few yards of it).
        foreach (var s in new[] { draw, fade })
            Assert.InRange(s.Fly().LateralYards, -6, 6);
        // Smaller swings curve less.
        Assert.True(Math.Abs(Caddie.SwingShot(SevenIron, new Swing(0.5, ShotShape.Draw)).SpinAxisDeg) < Math.Abs(draw.SpinAxisDeg));
    }

    [Fact]
    public void SpinLevelMovesLaunchAndSpin()
    {
        var low = Caddie.SwingShot(Pw, new Swing(1, Spin: SpinLevel.Low));
        var high = Caddie.SwingShot(Pw, new Swing(1, Spin: SpinLevel.High));
        Assert.True(low.LaunchDeg < Pw.LaunchDeg && low.SpinRpm < Pw.SpinRpm, "low: lower, less spin");
        Assert.True(high.LaunchDeg > Pw.LaunchDeg && high.SpinRpm > Pw.SpinRpm, "high: higher, more spin");
        Assert.Equal(Pw.BallMph, high.BallMph, 9);
    }

    [Fact]
    public void PuttPowerIsTheShareOfTheMetersRoll()
    {
        var putt = Caddie.SwingPutt(new Swing(0.5), fullScaleMetres: 6, Lie.Green);
        Assert.Equal(3, Caddie.PuttRollMetres(putt.BallMph / ShotMapper.MsToMph, Lie.Green), 6);
        Assert.Equal(0, putt.DirectionDeg, 9);
        // The scale is a round number of feet with the putt at about 70%.
        double scale = Caddie.PuttScaleMetres(4);
        Assert.InRange(4 / scale, 0.55, 0.72);
        Assert.Equal(0, Math.Round(scale * Units.MetresToYards * 3) % 5);
    }
}

public class CourseTests
{
    private static Hole Hole(int n) => TestCourse.Holes[n - 1];

    [Fact]
    public void TestHolesAreLaidOutSensibly()
    {
        Assert.Equal(new[] { 4, 3, 5 }, TestCourse.Holes.Select(h => h.Par));
        foreach (var h in TestCourse.Holes)
        {
            Assert.Equal(Lie.Tee, h.LieAt(h.Tee));
            Assert.Equal(Lie.Green, h.LieAt(h.Pin));
            Assert.Equal(Lie.OutOfBounds, h.LieAt(new P2(h.Bounds.Max.X + 5, 0)));
        }
        Assert.InRange(Hole(1).LengthYards, 340, 400);
        Assert.InRange(Hole(2).LengthYards, 140, 170);
        Assert.InRange(Hole(3).LengthYards, 470, 540);
    }

    [Fact]
    public void LiesFollowTheShapes()
    {
        var h2 = Hole(2);
        Assert.Equal(Lie.Water, h2.LieAt(new P2(85 * Units.YardsToMetres, 0)));       // middle of the pond
        Assert.Equal(Lie.Bunker, h2.LieAt(new P2(152 * Units.YardsToMetres, -15 * Units.YardsToMetres)));
        Assert.Equal(Lie.DeepRough, h2.LieAt(new P2(20, 30)));   // well away from tee, fairway and green
        Assert.Equal(Lie.Fairway, Hole(1).LieAt(new P2(150, 0)));
        Assert.Equal(Lie.Water, Hole(3).LieAt(new P2(365 * Units.YardsToMetres, -100 * Units.YardsToMetres))); // creek
    }

    [Fact]
    public void ShortShotIntoThePondCostsAPenaltyAndReplays()
    {
        var round = new HoleRound(TestCourse.Holes);
        round.StartHole(1);
        var topped = new RangeShot(70, 12, 0, 4000, 0, null, null);  // lands ~90 yd: in the pond
        var o = round.Play(topped, putt: false, round.DefaultAimHeading());
        Assert.Equal(Lie.Water, o.RestLie);
        Assert.Equal(1, o.Penalty);
        Assert.Equal(2, round.Strokes);
        Assert.Equal(round.Hole.Tee, round.Ball);   // stroke and distance
        Assert.False(round.Holed);
    }

    [Fact]
    public void WellStruckIronFindsTheParThreeGreen()
    {
        var round = new HoleRound(TestCourse.Holes);
        round.StartHole(1);
        var o = round.Play(new RangeShot(112, 18, 0, 7600, 0, null, null), false, round.DefaultAimHeading());
        Assert.Equal(Lie.Green, o.RestLie);
        Assert.Equal(1, round.Strokes);
        Assert.Equal(o.Rest, round.Ball);
        Assert.Equal("PT", Caddie.Suggest(round.DistanceToPin, round.Lie));
    }

    [Fact]
    public void PuttsHoleAtTheRightPaceAndTapInsCount()
    {
        var round = new HoleRound(TestCourse.Holes);
        round.StartHole(1);
        round.Play(new RangeShot(112, 18, 0, 7600, 0, null, null), false, round.DefaultAimHeading());
        double d = round.DistanceToPin;

        // Firm enough to reach, slow enough to drop: v = sqrt(2 a (d + 0.2)).
        double mph = Math.Sqrt(2 * 0.55 * (d + 0.2)) * ShotMapper.MsToMph;
        var o = round.Play(new RangeShot(mph, 1, 0, 0, 0, null, null), putt: true, round.DefaultAimHeading());
        Assert.True(o.Holed, o.Message);
        Assert.Equal(2, round.Strokes);
        Assert.Equal(2, round.Scores[1]);
    }

    [Fact]
    public void BlastedPuttRacesPastAndShortPuttIsTappedIn()
    {
        var round = new HoleRound(TestCourse.Holes);
        round.StartHole(1);
        round.Play(new RangeShot(112, 18, 0, 7600, 0, null, null), false, round.DefaultAimHeading());
        double d = round.DistanceToPin;
        var fast = round.Play(new RangeShot(Math.Sqrt(2 * 0.55 * (d + 4)) * ShotMapper.MsToMph, 1, 0, 0, 0, null, null), true, round.DefaultAimHeading());
        Assert.False(fast.Holed);
        Assert.InRange(round.DistanceToPin, 1.5, 6);   // skips over the cup, which takes some pace off
        // A putt that stops 0.3 m short is conceded.
        double left = round.DistanceToPin;
        var tap = round.Play(new RangeShot(Math.Sqrt(2 * 0.55 * (left - 0.3)) * ShotMapper.MsToMph, 1, 0, 0, 0, null, null), true, round.DefaultAimHeading());
        Assert.True(tap.Holed);
        Assert.Equal(4, round.Strokes);   // tee shot, two putts, tap-in
    }

    /// <summary>On the par-3 green, putts toward the pin missing the centre by <paramref name="miss"/> m, arriving at <paramref name="arrive"/> m/s.</summary>
    private static (HoleRound Round, ShotOutcome Putt) PuttAtCup(double miss, double arrive)
    {
        var round = new HoleRound(TestCourse.Holes);
        round.StartHole(1);
        round.Play(new RangeShot(112, 18, 0, 7600, 0, null, null), false, round.DefaultAimHeading());
        double d = round.DistanceToPin;
        double mph = Math.Sqrt(arrive * arrive + 2 * 0.55 * d) * ShotMapper.MsToMph;
        double dir = Math.Asin(miss / d) * 180 / Math.PI;
        return (round, round.Play(new RangeShot(mph, 1, dir, 0, 0, null, null), true, round.DefaultAimHeading()));
    }

    [Fact]
    public void BallDropsToTheBottomOfTheCup()
    {
        var (_, o) = PuttAtCup(0.02, 0.9);
        Assert.True(o.Holed, o.Message);
        Assert.Equal(-HoleRound.CupDepth, o.Flight.Points[^1].Y, 6);
        Assert.Equal(0, new P2(o.Flight.Points[^1].X, o.Flight.Points[^1].Z).DistanceTo(o.Rest), 6);
    }

    [Fact]
    public void FirmPuttCatchingTheEdgeLipsOutAndTurns()
    {
        var (round, o) = PuttAtCup(0.06, 1.3);   // 1.3 m/s is too quick to drop that far off centre
        Assert.False(o.Holed, o.Message);
        Assert.Contains(o.Flight.Points, p => p.Y < 0);          // dips onto the rim
        // A straight miss would pass 6 cm from the pin and roll ~1.5 m on; the lip-out turns it and kills its pace.
        var start = o.Start;
        var straightEnd = start + new P2(Math.Cos(round.DefaultAimHeading()), Math.Sin(round.DefaultAimHeading())) * (start.DistanceTo(round.Hole.Pin) + 1.5);
        Assert.True(o.Rest.DistanceTo(straightEnd) > 0.3);
        Assert.InRange(round.DistanceToPin, 0.1, 1.2);
        Assert.Equal(Lie.Green, round.Lie);
    }

    [Fact]
    public void SlowPuttDiesInTheSideDoor()
    {
        var (_, o) = PuttAtCup(0.065, 0.4);
        Assert.True(o.Holed, o.Message);
    }

    [Fact]
    public void PlaceRotatesOntoTheAimLine()
    {
        var pts = new[] { new FlightPoint(0, 0, 0, 0), new FlightPoint(1, 10, 0, 0) };
        var placed = HoleRound.Place(pts, new P2(5, 5), Math.PI / 2); // aim straight right
        Assert.Equal(5, placed[1].X, 6);
        Assert.Equal(15, placed[1].Z, 6);
    }

    [Fact]
    public void CaddieSuggestsSensibleClubs()
    {
        Assert.Equal("DR", Caddie.Suggest(240, Lie.Tee));
        foreach (var h in TestCourse.Holes.Where(h => h.Par >= 4))   // tee shots on the par 4 and 5
            Assert.Equal("DR", Caddie.Suggest(h.Tee.DistanceTo(h.TeeAim), Lie.Tee));
        Assert.StartsWith("I", Caddie.Suggest(TestCourse.Holes[1].Tee.DistanceTo(TestCourse.Holes[1].Pin), Lie.Tee));
        Assert.NotEqual("DR", Caddie.Suggest(240, Lie.Fairway));
        Assert.Equal("PT", Caddie.Suggest(8, Lie.Green));
        var seven = Caddie.Suggest(150, Lie.Fairway);
        Assert.Contains(seven, new[] { "I6", "I7", "I8" });
    }

    [Fact]
    public void ScoreNames()
    {
        Assert.Equal("Birdie", HoleRound.ScoreName(3, 4));
        Assert.Equal("Hole in one", HoleRound.ScoreName(1, 3));
        Assert.Equal("+3", HoleRound.ScoreName(7, 4));
    }

    [Fact]
    public void PolygonContainsConcaveShapes()
    {
        // An L shape: the notch at the top right is outside.
        var l = new Polygon(new P2[] { new(0, 0), new(10, 0), new(10, 4), new(4, 4), new(4, 10), new(0, 10) });
        Assert.True(l.Contains(new P2(2, 8)));
        Assert.True(l.Contains(new P2(8, 2)));
        Assert.False(l.Contains(new P2(8, 8)));
        Assert.Equal(2, l.DistanceTo(new P2(12, 2)), 6);
        Assert.Equal(-1, l.DistanceTo(new P2(2, 9)), 6);
        Assert.Equal(64, l.Area, 6);
        Assert.True(l.IsNear(new P2(12, 2), 2.5));
        Assert.False(l.IsNear(new P2(8, 8), 3.5));
    }
}

public class LieTests
{
    private static Hole Hole1 => TestCourse.Holes[0];

    [Fact]
    public void FringeFirstCutAndDeepRough()
    {
        var h = Hole1;   // fairway 35 yd wide along Z = 0; green at (362, 10) yd, 17 × 14 yd
        Assert.Equal(Lie.Fairway, h.LieAt(new P2(150, 0)));
        Assert.Equal(Lie.Rough, h.LieAt(new P2(150, 16 + 4)));       // just off the fairway (half-width 16 m)
        Assert.Equal(Lie.DeepRough, h.LieAt(new P2(150, 16 + 15)));
        var greenEdge = (362 + 17) * Units.YardsToMetres;
        Assert.Equal(Lie.Green, h.LieAt(new P2(greenEdge - 0.5, 10 * Units.YardsToMetres)));
        Assert.Equal(Lie.Fringe, h.LieAt(new P2(greenEdge + 1, 10 * Units.YardsToMetres)));
        Assert.Equal(Lie.Rough, h.LieAt(new P2(greenEdge + 5, 10 * Units.YardsToMetres)));
    }

    [Fact]
    public void StanceComesFromTheGround()
    {
        var area = new Bounds(new P2(-100, -150), new P2(450, 150));
        var uphill = new HoleRound(TestCourse.Holes, new Terrain(area, 1.0, p => p.X * 0.05));
        var s = uphill.StanceAt(new P2(150, 0), 0, leftHanded: false);
        Assert.Equal(Math.Atan(0.05) * 180 / Math.PI, s.UphillDegrees, 1);
        Assert.Equal(0, s.BallAboveFeetDegrees, 1);
        Assert.Equal(-s.UphillDegrees, uphill.StanceAt(new P2(150, 0), Math.PI, false).UphillDegrees, 6);   // facing back down
        Assert.True(uphill.StanceAt(TestCourse.Holes[0].Tee, 0, false).IsLevel, "tees are built level");

        var sidehill = new HoleRound(TestCourse.Holes, new Terrain(area, 1.0, p => p.Z * 0.05));   // rises to the right
        Assert.True(sidehill.StanceAt(new P2(150, 0), 0, leftHanded: false).BallAboveFeetDegrees > 2, "right-hander stands left of the ball");
        Assert.True(sidehill.StanceAt(new P2(150, 0), 0, leftHanded: true).BallAboveFeetDegrees < -2);
        Assert.Equal(SquareGspro.Physics.LieKind.Fairway, sidehill.StanceAt(new P2(150, 0), 0, false).Kind);
    }

    [Fact]
    public void BunkersAreDugInWithALipThatStaysOffTheGreen()
    {
        var area = new Bounds(new P2(-60, -60), new P2(60, 60));
        var terrain = new Terrain(area, 1.0, p => p.X * 0.02);
        var bunker = new Polygon(new[] { new P2(-10, -5), new P2(10, -5), new P2(10, 5), new P2(-10, 5) });
        var green = new Polygon(new[] { new P2(-10, 7), new P2(10, 7), new P2(10, 30), new P2(-10, 30) });
        double greenBefore = terrain.HeightAt(new P2(0, 8));
        terrain.Sculpt(new[] { green }, new[] { bunker }, Array.Empty<Shape>());

        Assert.InRange(terrain.HeightAt(new P2(0, 0)), -1.1, -0.5);                    // floor below the old ground (0 at X = 0)
        Assert.True(terrain.HeightAt(new P2(0, -5)) > 0.05, "a lip stands up at the edge");
        Assert.Equal(-14 * 0.02, terrain.HeightAt(new P2(-14, -20)), 6);                // ground well away is untouched
        Assert.Equal(greenBefore, terrain.HeightAt(new P2(0, 8)), 2);                   // the green beside it isn't
    }

    [Fact]
    public void PondsLieLevelBelowTheirLowestBank()
    {
        var area = new Bounds(new P2(-60, -60), new P2(60, 60));
        var terrain = new Terrain(area, 1.0, p => p.X * 0.03);   // 0.6 m fall across the pond
        var pond = new Polygon(new[] { new P2(-10, -10), new P2(10, -10), new P2(10, 10), new P2(-10, 10) });
        var creek = new Strip(new[] { new P2(-40, 30), new P2(40, 30) }, 3);
        terrain.Sculpt(Array.Empty<Shape>(), Array.Empty<Shape>(), new Shape[] { pond, creek });

        var level = terrain.WaterLevel(pond);
        Assert.NotNull(level);
        Assert.True(level < -10 * 0.03, "below the lowest bank");
        foreach (var p in new[] { new P2(-8, 0), new P2(0, 0), new P2(8, 5) })
            Assert.True(terrain.HeightAt(p) < level, "the bed is under the water everywhere");
        Assert.Null(terrain.WaterLevel(creek));   // creeks follow the ground
        Assert.Equal(20 * 0.03, terrain.HeightAt(new P2(20, 0)), 6);
    }

    [Fact]
    public void ShotsFromTheRoughAreAdjustedAndTheTeeShotIsNot()
    {
        var round = new HoleRound(TestCourse.Holes);
        var tee = round.Play(new RangeShot(160, 12, 9, 2700, 0, null, null), false, 0, "DR");   // pushed well right
        Assert.False(tee.LieEffect!.Changed);
        Assert.Contains(round.Lie, new[] { Lie.Rough, Lie.DeepRough });
        var next = round.Play(new RangeShot(118, 16.5, 0, 7000, 0, null, null), false, round.DefaultAimHeading(), "I7");
        Assert.True(next.LieEffect!.Changed);
        Assert.True(next.LieEffect.SpinFactor < 1);
        Assert.Contains("spin", next.LieEffect.Summary);
        Assert.Null(round.Play(new RangeShot(5, 1, 0, 0, 0, null, null), true, 0).LieEffect);   // putts aren't adjusted
    }

    [Fact]
    public void CaddieClubsUpFromTheRough()
    {
        var deep = new SquareGspro.Physics.LieCondition(SquareGspro.Physics.LieKind.DeepRough);
        Assert.True(Caddie.CarryFrom("I7", deep) < Caddie.Carry("I7") - 10);
        int fromFairway = ClubPresets.IndexOf(Caddie.Suggest(140, Lie.Fairway));
        int fromDeep = ClubPresets.IndexOf(Caddie.Suggest(140, Lie.DeepRough, deep));
        Assert.True(fromDeep < fromFairway, "a longer club (earlier in the list) from deep rough");
        Assert.Equal("PT", Caddie.Suggest(10, Lie.Fringe));
    }
}

/// <summary>
/// Physics tuning. These build settings with <see cref="PhysicsSettings.From"/> rather than changing
/// <see cref="ActivePhysics"/>, which is shared by every test.
/// </summary>
public class TuningTests
{
    private static double Carry(PhysicsSettings s, double mph = 118, double launch = 16.5, double spin = 7000) =>
        SquareGspro.Physics.BallFlightSimulator.Simulate(
            SquareGspro.Physics.LaunchConditions.FromDisplayUnits(mph, launch, 0, spin, 0), s.Ball, s.Environment,
            SquareGspro.Physics.SimulationRequest.DefaultTerrain,
            SquareGspro.Physics.SimulationOptions.Default with { EnableBounce = false }).CarryDistance;

    [Fact]
    public void DefaultsAreTheEngineDefaults()
    {
        var s = PhysicsSettings.From(PhysicsTuning.Defaults);
        Assert.Same(SquareGspro.Physics.BallProperties.Standard, s.Ball);
        Assert.Same(SquareGspro.Physics.EnvironmentConditions.Standard, s.Environment);
        Assert.Equal(SquareGspro.Physics.Surfaces.Fairway with { }, s.SurfaceFor(Lie.Fairway));
        Assert.Equal(SquareGspro.Physics.Surfaces.Rough, s.SurfaceFor(Lie.Rough));
        Assert.Equal(SquareGspro.Physics.Surfaces.DeepRough, s.SurfaceFor(Lie.DeepRough));
        Assert.Equal(SquareGspro.Physics.Surfaces.Sand, s.SurfaceFor(Lie.Bunker));
        Assert.Equal(SquareGspro.Physics.Surfaces.Fringe, s.SurfaceFor(Lie.Fringe));
        Assert.Equal(CourseGround.DefaultSurfaceFor(Lie.Green), s.SurfaceFor(Lie.Green));
        foreach (var kind in Enum.GetValues<SquareGspro.Physics.LieKind>())
            Assert.Equal(SquareGspro.Physics.LieModel.Default.ResponseFor(kind), s.LieModel.ResponseFor(kind));
        Assert.Equal(0, PhysicsTuning.Defaults.ChangedCount);
    }

    [Fact]
    public void ValuesChangeTheFlight()
    {
        var defaults = PhysicsSettings.From(PhysicsTuning.Defaults);
        double baseline = Carry(defaults);
        Assert.True(Carry(PhysicsSettings.From(PhysicsTuning.Defaults.With("flight.drag", 1.3))) < baseline - 3, "more drag, less carry");
        Assert.True(Carry(PhysicsSettings.From(PhysicsTuning.Defaults.With("air.altitude", 5000))) > baseline + 3, "thin air carries further");
        Assert.True(Carry(PhysicsSettings.From(PhysicsTuning.Defaults.With("wind.speed", 15))) < baseline - 3, "into the wind");
        Assert.True(Carry(PhysicsSettings.From(PhysicsTuning.Defaults.With("wind.speed", 15).With("wind.from", 180))) > baseline + 3, "downwind");

        var rough = PhysicsSettings.From(PhysicsTuning.Defaults.With("lie.rough.spin", 40)).LieModel.ResponseFor(SquareGspro.Physics.LieKind.Rough);
        Assert.Equal(0.40, rough.SpinFactor, 6);
        var fast = PhysicsSettings.From(PhysicsTuning.Defaults.With("ground.green.stimp", 13)).SurfaceFor(Lie.Green);
        Assert.True(fast.RollingResistance < defaults.SurfaceFor(Lie.Green).RollingResistance);
        var grabby = PhysicsSettings.From(PhysicsTuning.Defaults.With("ground.fairway.friction", 0.6)).SurfaceFor(Lie.Fairway);
        Assert.Equal(0.6, grabby.DynamicFriction, 9);
        Assert.Equal(0.65, grabby.StaticFriction, 9);
    }

    [Fact]
    public void SavesAndLoads()
    {
        var t = PhysicsTuning.Defaults.With("flight.lift", 1.12).With("ground.sand.rolling", 0.8).Named("Firm and fast");
        var back = PhysicsTuning.FromJson(t.ToJson());
        Assert.Equal("Firm and fast", back.Name);
        Assert.Equal(1.12, back["flight.lift"], 9);
        Assert.Equal(0.8, back["ground.sand.rolling"], 9);
        Assert.Equal(2, back.ChangedCount);
        Assert.Equal(new[] { "flight.lift", "ground.sand.rolling" }, back.ChangesFrom(PhysicsTuning.Defaults).Select(c => c.Parameter.Key));

        // Unknown keys are ignored, missing ones are defaults, out-of-range values are clamped.
        var odd = PhysicsTuning.FromJson("""{ "values": { "flight.drag": 9, "no.such": 1 } }""");
        Assert.Equal(CalibrationMax, odd["flight.drag"]);
        Assert.Equal(1, odd["flight.lift"]);
        Assert.Equal(0, odd.Reset(PhysicsTuning.Flight).ChangedCount);
    }

    private const double CalibrationMax = SquareGspro.Physics.CalibrationProfile.MaximumAeroScale;

    [Fact]
    public void ARoundCanBeRewoundToReplayAShot()
    {
        var round = new HoleRound(TestCourse.Holes);
        var before = round.Save();
        var shot = new RangeShot(160, 12, 0, 2700, 0, null, null);
        var first = round.Play(shot, false, round.DefaultAimHeading(), "DR");
        Assert.Equal(1, round.Strokes);
        round.Restore(before);
        Assert.Equal(0, round.Strokes);
        Assert.Equal(TestCourse.Holes[0].Tee, round.Ball);
        var again = round.Play(shot, false, round.DefaultAimHeading(), "DR");
        Assert.Equal(first.Rest, again.Rest);
        Assert.Equal(1, round.Strokes);
    }
}

public class OsmCourseTests
{
    private static readonly Lazy<SquareGspro.Core.Course.Course> DeerRun = new(() => OsmCourse.FromResource("deer-run.osm", "Deer Run"));

    [Fact]
    public void DeerRunImportsEighteenHolesInOrder()
    {
        var c = DeerRun.Value;
        Assert.True(c.SharedLayout);
        Assert.Equal(Enumerable.Range(1, 18), c.Holes.Select(h => h.Number));
        Assert.Equal(72, c.Holes.Sum(h => h.Par));
        Assert.Equal(new[] { 4, 4, 4, 4, 5, 3, 4, 3, 5, 4, 3, 5, 4, 3, 5, 4, 4, 4 }, c.Holes.Select(h => h.Par));
        // Published: 6,745 yd from the back tees.
        Assert.InRange(c.Holes.Sum(h => h.LengthYards), 6600, 6900);
        Assert.InRange(c.Holes[14].LengthYards, 530, 590);      // 15: the long par 5
        // Holes are numbered correctly in the map (7 used to be tagged 8; the synthetic test covers renumbering).
        Assert.DoesNotContain(c.Notes, n => n.Contains("playing it as hole"));
        // Every par 4 and 5 has a mapped fairway; the par 3s get a generated apron.
        Assert.Contains(c.Notes, n => n.StartsWith("Fairways aren't mapped for holes 6, 8, 11, 14;"));
    }

    [Fact]
    public void DeerRunMappedFairwaysAndPondsArePlayable()
    {
        var c = DeerRun.Value;
        var h1 = c.Holes[0];
        Assert.True(h1.Fairways.OfType<Polygon>().Count() >= 17);
        Assert.True(h1.Water.OfType<Polygon>().Count() >= 7);
        Assert.True(h1.Water.OfType<Strip>().Count() >= 4);     // ditches
        // Every pond plays as water.
        foreach (var pond in h1.Water.OfType<Polygon>())
        {
            // A point inside it (a curved pond's centroid can be outside).
            var b = pond.Box;
            var inside = Enumerable.Range(0, 400).Select(i => new P2(b.Min.X + (b.Max.X - b.Min.X) * (i % 20 + 0.5) / 20, b.Min.Z + (b.Max.Z - b.Min.Z) * (i / 20 + 0.5) / 20))
                .First(pond.Contains);
            Assert.Equal(Lie.Water, h1.LieAt(inside));
        }
    }

    [Fact]
    public void DeerRunHolesHaveTeesGreensAndBounds()
    {
        foreach (var h in DeerRun.Value.Holes)
        {
            Assert.Equal(Lie.Tee, h.LieAt(h.Tee));
            Assert.Equal(Lie.Green, h.LieAt(h.Pin));
            Assert.True(h.InBounds(h.Tee) && h.InBounds(h.Pin), $"hole {h.Number}");
            // Mostly fairway down the line (15 has a strip of rough at the ditch; 10 ends short of the pond).
            if (h.Par >= 4)
                Assert.True(new[] { 0.4, 0.5, 0.6, 0.7, 0.8 }.Count(f => h.LieAt(OsmCourse.PointAt(h.Route!, OsmCourse.Length(h.Route!) * f)) == Lie.Fairway) >= 3,
                    $"hole {h.Number}");
            Assert.Equal(Lie.OutOfBounds, h.LieAt(h.Pin + new P2(5000, 0)));
            Assert.InRange(h.Tee.DistanceTo(h.TeeAim), 100, 290);
        }
        Assert.True(DeerRun.Value.Holes[0].Bunkers.Count >= 20);
    }

    [Fact]
    public void RoundOverTheMappedCoursePlays()
    {
        var round = new HoleRound(DeerRun.Value.Holes);
        var rng = new Random(3);
        for (int shots = 0; shots < 12 && !round.Holed; shots++)
        {
            string club = Caddie.Suggest(round.Ball.DistanceTo(round.DefaultTarget), round.Lie);
            var preset = ClubPresets.All[ClubPresets.IndexOf(club)];
            double target = club == "PT" ? round.DistanceToPin : round.Ball.DistanceTo(round.DefaultTarget);
            round.Play(Caddie.DemoShot(preset, target, round.Lie, rng), club == "PT", round.DefaultAimHeading());
        }
        Assert.True(round.Holed);
    }

    private static readonly Lazy<SquareGspro.Core.Course.Course> DeerRunWithGround = new(() => OsmCourse.FromResource("deer-run.osm", "Deer Run", "deer-run.dem"));

    private static IEnumerable<P2> Inside(Polygon poly, int n = 30)
    {
        var b = poly.Box;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                var p = new P2(b.Min.X + (b.Max.X - b.Min.X) * (i + 0.5) / n, b.Min.Z + (b.Max.Z - b.Min.Z) * (j + 0.5) / n);
                if (poly.Contains(p)) yield return p;
            }
    }

    private static double StdDev(IReadOnlyCollection<double> v)
    {
        double m = v.Average();
        return Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / v.Count);
    }

    [Fact]
    public void DeerRunElevationLoadsAndLinesUpWithTheMap()
    {
        var c = DeerRunWithGround.Value;
        var t = c.Terrain!;
        Assert.NotNull(t);
        Assert.InRange(t.Datum, 150, 350);                         // Moulton is ~200 m above sea level

        // Ponds are flattened in lidar elevation models: inside the mapped outline the ground should be level
        // (if the grid were shifted against the map, the banks would show up as spread). Checked before the
        // app carves its own pond beds.
        var raw = OsmCourse.FromResource("deer-run.osm", "Deer Run", "deer-run.dem", shapeWater: false);
        foreach (var pond in raw.Holes[0].Water.OfType<Polygon>())
        {
            var inside = Inside(pond).Select(raw.Terrain!.HeightAt).ToList();
            Assert.True(StdDev(inside) < 0.35, $"pond spread {StdDev(inside):0.00} m");
            Assert.NotNull(t.WaterLevel(c.Holes[0].Water.First(w => w is Polygon q && q.Box == pond.Box)));   // and they're given a level surface
        }
        // Greens are gentler than the land around them (1 m data is a little noisy, so allow some slope).
        var greenSlopes = c.Holes[0].Greens.OfType<Polygon>().Select(g => Inside(g, 12).Average(p => t.Gradient(p).Length)).ToList();
        Assert.All(greenSlopes, s => Assert.True(s < 0.05, $"green slope {s:P1}"));
    }

    [Fact]
    public void RoundOverDeerRunWithGroundPlays()
    {
        var c = DeerRunWithGround.Value;
        var round = new HoleRound(c.Holes, c.Terrain);
        var rng = new Random(5);
        for (int hole = 0; hole < 3; hole++)
        {
            round.StartHole(hole);
            for (int shots = 0; shots < 14 && !round.Holed; shots++)
            {
                bool onGreen = round.Lie == Lie.Green;
                string club = Caddie.Suggest(round.PlaysLike(round.DefaultTarget), round.Lie);
                var preset = ClubPresets.All[ClubPresets.IndexOf(club)];
                var shot = club == "PT" && onGreen ? Caddie.DemoPutt(round.ReadPutt().Speed, rng)
                         : Caddie.DemoShot(preset, club == "PT" ? round.DistanceToPin : round.PlaysLike(round.DefaultTarget), round.Lie, rng);
                var o = round.Play(shot, club == "PT", round.DefaultAimHeading());
                // The ball is always on (or in) the ground where it stops.
                var last = o.Flight.Points[^1];
                Assert.True(o.Holed || Math.Abs(last.Y - round.GroundAt(o.Rest)) < 0.01 || o.Penalty > 0);
            }
            Assert.True(round.Holed, $"hole {hole + 1}");
        }
    }
}

public class TerrainPhysicsTests
{
    /// <summary>The test par 4 (flat lies) laid on ground with the given height function.</summary>
    private static HoleRound On(Func<P2, double> height, int hole = 0)
    {
        var area = new Bounds(new P2(-100, -150), new P2(450, 150));
        var round = new HoleRound(TestCourse.Holes, new Terrain(area, 1.0, height));
        round.StartHole(hole);
        return round;
    }

    private static readonly RangeShot SevenIron = new(120, 16, 0, 7000, 0, null, null);

    [Fact]
    public void UphillShotsComeDownShortAndDownhillOnesCarry()
    {
        double Carry(Func<P2, double> h) { var r = On(h); return r.Play(SevenIron, false, 0).Flight.CarryYards; }
        double flat = Carry(_ => 0), up = Carry(p => p.X * 0.08), down = Carry(p => -p.X * 0.08);
        Assert.True(up < flat - 5, $"up {up:0} flat {flat:0}");
        Assert.True(down > flat + 5, $"down {down:0} flat {flat:0}");
    }

    [Fact]
    public void PlaysLikeAddsTheRise()
    {
        var r = On(p => p.X * 0.05);
        var target = new P2(100, 0);
        Assert.Equal(105, r.PlaysLike(target), 1);
    }

    /// <summary>Puts the ball on the test par 3's green, 6 m short of the pin.</summary>
    private static HoleRound OnGreen(Func<P2, double> height)
    {
        var r = On(height, hole: 1);
        var pin = r.Hole.Pin;
        // Play a gentle shot to get onto the green; then putt from there (move the ball by playing to a known spot).
        var shot = new RangeShot(112, 18, 0, 7600, 0, null, null);
        r.Play(shot, false, r.DefaultAimHeading());
        Assert.Equal(Lie.Green, r.Lie);
        return r;
    }

    [Fact]
    public void PuttAcrossASideSlopeBreaksDownhill()
    {
        // Ground falls to the right (+Z) at 2%.
        var r = OnGreen(p => -p.Z * 0.02);
        double straight = HoleRound.Heading(r.Ball, r.Hole.Pin), d = r.DistanceToPin;
        double mph = Math.Sqrt(2 * 0.55 * (d + 0.4)) * ShotMapper.MsToMph;
        var start = r.Ball;
        var o = r.Play(new RangeShot(mph, 1, 0, 0, 0, null, null), true, straight);
        // Downhill is +Z whichever way the putt runs: the path bends to the side of the start line that faces +Z.
        var side = new P2(-Math.Sin(straight), Math.Cos(straight));
        if (side.Z < 0) side = side * -1;
        double bend = o.Flight.Points.Max(p => (p.X - start.X) * side.X + (p.Z - start.Z) * side.Z);
        Assert.True(bend > 0.05 * Math.Min(d, 6), $"should break downhill: {bend:0.00} m over a {d:0.0} m putt");
    }

    [Fact]
    public void ReadPuttAimsAboveTheHoleAndHolesIt()
    {
        var r = OnGreen(p => -p.Z * 0.02 + p.X * 0.01);   // falls toward +Z, and rises toward +X
        var (heading, speed) = r.ReadPutt();
        // Aimed at a point on the high side (-Z) of the hole.
        var aimPoint = r.Ball + new P2(Math.Cos(heading), Math.Sin(heading)) * r.DistanceToPin;
        Assert.True(aimPoint.Z < r.Hole.Pin.Z - 0.05, $"should aim above the hole, aimed {aimPoint.Z - r.Hole.Pin.Z:0.00} m");
        var o = r.Play(new RangeShot(speed * ShotMapper.MsToMph, 1, 0, 0, 0, null, null), true, heading);
        Assert.True(o.Holed && o.Flight.Points[^1].Y < r.GroundAt(r.Hole.Pin) - 0.05, o.Message);
    }

    [Fact]
    public void BallRestsOnAGentleSlopeButRollsOffASteepOne()
    {
        // A dribbled putt barely moves on a 2% green...
        var gentle = OnGreen(p => -p.Z * 0.02);
        var start = gentle.Ball;
        gentle.Play(new RangeShot(0.5, 1, 0, 0, 0, null, null), true, gentle.DefaultAimHeading());
        Assert.True(gentle.Ball.DistanceTo(start) < 0.3);

        // ...but nothing can rest on a 12% bank (the slope's pull, 5/7 g × 0.12, beats a stimp-10 green's rolling
        // resistance): the approach lands and keeps running downhill (+Z), off the green.
        var steep = On(p => -p.Z * 0.12, hole: 1);
        var o = steep.Play(new RangeShot(112, 18, 0, 7600, 0, null, null), false, steep.DefaultAimHeading());
        var landed = o.Flight.Points[o.Flight.LandingIndex];
        Assert.True(o.Rest.Z - landed.Z > 1, $"rolled {o.Rest.Z - landed.Z:0.00} m downhill");
    }

    [Fact]
    public void DemoPuttUsesTheGivenPace()
    {
        var s = Caddie.DemoPutt(2.0, new Random(1));
        Assert.InRange(s.BallMph / ShotMapper.MsToMph, 1.85, 2.15);
        Assert.InRange(s.DirectionDeg, -1.0, 1.0);
    }
}

public class OsmImporterTests
{

    [Fact]
    public void ImporterRepairsNumbersAndBuildsRings()
    {
        // Two holes with no course outline; the second is drawn green-to-tee and has no number. The green is split
        // across two ways inside a multipolygon.
        const string osm = """
            <osm>
              <node id="1" lat="34.0000" lon="-87.0000"/>
              <node id="2" lat="34.0030" lon="-87.0000"/>
              <node id="3" lat="34.0031" lon="-87.0000"/>
              <node id="4" lat="34.0045" lon="-87.0000"/>
              <node id="10" lat="34.00295" lon="-87.00008"/>
              <node id="11" lat="34.00305" lon="-87.00008"/>
              <node id="12" lat="34.00305" lon="-86.99992"/>
              <node id="13" lat="34.00295" lon="-86.99992"/>
              <way id="100"><nd ref="1"/><nd ref="2"/><tag k="golf" v="hole"/><tag k="ref" v="1"/><tag k="par" v="4"/></way>
              <way id="101"><nd ref="4"/><nd ref="3"/><tag k="golf" v="hole"/></way>
              <way id="200"><nd ref="10"/><nd ref="11"/><nd ref="12"/></way>
              <way id="201"><nd ref="12"/><nd ref="13"/><nd ref="10"/></way>
              <relation id="300"><member type="way" ref="200" role="outer"/><member type="way" ref="201" role="outer"/>
                <tag k="type" v="multipolygon"/><tag k="golf" v="green"/></relation>
            </osm>
            """;
        var c = OsmCourse.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(osm)));
        Assert.Equal(2, c.Holes.Count);
        Assert.Equal(Lie.Green, c.Holes[0].LieAt(c.Holes[0].Pin));
        Assert.Equal(3, c.Holes[1].Par);                          // guessed from ~155 m
        Assert.True(c.Holes[1].Tee.X > c.Holes[1].Pin.X);         // reversed: tees off from the north end
        Assert.Contains(c.Notes, n => n.Contains("no number"));
    }
}

public class CartPathTests
{
    /// <summary>A straight par 4 down +X with a 2.5 m path across it at 150 m, and a wide concrete apron at 145-160 m on <paramref name="apron"/>.</summary>
    private static Hole Hole(bool apron = false)
    {
        var path = new Strip(new[] { new P2(150, -60), new P2(150, 60) }, 2.5);
        var slab = new Polygon(new[] { new P2(145, -60), new P2(160, -60), new P2(160, 60), new P2(145, 60) });
        return new Hole(1, "", 4, new P2(0, 0), new P2(300, 0), new P2(230, 0),
            new Shape[] { new Strip(new[] { new P2(20, 0), new P2(290, 0) }, 40) }, new Shape[] { new Ellipse(new P2(300, 0), 12, 12) },
            Array.Empty<Shape>(), Array.Empty<Shape>(), new Bounds(new P2(-50, -100), new P2(400, 100)))
        {
            CartPaths = apron ? new Shape[] { slab } : new Shape[] { path },
        };
    }

    [Fact]
    public void PathIsItsOwnLieOverTheFairway()
    {
        var h = Hole();
        Assert.Equal(Lie.CartPath, h.LieAt(new P2(150, 0)));
        Assert.Equal(Lie.CartPath, h.LieAt(new P2(150, 50)));   // out in the rough too
        Assert.Equal(Lie.Fairway, h.LieAt(new P2(152, 0)));
    }

    [Fact]
    public void ReliefIsTheNearestClearPointNoNearerTheHole()
    {
        var round = new HoleRound(new[] { Hole() });
        var at = new P2(150.3, 0);
        var drop = round.CartPathRelief(at);
        Assert.NotNull(drop);
        var (p, lie) = drop!.Value;
        Assert.Equal(Lie.Fairway, lie);
        Assert.True(p.DistanceTo(round.Hole.Pin) >= at.DistanceTo(round.Hole.Pin));
        // Behind the path (away from the hole), just clear of it: edge at 148.75, plus the stance clearance.
        Assert.InRange(p.X, 148.75 - HoleRound.ReliefClearance - 0.3, 148.75 - HoleRound.ReliefClearance + 0.01);
        Assert.InRange(Math.Abs(p.Z), 0, 1.0);
    }

    [Fact]
    public void BallStoppingOnThePathGetsFreeRelief()
    {
        var round = new HoleRound(new[] { Hole(apron: true) });
        round.Restore(new HoleRound.State(0, new P2(140, 0), Lie.Fairway, 1, false, null));
        // Rolls onto the concrete and stops on it (it runs easily on concrete, so anywhere on the 15 m slab).
        var o = round.Play(new RangeShot(9, 1, 0, 0, 0, null, null), putt: true, 0);
        Assert.Equal(Lie.CartPath, o.RestLie);
        Assert.Contains("free relief", o.Message);
        Assert.Equal(0, o.Penalty);
        Assert.Equal(2, round.Strokes);
        Assert.Equal(Lie.Fairway, round.Lie);
        Assert.Equal(round.Ball, o.NextBall);
        Assert.True(round.Ball.X <= 145 - HoleRound.ReliefClearance + 0.01, $"dropped at {round.Ball.X:0.00}");
    }

    [Fact]
    public void ConcreteBouncesHigherAndRollsFurtherThanFairway()
    {
        var concrete = CourseGround.SurfaceFor(Lie.CartPath).Effective();
        var fairway = CourseGround.SurfaceFor(Lie.Fairway).Effective();
        foreach (double v in new[] { 3.0, 8.0, 15.0, 25.0 })
            Assert.True(concrete.RestitutionAt(v) > fairway.RestitutionAt(v) + 0.1, $"at {v} m/s");
        Assert.True(concrete.RollingResistance < fairway.RollingResistance / 2);
        Assert.Contains(PhysicsTuning.Parameters, p => p.Key == "ground.cartPath.bounce");
    }

    [Fact]
    public void DeerRunCartPathsAreImported()
    {
        var h = OsmCourse.FromResource("deer-run.osm", "Deer Run").Holes[0];
        var paths = h.CartPaths.OfType<Strip>().ToList();
        Assert.True(paths.Count >= 14);
        // Untagged paths get the default width; one is tagged width=1.25.
        Assert.True(paths.Count(p => p.Width == OsmCourse.CartPathWidth) >= paths.Count - 1);
        Assert.Contains(paths, p => p.Width == 1.25);
        // The five short ones bridge the ditches. Every path plays as path all along, bridges included.
        Assert.Equal(5, h.Bridges.Count);
        foreach (var p in paths)
        {
            double len = OsmCourse.Length(p.Points);
            Assert.All(Enumerable.Range(0, 11), i => Assert.Equal(Lie.CartPath, h.LieAt(OsmCourse.PointAt(p.Points, len * i / 10))));
        }
    }

    [Fact]
    public void BridgeOverWaterIsPathNotWater()
    {
        var ditch = new Strip(new[] { new P2(100, -60), new P2(100, 60) }, 2);
        var bridge = new Strip(new[] { new P2(96, 0), new P2(104, 0) }, 2.5);
        var h = Hole() with { Water = new Shape[] { ditch } };
        h = h with { CartPaths = new Shape[] { bridge }, Bridges = new Shape[] { bridge } };
        Assert.Equal(Lie.CartPath, h.LieAt(new P2(100, 0)));
        Assert.Equal(Lie.Water, h.LieAt(new P2(100, 3)));
        // Relief from the middle of the bridge is off the end, not into the ditch.
        var round = new HoleRound(new[] { h });
        var (at, lie) = round.CartPathRelief(new P2(100, 0))!.Value;
        Assert.Equal(Lie.Fairway, lie);
        Assert.True(ditch.DistanceTo(at) > 0 && bridge.DistanceTo(at) >= HoleRound.ReliefClearance - 1e-9);
    }
}
