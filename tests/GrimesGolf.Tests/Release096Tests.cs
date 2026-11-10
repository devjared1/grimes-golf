using GrimesGolf.Core;
using GrimesGolf.Core.Audio;
using GrimesGolf.Core.Course;
using GrimesGolf.Core.Square;
using Xunit;
using Xunit.Abstractions;

namespace GrimesGolf.Tests;

public class AlignmentProtocolTests
{
    private static byte[] Int32s(byte status, params int[] values)
    {
        var d = new byte[5 + values.Length * 4];
        d[0] = 0x11; d[1] = 0x04; d[2] = 0x09; d[3] = status;
        for (int i = 0; i < values.Length; i++) BitConverter.GetBytes(values[i]).CopyTo(d, 5 + i * 4);
        return d;
    }

    [Fact]
    public void Stick_readings_are_decoded()
    {
        var e = Assert.IsType<StickEvent>(SquarePacketParser.Parse(Int32s(1, -123, 2500, -700, 10)));
        Assert.Equal(StickStatus.Stable, e.Status);
        Assert.Equal(-1.23, e.AngleDeg, 3);
        Assert.Equal(25.0, e.X, 3);
        Assert.Equal(-7.0, e.Y, 3);
        Assert.Equal(0.1, e.Z, 3);
    }

    [Fact]
    public void Stick_commands_match_Square_software()
    {
        var c = new SquareCommandBuilder();
        Assert.Equal(new byte[] { 0x00, 0x81, 0x00, 0x02, 0x11, 0, 0, 0, 0 }, c.DetectStick());
        Assert.Equal(new byte[] { 0x00, 0x82, 0x01, 0x01, 0x08, 0x00, 0, 0, 0 }, c.ClubSelect(SquareClubMap.AimingStick, leftHanded: false));
        // -1.23 degrees is -123, little-endian.
        Assert.Equal(new byte[] { 0x00, 0x85, 0x02, 0x01, 0x85, 0xFF, 0xFF, 0xFF, 0 }, c.StickResult(confirm: true, -1.23));
        Assert.Equal(new byte[] { 0x00, 0x85, 0x03, 0x00, 0x2C, 0x01, 0x00, 0x00, 0 }, c.StickResult(confirm: false, 3));
    }

    [Theory]
    // Real packets from the first session on hardware 1.5: on a mat, and teed up.
    [InlineData("11012201011EE8FFFFBAE3FFFFCFF4FFFF", true, -61.14, -72.38, -28.65)]
    [InlineData("1101C7010133F0FFFF8FF4FFFF4B080000", true, -40.45, -29.29, 21.23)]
    [InlineData("11014500010AE4FFFF12C9FFFF81F3FFFF", false, -71.58, -140.62, -31.99)]
    public void Sensor_packets_give_the_ball_position(string hex, bool ready, double x, double y, double z)
    {
        var e = Assert.IsType<SensorEvent>(SquarePacketParser.Parse(Convert.FromHexString(hex)));
        Assert.Equal(ready, e.BallReady);
        Assert.True(e.BallDetected);
        Assert.Equal(new BallPosition(x, y, z), e.Position!.Value);
    }

    [Fact]
    public void No_ball_means_no_position()
    {
        var e = Assert.IsType<SensorEvent>(SquarePacketParser.Parse(Convert.FromHexString("1101C20000000000000000000000000000")));
        Assert.False(e.BallDetected);
        Assert.Null(e.Position);
    }
}

public class TreeHitTests
{
    private readonly ITestOutputHelper _output;
    public TreeHitTests(ITestOutputHelper output) => _output = output;

    /// <summary>A flat, wide-open hole; everything is fairway so only the trees can change the shot.</summary>
    private static Hole Open => new(1, "Open", 5,
        Tee: new P2(0, 0), Pin: new P2(500, 0), TeeAim: new P2(250, 0),
        Fairways: new Shape[] { new Ellipse(new P2(250, 0), 600, 300) },
        Greens: new[] { new Ellipse(new P2(500, 0), 15, 15) },
        Bunkers: Array.Empty<Shape>(), Water: Array.Empty<Shape>(), Bounds: new Bounds(new P2(-1000, -1000), new P2(1500, 1000)));

    private static RangeShot Drive => new(BallMph: 150, LaunchDeg: 12, DirectionDeg: 0, SpinRpm: 2800, SpinAxisDeg: 0, ClubMph: null, Smash: null) { Source = ShotSource.Monitor };

    private static ShotOutcome Play(params CourseTree[] trees)
    {
        var round = new HoleRound(new[] { Open }) { Trees = new TreeField(trees, null) };
        round.StartHole(0);
        return round.Play(Drive, putt: false, headingRad: 0);
    }

    [Fact]
    public void A_wall_of_trunks_stops_a_low_shot_short()
    {
        // A low running ball, with trunks across its line at 60 m.
        var shot = new RangeShot(BallMph: 90, LaunchDeg: 1, DirectionDeg: 0, SpinRpm: 1500, SpinAxisDeg: 0, ClubMph: null, Smash: null) { Source = ShotSource.Monitor };
        var free = new HoleRound(new[] { Open });
        free.StartHole(0);
        double clear = free.Play(shot, false, 0).Flight.TotalYards * Units.YardsToMetres;
        Assert.True(clear > 60, $"Reached {clear} m unblocked");

        var trunks = Enumerable.Range(-6, 13).Select(i => new CourseTree(new P2(60, i * 0.5), 12, 0.5, TreeKind.Single));
        var round = new HoleRound(new[] { Open }) { Trees = new TreeField(trunks, null) };
        round.StartHole(0);
        var o = round.Play(shot, false, 0);
        Assert.NotNull(o.Flight.TreeHitIndex);
        Assert.StartsWith("Off a tree.", o.Message);
        Assert.True(o.Rest.X < 60, $"Stopped at {o.Rest.X} m");
    }

