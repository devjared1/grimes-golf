namespace SquareGspro.Core.Course;

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

    /// <summary>Ground heights, if elevation data was loaded (null = flat).</summary>
    public Terrain? Terrain { get; init; }

    /// <summary>Attribution to show for the map data, if any.</summary>
    public string? Credit { get; init; }

    /// <summary>Problems found while importing (and how they were handled).</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>The courses the app offers. Mapped courses are loaded on first use.</summary>
public static class CourseLibrary
{
    public sealed record Entry(string Name, Func<Course> Load);

    public static readonly IReadOnlyList<Entry> All = new[]
    {
        new Entry("Deer Run (Moulton, AL)", () => OsmCourse.FromResource("deer-run.osm", "Deer Run", "deer-run.dem")),
        new Entry("Test holes", () => TestCourse.Course),
    };
}
