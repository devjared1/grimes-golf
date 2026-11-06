using GrimesGolf.Core.Course;

// Checks course folders and gets them ready to publish (used by tools/make-course.ps1 and tools/publish-courses.ps1):
//   check <course folder>
//   publish <out folder> <folder of courses>...
switch (args)
{
    case ["check", var folder]:
        return Check(folder);
    case ["publish", var outFolder, .. var roots] when roots.Length > 0:
        return Publish(outFolder, roots);
    default:
        Console.Error.WriteLine("Usage: check <course folder>  |  publish <out folder> <folder of courses>...");
        return 2;
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
