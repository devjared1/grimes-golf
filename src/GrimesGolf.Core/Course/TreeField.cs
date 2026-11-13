namespace GrimesGolf.Core.Course;

/// <summary>Where a shot met a tree: on the path's segment from <see cref="Index"/>, at this point and moving at this velocity (m/s).</summary>
public readonly record struct TreeContact(int Index, FlightPoint At, double VelocityX, double VelocityY, double VelocityZ, bool Trunk, P2 TreeAt);

/// <summary>
/// The course's trees as things a ball can hit, shaped as they're drawn (<see cref="TreeShape"/>: a conifer narrows
/// to its tip, a broadleaf crown sits high on its trunk), so a ball only meets a tree where the tree is seen to be. A
/// trunk stops whatever reaches it. A crown is mostly air, so a ball flying through has a chance of meeting a branch
/// that grows with how far it travels inside: a clip of the edge usually gets through, the full width of a big tree
/// usually doesn't, and woods are thicker than a lone tree. The chance comes from the caller's random numbers, so the
/// same shot can be played back the same way.
/// </summary>
public sealed class TreeField
{
    private const double CellSize = 25;
    /// <summary>Distance a ball flies through a crown, on average, before meeting a branch (m).</summary>
    private const double OpenCrownReach = 8, DenseCrownReach = 5;
    /// <summary>How finely a path is checked (m). Crowns are metres across and trunks are checked by closest approach.</summary>
    private const double Step = 0.5;
    /// <summary>A tree this close to where the ball was played from is the player's problem, not an obstacle (m): no stance is that exact.</summary>
    private const double ClearOfStart = 1.5;
    /// <summary>The ball is off the clubface this far before a crown can catch it (m): hanging branches are not modelled.</summary>
    private const double CrownClearOfStart = 3;

    /// <param name="Base">Height of the foot of the drawn tree (its ground less <see cref="TreeShape.Sunk"/>).</param>
    /// <param name="Crown">How far the crown reaches at its widest (m), for finding the trees near a point.</param>
    private readonly record struct Tree(CourseTree Source, P2 At, double Base, double Height, double Crown, bool Conifer, double Width,
                                        double CrownBottom, double TrunkRadius, double Reach);

    private readonly Dictionary<(int, int), List<Tree>> _cells = new();

    public TreeField(IEnumerable<CourseTree> trees, Terrain? terrain)
    {
        foreach (var t in trees)
        {
            if (t.Height <= 0 || t.Crown <= 0) continue;
            bool conifer = TreeShape.IsConifer(t);
            double width = TreeShape.Width(t, conifer);
            double widest = width * (conifer ? TreeShape.ConiferReach : TreeShape.BroadleafReach);
            var tree = new Tree(t, t.At, (terrain?.HeightAt(t.At) ?? 0) - TreeShape.Sunk, t.Height, widest, conifer, width,
                TreeShape.CrownBottom(t, conifer), Math.Clamp(t.Height * 0.015, 0.08, 0.4), t.Kind == TreeKind.Dense ? DenseCrownReach : OpenCrownReach);
            var (x0, z0) = Cell(new P2(t.At.X - widest, t.At.Z - widest));
            var (x1, z1) = Cell(new P2(t.At.X + widest, t.At.Z + widest));
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    if (!_cells.TryGetValue((x, z), out var cell)) _cells[(x, z)] = cell = new List<Tree>();
                    cell.Add(tree);
                }
        }
    }

    private static (int, int) Cell(P2 p) => ((int)Math.Floor(p.X / CellSize), (int)Math.Floor(p.Z / CellSize));

    /// <summary>The first tree this path meets, if any. <paramref name="start"/> is where the shot was played from.</summary>
    public TreeContact? FirstContact(IReadOnlyList<FlightPoint> path, P2 start, Random random)
    {
        for (int i = 0; i + 1 < path.Count; i++)
        {
            var a = path[i];
            var b = path[i + 1];
            double dt = b.T - a.T;
            if (dt <= 0) continue;
            double length = Math.Sqrt(Sq(b.X - a.X) + Sq(b.Y - a.Y) + Sq(b.Z - a.Z));
            if (length < 1e-6) continue;
            int steps = Math.Max(1, (int)Math.Ceiling(length / Step));
            double ds = length / steps;
            for (int s = 1; s <= steps; s++)
            {
                double f = (double)s / steps;
                var at = new FlightPoint(a.T + dt * f, a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f, a.Z + (b.Z - a.Z) * f);
                var flat = new P2(at.X, at.Z);
                if (!_cells.TryGetValue(Cell(flat), out var cell)) continue;
                double fromStart = flat.DistanceTo(start);
                foreach (var tree in cell)
                {
                    if (tree.At.DistanceTo(start) < ClearOfStart) continue;
                    if (!Meets(tree, flat, at.Y, ds, fromStart, random, out bool trunk)) continue;
                    return new TreeContact(i, at, (b.X - a.X) / dt, (b.Y - a.Y) / dt, (b.Z - a.Z) / dt, trunk, tree.At);
                }
            }
        }
        return null;
    }

    private static bool Meets(in Tree t, P2 flat, double y, double ds, double fromStart, Random random, out bool trunk)
    {
        double across = flat.DistanceTo(t.At), up = y - t.Base;
        trunk = false;
        // (A rolling ball can sit a little under the tree's own ground height on a slope.)
        if (up < 0 || up > t.Height || across > t.Crown) return false;
        // Half a step's grace, so a fast ball can't slip past a trunk between two checks.
        if (up < t.CrownBottom) { trunk = across <= t.TrunkRadius + Step / 2; return trunk; }
        if (fromStart < CrownClearOfStart) return false;
        if (across > TreeShape.CrownRadiusAt(t.Source, t.Conifer, t.Width, up)) return false;
        return random.NextDouble() < 1 - Math.Exp(-ds / t.Reach);
    }

    private static double Sq(double v) => v * v;
}
