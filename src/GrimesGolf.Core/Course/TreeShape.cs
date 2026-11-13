namespace GrimesGolf.Core.Course;

/// <summary>
/// How a surveyed tree is drawn, shared by the scenery (which builds it) and <see cref="TreeField"/> (which flies balls
/// past it), so a ball is only stopped by a tree where the tree is seen to be. A tree is a conifer (stacked tiers of
/// needles, widest at the bottom and coming to a point) or broadleaf (a rounded crown high on the trunk). The meshes
/// are one unit tall and <see cref="Width"/> wide; the numbers here follow them (see <c>TreeFactory</c>).
/// </summary>
public static class TreeShape
{
    /// <summary>How far a crown reaches from the trunk, as a share of the mesh's width.</summary>
    public const double ConiferReach = 0.35, BroadleafReach = 0.43;

    /// <summary>Trees are planted this far into the ground (m), so they never stand on air on a slope.</summary>
    public const double Sunk = 0.3;

    /// <summary>A conifer's tiers start at this share of its height; the tip is at the top.</summary>
    private const double ConiferBottom = 0.16, ConiferTierSpan = 0.66, ConiferTierTop = 0.96;

    /// <summary>A broadleaf crown: an egg round this share of the height, this tall either way.</summary>
    private const double BroadleafMiddle = 0.68, BroadleafHalfHeight = 0.35;

    /// <summary>
    /// Whether a tree is drawn as a conifer: tall, narrow trees mostly are (pines are about 0.15-0.3 wide for their
    /// height, oaks 0.35 and more). Decided from where the tree stands, so every copy of the game (and every run) agrees.
    /// </summary>
    public static bool IsConifer(CourseTree t)
    {
        double slender = t.Crown / Math.Max(t.Height, 0.1);
        double chance = Math.Clamp((0.42 - slender) / 0.22, 0.1, 0.9);
        return Unit(t.At) < chance;
    }

    /// <summary>The mesh's width (m): what makes its crown reach as far as measured, within sensible proportions.</summary>
    public static double Width(CourseTree t, bool conifer) =>
        Math.Clamp(t.Crown / (conifer ? ConiferReach : BroadleafReach), t.Height * 0.45, t.Height * 1.2);

    /// <summary>
    /// How far the drawn crown reaches from the trunk at <paramref name="up"/> metres above the tree's base (its
    /// ground less <see cref="Sunk"/>); 0 where there is no crown at that height.
    /// </summary>
    public static double CrownRadiusAt(CourseTree t, bool conifer, double width, double up)
    {
        double y = up / t.Height;   // share of the height
        if (conifer)
        {
            if (y < ConiferBottom || y > 1) return 0;
            // The tiers' rims, from the widest at the bottom; above the top tier's rim it narrows to the tip.
            double k = Math.Min(1, (y - ConiferBottom) / ConiferTierSpan);
            double rim = 0.33 * Math.Pow(1 - k * 0.92, 0.9) + 0.02;
            if (y > ConiferTierTop) rim *= (1 - y) / (1 - ConiferTierTop);
            return rim * width;
        }
        double v = (y - BroadleafMiddle) / BroadleafHalfHeight;
        return Math.Abs(v) >= 1 ? 0 : BroadleafReach * width * Math.Sqrt(1 - v * v);
    }

    /// <summary>Lowest point of the crown (m above the tree's base): below it there's only the trunk.</summary>
    public static double CrownBottom(CourseTree t, bool conifer) =>
        t.Height * (conifer ? ConiferBottom : BroadleafMiddle - BroadleafHalfHeight);

    /// <summary>A number in [0, 1) from a point, the same on every machine (the point is rounded to the centimetre).</summary>
    private static double Unit(P2 p)
    {
        ulong h = (ulong)(long)Math.Round(p.X * 100) * 0x9E3779B97F4A7C15UL ^ (ulong)(long)Math.Round(p.Z * 100) * 0xC2B2AE3D27D4EB4FUL;
        h ^= h >> 33;
        h *= 0xFF51AFD7ED558CCDUL;
        h ^= h >> 33;
        return (h >> 11) / (double)(1UL << 53);
    }
}
