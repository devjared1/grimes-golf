namespace SquareGspro.Core.Course;

/// <summary>Three short test holes: a straight par 4, a par 3 over water, and a dog-leg-left par 5 with a creek.</summary>
public static class TestCourse
{
    // Holes are laid out in yards for readability and converted to metres.
    private static P2 Y(double x, double z) => new(x * Units.YardsToMetres, z * Units.YardsToMetres);
    private static double Yd(double v) => v * Units.YardsToMetres;
    private static Ellipse E(double x, double z, double rx, double rz, double rot = 0) => new(Y(x, z), Yd(rx), Yd(rz), rot);
    private static Strip S(double width, params (double X, double Z)[] pts) => new(pts.Select(p => Y(p.X, p.Z)).ToList(), Yd(width));

    public static readonly IReadOnlyList<Hole> Holes = new[]
    {
        new Hole(1, "The Opener", 4,
            Tee: Y(0, 0), Pin: Y(360, 10), TeeAim: Y(245, 0),
            Fairways: new Shape[] { S(35, (35, 0), (220, 0), (300, 6), (345, 10)) },
            Greens: new[] { E(362, 10, 17, 14) },
            Bunkers: new Shape[] { E(235, 22, 9, 6, 15), E(358, -10, 6, 4, 70), E(371, 28, 6, 4, -60) },
            Water: Array.Empty<Shape>(),
            Bounds: new Bounds(Y(-20, -55), Y(400, 55))),

        new Hole(2, "Over the Water", 3,
            Tee: Y(0, 0), Pin: Y(152, 3), TeeAim: Y(152, 3),
            Fairways: new Shape[] { S(26, (125, 2), (134, 2)) },
            Greens: new[] { E(150, 2, 14, 12) },
            Bunkers: new Shape[] { E(152, -15, 5, 4, 20), E(160, 17, 6, 4, -30) },
            Water: new Shape[] { E(85, -2, 38, 30) },
            Bounds: new Bounds(Y(-20, -50), Y(200, 50))),

        new Hole(3, "Dog Leg Left", 5,
            Tee: Y(0, 0), Pin: Y(462, -136), TeeAim: Y(240, 0),
            Fairways: new Shape[] { S(38, (40, 0), (240, 0), (330, -55), (415, -110), (445, -128)) },
            Greens: new[] { E(462, -138, 18, 15, 30) },
            Bunkers: new Shape[] { E(255, 22, 10, 6, 10), E(450, -158, 6, 4, 40), E(476, -118, 5, 4, -20) },
            Water: new Shape[] { S(9, (330, -170), (365, -100), (385, -45), (395, 10)) },
            Bounds: new Bounds(Y(-20, -220), Y(510, 60))),
    };

    public static readonly Course Course = new("Test holes", Holes);
}
