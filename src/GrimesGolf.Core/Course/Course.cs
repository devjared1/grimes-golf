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

    /// <summary>The surveyed trees as things a ball can hit (null when they weren't surveyed: planted scenery isn't in play).</summary>
    public TreeField? Obstacles => Trees == null ? null : _obstacles ??= new TreeField(Trees, Terrain);

    /// <summary>Ground heights, if elevation data was loaded (null = flat).</summary>
    public Terrain? Terrain { get; init; }

    /// <summary>Attribution to show for the map data, if any.</summary>
    public string? Credit { get; init; }

    /// <summary>Problems found while importing (and how they were handled).</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}
