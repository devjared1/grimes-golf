using GrimesGolf.Core.Square;
using Xunit;

namespace GrimesGolf.Tests;

public class PacketCaptureTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 19, 30, 0, TimeSpan.FromHours(-6));

    /// <summary>A ball-data notification: speed m/s, launch, direction, spin axis in hundredths; spins in rpm.</summary>
    private static byte[] Ball(byte seq, double ms, double launch, double dir, short spin, double axis)
    {
        var d = new byte[17];
        d[0] = 0x11; d[1] = 0x02; d[2] = seq;
        void Put(int at, int v) { d[at] = (byte)v; d[at + 1] = (byte)(v >> 8); }
        Put(3, (int)Math.Round(ms * 100)); Put(5, (int)Math.Round(launch * 100)); Put(7, (int)Math.Round(dir * 100));
        Put(9, spin); Put(11, (int)Math.Round(axis * 100)); Put(13, spin); Put(15, 0);
        return d;
    }

    /// <summary>A heartbeat reply saying the monitor is set to this club.</summary>
    private static byte[] Heartbeat(byte sel, byte number) => new byte[] { 0x11, 0x03, 0x00, 0x04, 0x00, sel, number, 0x00, 0x01 };

    private static CapturedPacket Rx(double sec, byte[] d) => new(T0.AddSeconds(sec), false, d);

    [Fact]
    public void Capture_round_trips_packets_and_notes()
    {
        string path = Path.Combine(Path.GetTempPath(), $"capture-{Guid.NewGuid():N}.jsonl");
        try
        {
            using (var w = new PacketCaptureWriter(path, new CaptureHeader(T0, "listen", "7 iron session")))
            {
                w.Write(new CapturedPacket(T0, true, new byte[] { 0x00, 0x83, 0x01 }));
                w.Note(T0.AddSeconds(1), "club I7");
                w.Write(Rx(2, Ball(1, 50, 18, -1.5, 6500, 4)));
            }
            File.AppendAllText(path, "{\"t\":\"2026-11-09T19:3");   // cut short by a crash

            var (header, packets, messages, notes) = PacketCapture.Read(path);
            Assert.Empty(messages);
            Assert.Equal("listen", header!.Mode);
            Assert.Equal("7 iron session", header.Note);
            Assert.Equal(T0, header.Started);
            Assert.Equal(2, packets.Count);
            Assert.True(packets[0].Outgoing);
            Assert.Equal("008301", packets[0].Hex);
            Assert.Equal(T0.AddSeconds(2), packets[1].At);
            Assert.Equal("club I7", Assert.Single(notes).Text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Shots_drop_repeated_notifications_and_carry_the_monitors_club()
    {
        var ball = Ball(5, 50, 18, -1.5, 6500, 4);
        var shots = PacketCapture.Shots(new[]
        {
            Rx(0, Heartbeat(6, 7)),
            Rx(1, ball), Rx(1.01, ball),                      // the same notification twice
            new CapturedPacket(T0.AddSeconds(2), true, ball), // something sent, not a shot
            Rx(5, Heartbeat(4, 1)),
            Rx(9, Ball(9, 70, 11, 2, 2700, -3)),
        });

        Assert.Equal(2, shots.Count);
        Assert.Equal((1, "I7"), (shots[0].Number, shots[0].ClubCode));
        Assert.Equal(50, shots[0].Ball.BallSpeedMs, 2);
        Assert.Equal(18, shots[0].Ball.LaunchDeg, 2);
        Assert.Equal(6500, shots[0].Ball.TotalSpinRpm);
        Assert.Equal((2, "DR"), (shots[1].Number, shots[1].ClubCode));
        Assert.Equal(-3, shots[1].Ball.SpinAxisDeg, 2);
    }

    [Fact]
    public void Club_data_attaches_to_the_shot_it_answers_only()
    {
        byte[] clubData = { 0x11, 0x07, 0x0f, 0x64, 0x00, 0x32, 0x00, 0x9c, 0xff, 0x10, 0x04 };   // path 1.00, face 0.50, attack -1.00, loft 10.40
        var shots = PacketCapture.Shots(new[]
        {
            Rx(0, clubData),                                   // replayed on connecting: no shot to attach to
            Rx(1, Ball(1, 50, 18, 0, 6500, 0)), Rx(1.5, clubData),
            Rx(10, Ball(2, 50, 18, 0, 6400, 0)), Rx(20, clubData.Append((byte)0).ToArray()),   // too late
        });
        Assert.Equal(1.0, shots[0].Club!.PathDeg!.Value, 2);
        Assert.Equal(10.4, shots[0].Club!.DynamicLoftDeg!.Value, 2);
        Assert.Null(shots[1].Club);
    }

    [Theory]
    [InlineData(6, 7, "I7")]
    [InlineData(0, 7, "I7")]   // swing-stick iron
    [InlineData(4, 1, "DR")]
    [InlineData(2, 1, "DR")]   // swing-stick driver
    [InlineData(5, 0x0e, "H4")]
    [InlineData(6, 0x0b, "GW")]
    [InlineData(7, 1, "PT")]
    [InlineData(8, 1, null)]   // the aiming stick
    public void Club_codes_read_back_from_the_monitor(byte sel, byte number, string? code) =>
        Assert.Equal(code, SquareClubMap.CodeFor(sel, number));

    [Theory]
    [InlineData("152 168 85 R4", 152.0, 168.0, 85.0, 4.0)]
    [InlineData("152,168,85,L4.5", 152.0, 168.0, 85.0, -4.5)]
    [InlineData("152 168 28y -2", 152.0, 168.0, 84.0, -2.0)]
    [InlineData("152 168 85ft", 152.0, 168.0, 85.0, null)]
    [InlineData("152 - 85", 152.0, null, 85.0, null)]
    [InlineData("230", 230.0, null, null, null)]
    public void Reference_numbers_read_as_typed(string text, double? carry, double? total, double? apex, double? offline)
    {
        Assert.True(ReferenceShot.TryParse(3, text, out var r, out _));
        Assert.Equal(new ReferenceShot(3, carry, total, apex, offline), r);
    }

    [Theory]
    [InlineData("")]
    [InlineData("long")]
    [InlineData("1 2 3 4 5")]
    public void Reference_numbers_that_do_not_read_are_refused(string text) =>
        Assert.False(ReferenceShot.TryParse(1, text, out _, out _));

    [Fact]
    public void Reference_file_keeps_the_last_line_for_a_shot()
    {
        string path = Path.Combine(Path.GetTempPath(), $"gspro-{Guid.NewGuid():N}.csv");
        try
        {
            File.WriteAllLines(path, new[]
            {
                ReferenceShot.CsvHeader,
                new ReferenceShot(1, 150, 160, 80, 2).ToCsv(),
                new ReferenceShot(2, 151, null, null, null).ToCsv(),
                new ReferenceShot(1, 152.5, 162, 81, -3).ToCsv(),   // a correction
            });
            var refs = ReferenceShot.ReadCsv(path);
            Assert.Equal(new ReferenceShot(1, 152.5, 162, 81, -3), refs[1]);
            Assert.Equal(new ReferenceShot(2, 151, null, null, null), refs[2]);
        }
        finally { File.Delete(path); }
    }
}
