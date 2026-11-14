using GrimesGolf.Core.Square;
using Xunit;

namespace GrimesGolf.Tests;

public class OpenConnectTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 21, 0, 0, TimeSpan.FromHours(-6));

    private static string Shot(int number, double mph, double vla, double hla, double spin, double axis, bool heartbeat = false, string club = "")
    {
        string I(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string B(bool b) => b ? "true" : "false";
        return "{\"DeviceID\":\"SquareGolf\",\"Units\":\"Yards\",\"ShotNumber\":" + number + ",\"APIversion\":\"1\"," +
               "\"BallData\":{\"Speed\":" + I(mph) + ",\"SpinAxis\":" + I(axis) + ",\"TotalSpin\":" + I(spin) + ",\"BackSpin\":" + I(spin) +
               ",\"SideSpin\":0,\"HLA\":" + I(hla) + ",\"VLA\":" + I(vla) + "}," +
               "\"ClubData\":" + (club == "" ? "{\"Speed\":0}" : club) + "," +
               "\"ShotDataOptions\":{\"ContainsBallData\":" + B(!heartbeat) + ",\"ContainsClubData\":" + B(club != "") +
               ",\"LaunchMonitorIsReady\":true,\"IsHeartBeat\":" + B(heartbeat) + "}}";
    }

    private static string Reply(string club) =>
        "{\"Code\":201,\"Message\":\"GSPro Player Information\",\"Player\":{\"Handed\":\"RH\",\"Club\":\"" + club + "\",\"DistanceToTarget\":150}}";

    private static CapturedMessage To(double sec, string json) => new(T0.AddSeconds(sec), true, json);
    private static CapturedMessage From(double sec, string json) => new(T0.AddSeconds(sec), false, json);

    [Fact]
    public void Framer_splits_objects_however_the_reads_fall()
    {
        var f = new JsonObjectFramer();
        Assert.Empty(f.Feed("{\"a\":{\"b\":\"}{\\\"\"},"));
        var got = f.Feed("\"c\":1}\r\n{\"d\":2}{\"e\"");
        Assert.Equal(new[] { "{\"a\":{\"b\":\"}{\\\"\"},\"c\":1}", "{\"d\":2}" }, got);
        Assert.Equal(new[] { "{\"e\":3}" }, f.Feed(":3}"));
    }

    [Fact]
    public void Shots_come_from_ball_data_sent_to_gspro_once_each()
    {
        var shots = PacketCapture.Shots(new[]
        {
            From(0, Reply("I7")),
            To(1, Shot(0, 0, 0, 0, 0, 0, heartbeat: true)),
            To(2, Shot(1, 115.2, 17.5, -1.2, 6800, 3.5)),
            To(3, Shot(1, 115.2, 17.5, -1.2, 6800, 3.5)),   // resent: GSPro was slow to answer
            From(3.1, """{"Code":200,"Message":"Shot received successfully","Player":{"Handed":"RH","Club":"I7"}}"""),
            From(10, Reply("DR")),
            To(20, Shot(2, 160, 11, 2, 2600, -4, club: """{"Speed":110,"Path":2.5,"FaceToTarget":-1,"AngleOfAttack":3,"Loft":13}""")),
        });

        Assert.Equal(2, shots.Count);
        var s = shots[0];
        Assert.Equal((1, "I7"), (s.Number, s.ClubCode));
        Assert.Equal(115.2, s.Ball.BallSpeedMs * GrimesGolf.Core.Course.Units.MsToMph, 6);
        Assert.Equal(17.5, s.Ball.LaunchDeg);
        Assert.Equal(-1.2, s.Ball.DirectionDeg);
        Assert.Equal(6800, s.Ball.TotalSpinRpm);
        Assert.Equal(-3.5, s.Ball.SpinAxisDeg);   // back to the monitor's sign: negative = curves right
        Assert.Null(s.Club);
        Assert.NotNull(s.Json);

        Assert.Equal("DR", shots[1].ClubCode);
        Assert.Equal(2.5, shots[1].Club!.PathDeg);
        Assert.Equal(110, shots[1].Club!.ClubSpeedMs!.Value * GrimesGolf.Core.Course.Units.MsToMph, 6);
    }

    /// <summary>The messages Square's connector really sends (2026-11-09 capture): every one is flagged a heartbeat,
    /// and the club data follows on its own under the same shot number, with no club speed.</summary>
    [Fact]
    public void Square_connector_shots_are_flagged_heartbeats_and_send_club_data_after()
    {
        const string zeros = "\"ClubData\":{\"Speed\":0.0,\"AngleOfAttack\":0.0,\"FaceToTarget\":0.0,\"Loft\":0.0,\"Path\":0.0}";
        const string ball = "\"BallData\":{\"Speed\":123.344864,\"SpinAxis\":7.49,\"TotalSpin\":6448.0,\"BackSpin\":6392.0,\"SideSpin\":841.0,\"HLA\":0.03,\"VLA\":23.43,\"CarryDistance\":0.0}";
        const string club = "\"ClubData\":{\"Speed\":0.0,\"AngleOfAttack\":-1.77,\"FaceToTarget\":0.92,\"Lie\":0.0,\"Loft\":31.9,\"Path\":-3.51}";
        string Msg(int n, string b, string c, bool hasBall, bool hasClub, bool ready) =>
            "{\"DeviceID\":\"SquareGolf\",\"Units\":\"Yards\",\"ShotNumber\":" + n + ",\"APIversion\":\"1\"," + b + "," + c +
            ",\"ShotDataOptions\":{\"ContainsBallData\":" + (hasBall ? "true" : "false") + ",\"ContainsClubData\":" + (hasClub ? "true" : "false") +
            ",\"LaunchMonitorIsReady\":" + (ready ? "true" : "false") + ",\"LaunchMonitorBallDetected\":true,\"IsHeartBeat\":" + (hasBall || hasClub ? "true" : "false") + "}}";
        string emptyBall = ball.Replace("123.344864", "0.0");

        var shots = PacketCapture.Shots(new[]
        {
            To(0, Msg(2, emptyBall, zeros, hasBall: true, hasClub: false, ready: false)),   // the hello: ball data of zeros
            From(0.1, Reply("I7")),
            To(5, Msg(2, emptyBall, zeros, hasBall: false, hasClub: false, ready: true)),   // status
            To(10, Msg(3, ball, zeros, hasBall: true, hasClub: false, ready: true)),
            To(10.8, Msg(3, ball, club, hasBall: false, hasClub: true, ready: false)),
            To(12, Msg(3, ball, club, hasBall: false, hasClub: false, ready: true)),          // status, still echoing the last shot
        });

        var s = Assert.Single(shots);
        Assert.Equal("I7", s.ClubCode);
        Assert.Equal(23.43, s.Ball.LaunchDeg);
        Assert.Equal(-7.49, s.Ball.SpinAxisDeg);
        Assert.NotNull(s.Club);
        Assert.Equal(-3.51, s.Club!.PathDeg);
        Assert.Equal(31.9, s.Club.DynamicLoftDeg);
        Assert.Null(s.Club.ClubSpeedMs);
    }

    [Fact]
    public void A_club_named_only_in_the_answer_goes_with_the_shot()
    {
        var shots = PacketCapture.Shots(new[]
        {
            To(1, Shot(1, 100, 20, 0, 7000, 0)),
            From(1.2, """{"Code":200,"Player":{"Handed":"RH","Club":"pw"}}"""),
        });
        Assert.Equal("PW", Assert.Single(shots).ClubCode);
    }

    [Fact]
    public void A_relay_capture_round_trips_and_flies_from_its_messages()
    {
        string path = Path.Combine(Path.GetTempPath(), $"relay-{Guid.NewGuid():N}.jsonl");
        try
        {
            using (var w = new PacketCaptureWriter(path, new CaptureHeader(T0, "gspro-relay", null)))
            {
                w.Write(From(0, Reply("I7")));
                w.Write(To(2, Shot(1, 115.2, 17.5, -1.2, 6800, 3.5)));
            }
            var cap = PacketCapture.Read(path);
            Assert.Equal(2, cap.Messages.Count);
            Assert.True(cap.Messages[1].ToGsPro);
            Assert.Equal(T0.AddSeconds(2), cap.Messages[1].At);
            var shot = Assert.Single(cap.Shots());
            Assert.Equal("I7", shot.ClubCode);
        }
        finally { File.Delete(path); }
    }
}