    [Fact]
    public void Trees_off_the_line_change_nothing()
    {
        var plain = Play();
        var away = Play(new CourseTree(new P2(100, 80), 25, 8, TreeKind.Single), new CourseTree(new P2(200, -60), 25, 8, TreeKind.Dense));
        Assert.Null(away.Flight.TreeHitIndex);
        Assert.Equal(plain.Rest, away.Rest);
    }

    [Fact]
    public void A_ball_flies_over_a_short_tree()
    {
        var o = Play(new CourseTree(new P2(150, 0), 4, 2, TreeKind.Single));
        Assert.Null(o.Flight.TreeHitIndex);
    }

    [Fact]
    public void The_same_shot_meets_the_same_branches()
    {
        var woods = Enumerable.Range(0, 30).Select(i => new CourseTree(new P2(60 + i * 3, (i % 5 - 2) * 3), 30, 7, TreeKind.Dense)).ToArray();
        var a = Play(woods);
        var b = Play(woods);
        Assert.NotNull(a.Flight.TreeHitIndex);
        Assert.Equal(a.Rest, b.Rest);
        Assert.Equal(a.Flight.Points.Count, b.Flight.Points.Count);
    }

    [Fact]
    public void Time_keeps_moving_forward_through_a_tree_hit()
    {
        var woods = Enumerable.Range(0, 30).Select(i => new CourseTree(new P2(60 + i * 3, (i % 5 - 2) * 3), 30, 7, TreeKind.Dense)).ToArray();
        var f = Play(woods).Flight;
        for (int i = 1; i < f.Points.Count; i++) Assert.True(f.Points[i].T >= f.Points[i - 1].T, $"Time went back at point {i}");
        Assert.InRange(f.LandingIndex, 1, f.Points.Count - 1);
    }

    [Fact]
    public void Tee_shots_down_Deer_Run_rarely_hit_trees()
    {
        var course = CourseLibrary.All.Single(e => e.Name.StartsWith("Deer Run")).Load();
        Assert.NotNull(course.Obstacles);
        var drive = new RangeShot(BallMph: 150, LaunchDeg: 13, DirectionDeg: 0, SpinRpm: 2800, SpinAxisDeg: 0, ClubMph: null, Smash: null) { Source = ShotSource.Monitor };
        int hits = 0;
        for (int h = 0; h < course.Holes.Count; h++)
        {
            var round = new HoleRound(course.Holes, course.Terrain) { Trees = course.Obstacles };
            round.StartHole(h);
            var o = round.Play(drive, putt: false, round.DefaultAimHeading());
            if (o.Flight.TreeHitIndex != null) { hits++; _output.WriteLine($"Hole {course.Holes[h].Number}: {o.Message}"); }
        }
        _output.WriteLine($"{hits} of {course.Holes.Count} straight drives hit a tree");
        Assert.True(hits <= course.Holes.Count / 3, $"{hits} of {course.Holes.Count} straight drives hit a tree");
    }
}

public class SoundTests
{
    [Theory]
    [InlineData("DR", SoundEffect.Wood)]
    [InlineData("h4", SoundEffect.Wood)]
    [InlineData("W3", SoundEffect.Wood)]
    [InlineData("I7", SoundEffect.Iron)]
    [InlineData("PW", SoundEffect.Wedge)]
    [InlineData("LW", SoundEffect.Wedge)]
    [InlineData("PT", SoundEffect.Putt)]
    public void Each_club_has_its_sound(string club, SoundEffect sound) => Assert.Equal(sound, SoundSynth.ImpactFor(club));

    [Theory]
    [InlineData(SoundEffect.Wood)]
    [InlineData(SoundEffect.Iron)]
    [InlineData(SoundEffect.Wedge)]
    [InlineData(SoundEffect.Putt)]
    [InlineData(SoundEffect.Landing)]
    [InlineData(SoundEffect.Tree)]
    [InlineData(SoundEffect.Cup)]
    [InlineData(SoundEffect.Applause)]
    [InlineData(SoundEffect.Cheer)]
    [InlineData(SoundEffect.Ready)]
    public void Sounds_are_audible_unclipped_and_end_cleanly(SoundEffect effect)
    {
        var s = SoundSynth.Make(effect);
        Assert.True(s.Length > SoundSynth.SampleRate / 20);
        int peak = s.Max(v => Math.Abs((int)v));
        Assert.InRange(peak, short.MaxValue / 2, short.MaxValue - 1000);
        Assert.Equal(0, s[^1]);
        Assert.Equal(s, SoundSynth.Make(effect));
    }

    [Fact]
    public void Cheers_outlast_applause()
    {
        Assert.True(SoundSynth.Make(SoundEffect.Cheer).Length > SoundSynth.Make(SoundEffect.Applause).Length);
    }
}
