using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GrimesGolf.Core.OpenConnect;
using Xunit;

namespace GrimesGolf.Tests;

public class OpenConnectProtocolTests
{
    // The example from the GSPro Open Connect v1 page (its comments taken out).
    private const string SpecShot = """
        {
        "DeviceID": "GSPro LM 1.1", "Units": "Yards", "ShotNumber": 13, "APIversion": "1",
        "BallData": { "Speed": 147.5, "SpinAxis": -13.2, "TotalSpin": 3250.0, "BackSpin": 2500.0, "SideSpin": -800.0,
                      "HLA": 2.3, "VLA": 14.3, "CarryDistance": 256.5 },
        "ClubData": { "Speed": 0.0, "AngleOfAttack": 0.0, "FaceToTarget": 0.0, "Lie": 0.0, "Loft": 0.0, "Path": 0.0,
                      "SpeedAtImpact": 0.0, "VerticalFaceImpact": 0.0, "HorizontalFaceImpact": 0.0, "ClosureRate": 0.0 },
        "ShotDataOptions": { "ContainsBallData": true, "ContainsClubData": false, "LaunchMonitorIsReady": true,
                             "LaunchMonitorBallDetected": true, "IsHeartBeat": false }
        }
        """;

    [Fact]
    public void ReadsTheSpecExampleShot()
    {
        var m = OpenConnectProtocol.Parse(SpecShot)!;
        Assert.Equal("GSPro LM 1.1", m.DeviceID);
        Assert.Equal(13, m.ShotNumber);
        Assert.True(m.IsShot);
        var (shot, error) = OpenConnectProtocol.ToShot(m);
        Assert.Null(error);
        var s = shot!.Shot;
        Assert.Equal(147.5, s.BallMph);
        Assert.Equal(14.3, s.LaunchDeg);
        Assert.Equal(2.3, s.DirectionDeg);
        Assert.Equal(3250, s.SpinRpm);
        Assert.Equal(-13.2, s.SpinAxisDeg);    // total spin and axis win over back and side spin
        Assert.Null(s.ClubMph);                // ContainsClubData false: its zeros aren't a club speed
        Assert.Equal(Core.ShotSource.Monitor, s.Source);
        Assert.Empty(shot.Warnings);
    }

    [Fact]
    public void SpinFromBackAndSideWhenNoTotal()
    {
        var m = OpenConnectProtocol.Parse("""
            {"DeviceID":"X","ShotNumber":1,"BallData":{"Speed":120,"BackSpin":5000,"SideSpin":-500,"HLA":-1,"VLA":18},
             "ShotDataOptions":{"ContainsBallData":true,"ContainsClubData":false}}
            """)!;
        var s = OpenConnectProtocol.ToShot(m).Shot!.Shot;
        Assert.Equal(Math.Sqrt(5000.0 * 5000 + 500 * 500), s.SpinRpm, 6);
        Assert.Equal(Math.Atan2(-500, 5000) * 180 / Math.PI, s.SpinAxisDeg, 6);   // sidespin left = axis left
        Assert.True(s.SpinAxisDeg < 0);
    }

    [Fact]
    public void ClubSpeedAndSmashWhenClubDataSent()
    {
        var m = OpenConnectProtocol.Parse("""
            {"DeviceID":"GSPRO-R10","Units":"Yards","ShotNumber":4,"APIVersion":"1",
             "BallData":{"Speed":150,"SpinAxis":3,"TotalSpin":2600,"HLA":1,"VLA":12},
             "ClubData":{"Speed":100,"Path":2.5,"FaceToTarget":1},
             "ShotDataOptions":{"ContainsBallData":true,"ContainsClubData":true}}
            """)!;
        var s = OpenConnectProtocol.ToShot(m).Shot!.Shot;
        Assert.Equal(100, s.ClubMph);
        Assert.Equal(1.5, s.Smash!.Value, 6);
    }

    [Fact]
    public void ShotSentAsHeartbeatStillCounts()
    {
        // The MLM2PRO app's "pretend to be SquareGolf" mode flags its shots IsHeartBeat true.
        var m = OpenConnectProtocol.Parse("""
            {"DeviceID":"SquareGolf","ShotNumber":2,"BallData":{"Speed":100,"TotalSpin":6000,"SpinAxis":0,"HLA":0,"VLA":20},
             "ShotDataOptions":{"ContainsBallData":true,"ContainsClubData":true,"IsHeartBeat":true}}
            """)!;
        Assert.True(m.IsShot);
    }

