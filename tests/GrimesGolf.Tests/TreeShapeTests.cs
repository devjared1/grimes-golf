using GrimesGolf.Core;
using GrimesGolf.Core.Course;
using Xunit;

namespace GrimesGolf.Tests;

/// <summary>A ball meets a tree only where the tree is drawn (<see cref="TreeShape"/>), and only trees that are drawn.</summary>
public class TreeShapeTests
{
    /// <summary>A tall, slender tree (20 m, crown 4 m) somewhere it's drawn as a conifer.</summary>
    private static CourseTree Pine()
    {
        for (int i = 0; ; i++)
        {
            var t = new CourseTree(new P2(100, i * 0.37), 20, 4, TreeKind.Dense);
            if (TreeShape.IsConifer(t)) return t;
        }
    }

    /// <summary>A level flight from x = 0 to 200 m, <paramref name="side"/> m to the side of the line, at this height.</summary>
    private static FlightPoint[] Level(double side, double height) =>
        Enumerable.Range(0, 41).Select(i => new FlightPoint(i * 0.1, i * 5.0, height, side)).ToArray();

    [Fact]
    public void A_ball_passing_close_to_a_pines_narrow_top_gets_by()
    {
        var pine = Pine();
        var field = new TreeField(new[] { pine }, null);
        // 14 m up the tree is drawn about 1.3 m wide; the old crown shape reached nearly 4 m there.
        double up = 14 - TreeShape.Sunk;
        Assert.InRange(TreeShape.CrownRadiusAt(pine, true, TreeShape.Width(pine, true), 14), 1, 1.6);
        var path = Level(pine.At.Z + 2.5, up);
        for (int seed = 0; seed < 200; seed++)
            Assert.Null(field.FirstContact(path, new P2(0, 0), new Random(seed)));
    }

    [Fact]
    public void A_ball_through_a_pines_needles_can_still_be_caught()
    {
        var pine = Pine();
        var field = new TreeField(new[] { pine }, null);
        var path = Level(pine.At.Z + 0.6, 8);   // low in the crown, where it's widest
        int caught = Enumerable.Range(0, 200).Count(seed => field.FirstContact(path, new P2(0, 0), new Random(seed)) != null);
        Assert.True(caught > 20, $"Caught {caught} of 200");
    }

    [Fact]
    public void The_same_tree_is_the_same_kind_every_time()
    {
        var t = new CourseTree(new P2(123.456, -78.9), 15, 4, TreeKind.Single);
        bool first = TreeShape.IsConifer(t);
        for (int i = 0; i < 10; i++) Assert.Equal(first, TreeShape.IsConifer(t));
    }

    [Fact]
    public void Trees_cleared_round_a_tee_are_not_in_play()
    {
        var hole = new Hole(1, "Open", 4, Tee: new P2(0, 0), Pin: new P2(300, 0), TeeAim: new P2(200, 0),
            Fairways: Array.Empty<Shape>(), Greens: new[] { new Ellipse(new P2(300, 0), 15, 15) },
            Bunkers: Array.Empty<Shape>(), Water: Array.Empty<Shape>(), Bounds: new Bounds(new P2(-100, -100), new P2(400, 100)));
        var nearTee = new CourseTree(new P2(10, 5), 15, 4, TreeKind.Single);
        var downTheHole = new CourseTree(new P2(150, 20), 15, 4, TreeKind.Single);
        var course = new Course("Test", new[] { hole }) { SharedLayout = true, Trees = new[] { nearTee, downTheHole } };
        Assert.Equal(new[] { downTheHole }, course.StandingTrees);
        // A low shot straight through where the hidden tree was isn't stopped by it.
        var path = Enumerable.Range(0, 21).Select(i => new FlightPoint(i * 0.05, i * 1.0, 3, 5)).ToArray();
        Assert.Null(course.Obstacles!.FirstContact(path, new P2(0, 0), new Random(1)));
    }
}
