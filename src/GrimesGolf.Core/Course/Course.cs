namespace GrimesGolf.Core.Course;

/// <summary>
/// A set of holes to play. With <see cref="SharedLayout"/> the holes are parts of one mapped course: they share one
/// coordinate frame and the same feature lists, so the whole course is drawn once and neighbouring holes are in play.
/// Otherwise each hole is drawn on its own (the test holes all start at the origin).
/// </summary>
public sealed record Course(string Name, IReadOnlyList<Hole> Holes)
{
    public bool SharedLayout { get; init; }

    /// <summary>Area covered by the course (shared layouts), for the ground and scenery.</summary>
    public Bounds Extent { get; init; }

    /// <summary>Areas mapped as woods (shared layouts); trees are planted there.</summary>
    public IReadOnlyList<Shape> Woods { get; init; } = Array.Empty<Shape>();

    /// <summary>
    /// The real trees, if they were surveyed (shared layouts): every tree in and around the course. Null = not
    /// surveyed; the scenery plants its own.
    /// </summary>
    public IReadOnlyList<CourseTree>? Trees { get; init; }

    private TreeField? _obstacles;
    private IReadOnlyList<CourseTree>? _standing;

    /// <summary>
    /// The surveyed trees that stand on the course: all but those near a tee or just behind it, which are cleared
    /// so the camera there has a view. These are the trees drawn, and the only ones a ball can hit.
    /// </summary>
    public IReadOnlyList<CourseTree>? StandingTrees =>
        Trees == null ? null : _standing ??= Trees.Where(t => !Holes.Any(h => ClearedAroundTee(h, t.At))).ToList();

    /// <summary>The standing trees as things a ball can hit (null when they weren't surveyed: planted scenery isn't in play).</summary>
    public TreeField? Obstacles => StandingTrees is { } trees ? _obstacles ??= new TreeField(trees, Terrain) : null;

    /// <summary>Near the tee or just behind it, where the camera sits: no surveyed tree is kept here.</summary>
    public static bool ClearedAroundTee(Hole hole, P2 p)
    {
        var d = p - hole.Tee;
        var aim = hole.TeeAim - hole.Tee;
        double yaw = Math.Atan2(aim.Z, aim.X);
        double along = d.X * Math.Cos(yaw) + d.Z * Math.Sin(yaw), side = -d.X * Math.Sin(yaw) + d.Z * Math.Cos(yaw);
        return along is > -40 and < 25 && Math.Abs(side) < 30;
    }

    /// <summary>Ground heights, if elevation data was loaded (null = flat).</summary>
    public Terrain? Terrain { get; init; }

    /// <summary>Attribution to show for the map data, if any.</summary>
    public string? Credit { get; init; }

    /// <summary>Problems found while importing (and how they were handled).</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}