    [Theory]
    [InlineData("""{"ShotNumber":0,"ShotDataOptions":{"ContainsBallData":false,"ContainsClubData":false,"LaunchMonitorIsReady":true,"IsHeartBeat":true}}""")]
    [InlineData("""{"ShotNumber":2,"BallData":{"Speed":0.0},"ShotDataOptions":{"ContainsBallData":false,"LaunchMonitorIsReady":true,"IsHeartBeat":false}}""")]
    [InlineData("""{"ShotNumber":3,"BallData":{"Speed":0},"ShotDataOptions":{"ContainsBallData":true}}""")]
    public void HeartbeatsAndReadySignalsAreNotShots(string json) => Assert.False(OpenConnectProtocol.Parse(json)!.IsShot);

    [Theory]
    [InlineData(300, 12, 0, 3000, "Ball speed")]
    [InlineData(150, 95, 0, 3000, "Launch angle")]
    [InlineData(150, 12, 80, 3000, "Launch direction")]
    [InlineData(150, 12, 0, 30000, "Spin")]
    public void RejectsUnbelievableShots(double speed, double vla, double hla, double spin, string what)
    {
        var m = new OpenConnectMessage
        {
            BallData = new OpenConnectBallData { Speed = speed, VLA = vla, HLA = hla, TotalSpin = spin },
            ShotDataOptions = new OpenConnectShotDataOptions { ContainsBallData = true },
        };
        var (shot, error) = OpenConnectProtocol.ToShot(m);
        Assert.Null(shot);
        Assert.StartsWith(what, error);
    }

    [Fact]
    public void ReadsNumbersSentAsStringsAndIgnoresUnknownFields()
    {
        var m = OpenConnectProtocol.Parse("""
            {"DeviceID":"X","ShotNumber":"7","Extra":{"a":1},
             "BallData":{"Speed":"101.5","TotalSpin":"7000","SpinAxis":"-2","HLA":"0.5","VLA":"19"},
             "ShotDataOptions":{"ContainsBallData":true,"ContainsClubData":false}}
            """)!;
        Assert.Equal(7, m.ShotNumber);
        Assert.Equal(101.5, OpenConnectProtocol.ToShot(m).Shot!.Shot.BallMph);
    }

    [Fact]
    public void WarnsAboutOtherUnits()
    {
        var m = OpenConnectProtocol.Parse(SpecShot.Replace("\"Yards\"", "\"Meters\""))!;
        Assert.Single(OpenConnectProtocol.ToShot(m).Shot!.Warnings);
    }

    [Fact]
    public void BadJsonIsNotAMessage() => Assert.Null(OpenConnectProtocol.Parse("{\"DeviceID\": }"));

    [Fact]
    public void PlayerInformationAsInTheSpec()
    {
        string json = OpenConnectProtocol.Serialize(OpenConnectProtocol.PlayerInfo(false, "DR", 0));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(201, root.GetProperty("Code").GetInt32());
        Assert.Equal("GSPro Player Information", root.GetProperty("Message").GetString());
        var p = root.GetProperty("Player");
        Assert.Equal("RH", p.GetProperty("Handed").GetString());
        Assert.Equal("DR", p.GetProperty("Club").GetString());
        Assert.True(p.GetProperty("DistanceToTarget").GetDouble() > 0);   // never 0 (the MLM2PRO app won't arm on 0)
    }

    [Fact]
    public void LeftHandedPutterAndUnknownClubs()
    {
        Assert.Equal(new OpenConnectPlayer("LH", "PT", 12), OpenConnectProtocol.PlayerInfo(true, "pt", 12).Player);
        Assert.Equal("unknown", OpenConnectProtocol.PlayerInfo(false, "XX", 100).Player!.Club);
    }

    [Fact]
    public void ResponsesLeaveOutAMissingPlayer() =>
        Assert.Equal("""{"Code":200,"Message":"Shot received successfully"}""", OpenConnectProtocol.Serialize(OpenConnectProtocol.Received()));
}

