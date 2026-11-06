using GrimesGolf.Core.Course;

// Checks course folders and gets them ready to publish (used by tools/make-course.ps1 and tools/publish-courses.ps1):
//   check <course folder>
//   publish <out folder> <folder of courses>...
//   merge <out folder> <live catalog.json, or "none"> [id to remove]...
//   anonymise <map.osm> <name in the map> <name to publish it under>
switch (args)
{
    case ["check", var folder]:
        return Check(folder);
    case ["publish", var outFolder, .. var roots] when roots.Length > 0:
        return Publish(outFolder, roots);
    case ["merge", var outFolder, var liveCatalog, .. var remove]:
        return Merge(outFolder, liveCatalog, remove);
    case ["anonymise", var map, var mappedName, var publicName]:
        return Anonymise(map, mappedName, publicName);
    default:
        Console.Error.WriteLine("Usage: check <course folder>  |  publish <out folder> <folder of courses>...  |  merge <out folder> <live catalog.json, or none> [id to remove]...  |  anonymise <map.osm> <name in the map> <name to publish it under>");
        return 2;
}

static int Anonymise(string map, string mappedName, string publicName)
{
    try
    {
        OsmAnonymiser.Rewrite(map, mappedName, publicName);
        return 0;
    }
    catch (Exception ex) when (ex is InvalidDataException or IOException or System.Xml.XmlException)
    {
        Console.Error.WriteLine($"{map}: {ex.Message}");
        return 1;
    }
}

// Turns what was just built in the out folder into what to upload: its catalog becomes the live one plus these courses,
// and only the zips that need uploading are left beside it.
static int Merge(string outFolder, string liveCatalog, string[] remove)
{
    string catalog = Path.Combine(outFolder, CourseCatalog.FileName);
    try
    {
        var live = liveCatalog == "none" ? Array.Empty<CatalogCourse>() : CourseCatalog.Parse(File.ReadAllBytes(liveCatalog));
        var built = CourseCatalog.Parse(File.ReadAllBytes(catalog));
        foreach (var id in remove.Where(id => live.All(c => c.Id != id) && built.All(c => c.Id != id)))
        {
            Console.Error.WriteLine($"There's no course '{id}' to remove.");
            return 1;
        }
        var (courses, upload) = CourseCatalog.Merge(live, built, remove);

        foreach (var b in built.Where(b => upload.All(u => u.Id != b.Id)))
        {
            File.Delete(Path.Combine(outFolder, b.File));
            var current = live.FirstOrDefault(c => c.Id == b.Id);
            if (current != null && !remove.Contains(b.Id)) Console.WriteLine($"{b.Id}: version {current.Version} is already up, so this version {b.Version} isn't uploaded (raise the version in course.json to replace it).");
        }
        foreach (var u in upload) Console.WriteLine($"{u.Id}: uploading version {u.Version}.");
        foreach (var c in courses.Where(c => built.All(b => b.Id != c.Id))) Console.WriteLine($"{c.Id}: not on this PC; version {c.Version} stays on the list.");
        foreach (var id in remove) Console.WriteLine($"{id}: taking it off the list.");
        File.WriteAllText(catalog, CourseCatalog.ToJson(courses));
        return 0;
    }
    catch (Exception ex) when (ex is InvalidDataException or IOException)
    {
        Console.Error.WriteLine($"Couldn't work out the new course list: {ex.Message}");
        return 1;
    }
}

static int Check(string folder)
{
    CoursePack pack;
    Course course;
    try
    {
        pack = CoursePack.Read(folder);
        course = pack.Load();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"{folder}: this course doesn't load. {ex.Message}");
        return 1;
    }
    Console.WriteLine($"{pack.Title}, version {pack.Version}");
    Console.WriteLine($"  {course.Holes.Count} holes, par {course.Holes.Sum(h => h.Par)}, {course.Holes.Sum(h => h.LengthYards):#,0} yd");
    Console.WriteLine($"  Elevation: {(course.Terrain == null ? "none (flat)" : $"yes, {course.Terrain.Datum:0} m above sea level")}");
    Console.WriteLine($"  Trees: {(course.Trees == null ? "not surveyed (the scenery plants its own)" : $"{course.Trees.Count:#,0} surveyed")}");
    foreach (var h in course.Holes)
    {
        // A par that doesn't fit the length usually means a mistake in the map: worth a look on openstreetmap.org.
        bool odd = h.Par switch { 3 => h.LengthYards > 280, 4 => h.LengthYards is < 230 or > 520, 5 => h.LengthYards < 400, _ => true };
        Console.WriteLine($"  Hole {h.Number,2}: par {h.Par}, {h.LengthYards,4:0} yd{(odd ? "   <-- check this par against the scorecard" : "")}");
    }
    if (course.Notes.Count > 0) Console.WriteLine("  What the import fixed or generated:");
    foreach (var note in course.Notes) Console.WriteLine($"    {note}");
    Console.WriteLine($"  Fingerprint: {CourseFingerprint.Of(course)}");
    return 0;
}

static int Publish(string outFolder, string[] roots)
{
    var (packs, problems) = CourseLibrary.Find(roots);
    foreach (var problem in problems) Console.Error.WriteLine(problem);
    if (problems.Count > 0) return 1;
    var entries = new List<CatalogCourse>();
    foreach (var pack in packs)
    {
        var entry = CourseStore.Publish(pack, pack.Load(), outFolder);
        entries.Add(entry);
        Console.WriteLine($"{entry.File}: {pack.Title}, {entry.Bytes / 1e6:0.0} MB, {entry.Holes} holes");
    }
    File.WriteAllText(Path.Combine(outFolder, CourseCatalog.FileName), CourseCatalog.ToJson(entries));
    return 0;
}
