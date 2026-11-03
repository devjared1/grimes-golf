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

/// <summary>The courses the app offers. Mapped courses are loaded on first use, then kept.</summary>
public static class CourseLibrary
{
    /// <param name="Description">A line or two about the course, for the course list.</param>
    public sealed record Entry(string Name, string Description, Func<Course> Build)
    {
        private Course? _loaded;
        private readonly object _gate = new();

        /// <summary>The course, built the first time it's asked for (safe from any thread). Throws if it can't be built.</summary>
        public Course Load()
        {
            lock (_gate) return _loaded ??= Build();
        }
    }

    public static readonly IReadOnlyList<Entry> All = new[]
    {
        new Entry("Deer Run (Moulton, AL)",
            "18 holes mapped from OpenStreetMap with real elevation: rolling fairways, bunkers, water hazards and woods.",
            () => OsmCourse.FromResource("deer-run.osm", "Deer Run", "deer-run.dem")),
    };
}