public class JsonObjectFramerTests
{
    [Fact]
    public void SplitsObjectsSentBackToBack()
    {
        var f = new JsonObjectFramer();
        Assert.Equal(new[] { "{\"a\":1}", "{\"b\":{\"c\":2}}", "{}" }, f.Push("{\"a\":1}{\"b\":{\"c\":2}}\r\n{}"));
    }

    [Fact]
    public void JoinsAnObjectSplitAcrossReads()
    {
        var f = new JsonObjectFramer();
        Assert.Empty(f.Push("{\"a\":{\"b\":"));
        Assert.Empty(f.Push("1}"));
        Assert.Equal(new[] { "{\"a\":{\"b\":1}}" }, f.Push("}{\"x\""));
        Assert.Equal(new[] { "{\"x\":2}" }, f.Push(":2}"));
    }

    [Fact]
    public void BracesAndQuotesInsideStringsDontCount()
    {
        var f = new JsonObjectFramer();
        string obj = "{\"DeviceID\":\"odd } { \\\" name\"}";
        Assert.Equal(new[] { obj, obj }, f.Push(obj + obj));
    }

    [Fact]
    public void Utf8SplitMidCharacter()
    {
        var f = new JsonObjectFramer();
        byte[] bytes = Encoding.UTF8.GetBytes("{\"DeviceID\":\"Café\"}");
        int split = Array.IndexOf(bytes, (byte)0xC3) + 1;   // between the two bytes of é
        Assert.Empty(f.Push(bytes.AsSpan(0, split)));
        Assert.Equal(new[] { "{\"DeviceID\":\"Café\"}" }, f.Push(bytes.AsSpan(split)));
    }

    [Fact]
    public void DropsAnObjectThatNeverCloses()
    {
        var f = new JsonObjectFramer();
        bool dropped = false;
        f.Overflowed += () => dropped = true;
        f.Push("{\"a\":\"" + new string('x', JsonObjectFramer.MaxObjectChars + 10));
        Assert.True(dropped);
        Assert.Equal(new[] { "{}" }, f.Push("\"}{}"));   // the tail of the junk is skipped; the next object is whole
    }
}

