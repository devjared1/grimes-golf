using System.Text.Json;
using System.Text.RegularExpressions;

namespace GrimesGolf.Core.Course;

/// <summary>
/// A course as a folder of files: <c>course.json</c> (who it is), <c>map.osm</c> (the OpenStreetMap data), and optionally
/// <c>ground.dem</c> (<see cref="ElevationGrid"/>) and <c>trees.csv.gz</c> (<see cref="TreeSurvey"/>). Made by
/// <c>tools/make-course.ps1</c>.
/// </summary>
/// <param name="Id">Lower-case letters, digits and hyphens: the course's name on disk and in the catalog.</param>
/// <param name="Name">The course's name, as mapped (it picks out the course's outline in the map).</param>
/// <param name="Place">Where it is ("Moulton, AL"), if that's known.</param>
/// <param name="Version">Goes up each time the course is published again: the highest version on this PC is the one played.</param>
public sealed partial record CoursePack(string Folder, string Id, string Name, string? Place, string Description, int Version)
{
    public const string ManifestFile = "course.json", MapFile = "map.osm", GroundFile = "ground.dem", TreesFile = "trees.csv.gz";

    /// <summary>The name on the course list, and the one saved rounds and online lobbies know the course by.</summary>
    public string Title => Place == null ? Name : $"{Name} ({Place})";

    public static bool ValidId(string? id) => id != null && IdPattern().IsMatch(id);

    /// <summary>Reads a course folder's manifest. Throws <see cref="InvalidDataException"/> if it isn't a usable course.</summary>
    public static CoursePack Read(string folder)
    {
        string manifest = Path.Combine(folder, ManifestFile);
        if (!File.Exists(manifest)) throw new InvalidDataException($"No {ManifestFile}.");
        if (!File.Exists(Path.Combine(folder, MapFile))) throw new InvalidDataException($"No {MapFile}.");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(manifest));
            var root = doc.RootElement;
            string? Text(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
            string id = Text("id") ?? throw new InvalidDataException("It has no id.");
            if (!ValidId(id)) throw new InvalidDataException($"Its id '{id}' isn't lower-case letters, digits and hyphens.");
            string name = Text("name") ?? throw new InvalidDataException("It has no name.");
            int version = root.TryGetProperty("version", out var ver) && ver.TryGetInt32(out int n) && n > 0
                ? n : throw new InvalidDataException("It has no version.");
            return new CoursePack(folder, id, name, Text("place"), Text("description") ?? "", version);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidDataException($"{ManifestFile} can't be read: {ex.Message}");
        }
    }

    /// <summary>Builds the course from its files.</summary>
    public Course Load() => Load(ground: true, trees: true, shapeWater: true);

    /// <param name="shapeWater">False leaves ponds as the elevation data has them (tests check the map against them).</param>
    internal Course Load(bool ground, bool trees, bool shapeWater)
    {
        using var map = File.OpenRead(Path.Combine(Folder, MapFile));
        return OsmCourse.Load(map, Name, ground ? Optional(GroundFile, ElevationGrid.Load) : null, shapeWater, trees ? Optional(TreesFile, TreeSurvey.Load) : null);
    }

    private T? Optional<T>(string file, Func<Stream, T> read) where T : class
    {
        string path = Path.Combine(Folder, file);
        if (!File.Exists(path)) return null;
        using var s = File.OpenRead(path);
        return read(s);
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex IdPattern();
}

/// <summary>The courses on this PC: the ones that came with the app and any downloaded since. Each is loaded on first use, then kept.</summary>
public static class CourseLibrary
{
    /// <summary>A course on the list (<see cref="Pack"/> says where its files are).</summary>
    public sealed class Entry(CoursePack pack)
    {
        private Course? _loaded;
        private readonly object _gate = new();

        public CoursePack Pack { get; } = pack;
        public string Name => Pack.Title;
        public string Description => Pack.Description;

        /// <summary>The course, built the first time it's asked for (safe from any thread). Throws if it can't be built.</summary>
        public Course Load()
        {
            lock (_gate) return _loaded ??= Pack.Load();
        }
    }

    private static volatile IReadOnlyList<Entry>? _all;

    /// <summary>The courses by name. Until <see cref="Scan"/> is called: those in a <c>courses</c> folder beside the program.</summary>
    public static IReadOnlyList<Entry> All
    {
        get
        {
            if (_all == null) Scan(Path.Combine(AppContext.BaseDirectory, "courses"));
            return _all!;
        }
    }

    /// <summary>
    /// Looks through these folders, each holding a folder per course, and makes that the list. If a course is in more
    /// than one the highest version wins (the earlier folder on a tie). A course that hasn't changed keeps what's loaded.
    /// </summary>
    /// <returns>What's wrong with any folders that were skipped.</returns>
    public static IReadOnlyList<string> Scan(params string[] folders)
    {
        var (packs, problems) = Find(folders);
        var old = _all ?? Array.Empty<Entry>();
        _all = packs.Select(p => old.FirstOrDefault(e => e.Pack == p) ?? new Entry(p)).ToList();
        return problems;
    }

    internal static (IReadOnlyList<CoursePack> Packs, IReadOnlyList<string> Problems) Find(params string[] folders)
    {
        var problems = new List<string>();
        var found = new Dictionary<string, CoursePack>();
        foreach (var root in folders.Where(Directory.Exists))
            foreach (var folder in Directory.GetDirectories(root).OrderBy(f => f, StringComparer.Ordinal))
            {
                if (Path.GetFileName(folder).StartsWith('.')) continue;   // (an install in progress)
                try
                {
                    var pack = CoursePack.Read(folder);
                    if (!found.TryGetValue(pack.Id, out var other) || pack.Version > other.Version) found[pack.Id] = pack;
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    problems.Add($"Skipped the course in {folder}: {ex.Message}");
                }
            }
        return (found.Values.OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase).ToList(), problems);
    }
}