public class OpenConnectServerTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private const string Shot = """
        {"DeviceID":"GSPRO-R10","Units":"Yards","ShotNumber":1,"APIVersion":"1",
         "BallData":{"Speed":140,"SpinAxis":-4,"TotalSpin":3000,"HLA":-1.5,"VLA":13},
         "ShotDataOptions":{"ContainsBallData":true,"ContainsClubData":false}}
        """;

    private const string Ready = """
        {"DeviceID":"GSPRO-R10","ShotNumber":0,"ShotDataOptions":{"ContainsBallData":false,"ContainsClubData":false,
         "LaunchMonitorIsReady":true,"LaunchMonitorBallDetected":true,"IsHeartBeat":true}}
        """;

    private static async Task<TcpClient> ConnectAsync(OpenConnectServer server)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", server.Port);
        return tcp;
    }

    private static Task SendAsync(TcpClient tcp, string text) => tcp.GetStream().WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask();

    /// <summary>Reads until one whole JSON reply has arrived.</summary>
    private static async Task<JsonElement> ReadReplyAsync(TcpClient tcp)
    {
        var framer = new JsonObjectFramer();
        var buf = new byte[4096];
        using var cts = new CancellationTokenSource(Wait);
        while (true)
        {
            int n = await tcp.GetStream().ReadAsync(buf, cts.Token);
            Assert.True(n > 0, "the server closed the connection");
            if (framer.Push(buf.AsSpan(0, n)) is { Count: > 0 } got) return JsonDocument.Parse(got[0]).RootElement.Clone();
        }
    }

    private static async Task<T> Within<T>(Task<T> t) => await t.WaitAsync(Wait);

    [Fact]
    public async Task ShotIsReceivedAndAcknowledged()
    {
        using var server = new OpenConnectServer(port: 0);
        var shot = new TaskCompletionSource<OpenConnectShot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var device = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ShotReceived += s => shot.TrySetResult(s);
        server.DeviceNamed += d => device.TrySetResult(d);
        server.Start();

        using var tcp = await ConnectAsync(server);
        await SendAsync(tcp, Shot);
        var s = await Within(shot.Task);
        Assert.Equal(140, s.Shot.BallMph);
        Assert.Equal(-4, s.Shot.SpinAxisDeg);
        Assert.Equal("GSPRO-R10", await Within(device.Task));
        var reply = await ReadReplyAsync(tcp);
        Assert.Equal(200, reply.GetProperty("Code").GetInt32());
    }

    [Fact]
    public async Task ReadySignalsAreReportedAndNotAnswered()
    {
        using var server = new OpenConnectServer(port: 0);
        var ready = new TaskCompletionSource<(bool, bool?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shot = new TaskCompletionSource<OpenConnectShot>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ReadyChanged += (r, b) => ready.TrySetResult((r, b));
        server.ShotReceived += s => shot.TrySetResult(s);
        server.Start();

        using var tcp = await ConnectAsync(server);
        await SendAsync(tcp, Ready + Shot);   // in one write, no newline between
        Assert.Equal((true, (bool?)true), await Within(ready.Task));
        await Within(shot.Task);
        // The first reply is the shot's 200: the ready signal got none.
        Assert.Equal(200, (await ReadReplyAsync(tcp)).GetProperty("Code").GetInt32());
    }

    [Fact]
    public async Task BadShotGetsAFailure()
    {
        using var server = new OpenConnectServer(port: 0);
        bool got = false;
        server.ShotReceived += _ => got = true;
        server.Start();

        using var tcp = await ConnectAsync(server);
        await SendAsync(tcp, Shot.Replace("\"Speed\":140", "\"Speed\":900"));
        var reply = await ReadReplyAsync(tcp);
        Assert.Equal(501, reply.GetProperty("Code").GetInt32());
        Assert.False(got);
    }

    [Fact]
    public async Task PlayerIsSentNowAndToTheNextConnector()
    {
        using var server = new OpenConnectServer(port: 0);
        var connected = new SemaphoreSlim(0);
        server.Connected += _ => connected.Release();
        server.Start();

        using var first = await ConnectAsync(server);
        Assert.True(await connected.WaitAsync(Wait));
        server.SendPlayer(leftHanded: true, "I7", 150);
        var p = (await ReadReplyAsync(first)).GetProperty("Player");
        Assert.Equal("LH", p.GetProperty("Handed").GetString());
        Assert.Equal("I7", p.GetProperty("Club").GetString());

        using var second = await ConnectAsync(server);   // replaces the first
        var again = await ReadReplyAsync(second);
        Assert.Equal(201, again.GetProperty("Code").GetInt32());
        Assert.Equal("I7", again.GetProperty("Player").GetProperty("Club").GetString());
    }

    [Fact]
    public async Task NewConnectionReplacesTheOldAndDisconnectIsReported()
    {
        using var server = new OpenConnectServer(port: 0);
        var connected = new SemaphoreSlim(0);
        var disconnected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Connected += _ => connected.Release();
        server.Disconnected += r => disconnected.TrySetResult(r);
        server.Start();

        using var first = await ConnectAsync(server);
        Assert.True(await connected.WaitAsync(Wait));
        var second = await ConnectAsync(server);
        Assert.True(await connected.WaitAsync(Wait));
        // The old one is closed by the server, without a "disconnected" (the new one is connected).
        using (var cts = new CancellationTokenSource(Wait))
            Assert.Equal(0, await first.GetStream().ReadAsync(new byte[16], cts.Token));
        Assert.False(disconnected.Task.IsCompleted);
        Assert.True(server.HasClient);

        second.Close();
        await Within(disconnected.Task);
        Assert.False(server.HasClient);
    }

    [Fact]
    public void PortInUseThrows()
    {
        using var a = new OpenConnectServer(port: 0);
        a.Start();
        using var b = new OpenConnectServer(port: a.Port);
        Assert.Throws<SocketException>(b.Start);
    }
}

public class OpenConnectSettingsTests
{
    [Fact]
    public void OlderSettingsLoadWithOpenConnectOff()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gg-oc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, Core.Storage.AppSettings.FileName), """{"AutoConnect": true}""");
            var s = Core.Storage.AppSettings.Load(dir);
            Assert.False(s.OpenConnect);
            Assert.Equal(921, s.OpenConnectPort);
            Assert.False(s.OpenConnectFromNetwork);
        }
        finally { Directory.Delete(dir, true); }
    }
}
