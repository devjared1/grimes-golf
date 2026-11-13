using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using GrimesGolf.Core;
using GrimesGolf.Core.Course;
using Xunit;
using CourseDef = GrimesGolf.Core.Course.Course;

namespace GrimesGolf.Tests;

public sealed class CourseStoreTests : IDisposable
{
    private const string Catalog = "https://courses.test/";
    private static CoursePack DeerRun => CourseLibrary.All.Single(e => e.Pack.Id == "deer-run").Pack;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "grimes-store-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, byte[]> _served = new();

    public CourseStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class Server(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) =>
            Task.FromResult(files.TryGetValue(request.RequestUri!.AbsolutePath.TrimStart('/'), out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private string Store => Path.Combine(_root, "store");

    private CourseStore NewStore() => new(new HttpClient(new Server(_served)), Catalog, Store);

    /// <summary>A small course folder: Deer Run's map, under this id and version.</summary>
    private CoursePack MakePack(string id, int version, string where = "source")
    {
        string folder = Path.Combine(_root, where, id);
        Directory.CreateDirectory(folder);
        File.Copy(Path.Combine(DeerRun.Folder, CoursePack.MapFile), Path.Combine(folder, CoursePack.MapFile), overwrite: true);
        File.WriteAllText(Path.Combine(folder, CoursePack.ManifestFile), $$"""{"id":"{{id}}","name":"Deer Run","place":"Test","description":"d","version":{{version}}}""");
        return CoursePack.Read(folder);
    }

    private CatalogCourse Serve(CoursePack pack)
    {
        var entry = CourseStore.Publish(pack, pack.Load(), Path.Combine(_root, "out"));
        _served[entry.File] = File.ReadAllBytes(Path.Combine(_root, "out", entry.File));
        return entry;
    }

    /// <summary>A catalog entry for a zip with exactly these entries.</summary>
    private CatalogCourse ServeZip(string id, int version, params (string Name, byte[] Body)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, body) in files)
            {
                using var e = zip.CreateEntry(name).Open();
                e.Write(body);
            }
        byte[] bytes = ms.ToArray();
        string file = $"{id}-{version}.zip";
        _served[file] = bytes;
        return new CatalogCourse(id, "Deer Run", null, "", version, file, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), 18, 72, 6000);
    }

    private static CatalogCourse Listed(string id, int version, string hash = "a") =>
        new(id, id, null, "", version, $"{id}-{version}.zip", 1, new string(hash[0], 64), 18, 72, 6000);

    [Fact]
    public void PublishingKeepsCoursesThatArentOnThisPc()
    {
        var (courses, upload) = CourseCatalog.Merge(new[] { Listed("old", 3) }, new[] { Listed("new", 1) }, Array.Empty<string>());
        Assert.Equal(new[] { "new", "old" }, courses.Select(c => c.Id));
        Assert.Equal(new[] { "new" }, upload.Select(c => c.Id));
    }

    [Fact]
    public void PublishingOnlyReplacesACourseWithAHigherVersion()
    {
        var live = new[] { Listed("same", 2, "a"), Listed("older", 2, "a"), Listed("newer", 2, "a") };
        var built = new[] { Listed("same", 2, "b"), Listed("older", 1, "b"), Listed("newer", 3, "b") };
        var (courses, upload) = CourseCatalog.Merge(live, built, Array.Empty<string>());
        Assert.Equal(live[0], courses.Single(c => c.Id == "same"));     // its hash stays what players' zip has
        Assert.Equal(live[1], courses.Single(c => c.Id == "older"));
        Assert.Equal(built[2], courses.Single(c => c.Id == "newer"));
        Assert.Equal(new[] { built[2] }, upload);
    }

    [Fact]
    public void PublishingRemovesOnlyWhatItsAskedTo()
    {
        var (courses, upload) = CourseCatalog.Merge(new[] { Listed("gone", 1), Listed("stays", 1) }, new[] { Listed("gone", 2) }, new[] { "gone" });
        Assert.Equal(new[] { "stays" }, courses.Select(c => c.Id));
        Assert.Empty(upload);
    }

    [Fact]
    public void AnonymisingAMapRenamesItWithoutChangingHowItPlays()
    {
        var before = MakePack("test-course", 1);
        var original = before.Load();
        string map = Path.Combine(before.Folder, CoursePack.MapFile);

        OsmAnonymiser.Rewrite(map, "Deer Run", "Alabama Pines");
        File.WriteAllText(Path.Combine(before.Folder, CoursePack.ManifestFile), """{"id":"test-course","name":"Alabama Pines","version":1}""");
        var after = CoursePack.Read(before.Folder).Load();

        Assert.Equal(CourseFingerprint.Of(original), CourseFingerprint.Of(after));
        Assert.Equal(original.Holes.Select(h => (h.Number, h.Par)), after.Holes.Select(h => (h.Number, h.Par)));
        Assert.DoesNotContain("Deer Run", File.ReadAllText(map));
        Assert.Equal(new[] { "Alabama Pines" }, System.Xml.Linq.XDocument.Load(map).Descendants("tag")
            .Where(t => (string?)t.Attribute("k") == "name").Select(t => (string?)t.Attribute("v")));
    }

    [Fact]
    public void AnonymisingNeedsTheCourseToBeInTheMap()
    {
        string map = Path.Combine(MakePack("test-course", 1).Folder, CoursePack.MapFile);
        string before = File.ReadAllText(map);
        Assert.Throws<InvalidDataException>(() => OsmAnonymiser.Rewrite(map, "Nowhere Near", "Alabama Pines"));
        Assert.Equal(before, File.ReadAllText(map));
    }

    [Fact]
    public async Task InstallsAndAnUpdateReplacesIt()
    {
        var store = NewStore();
        await store.Install(Serve(MakePack("test-course", 1)));
        Assert.Equal(1, CoursePack.Read(Path.Combine(Store, "test-course")).Version);

        double last = 0;
        await store.Install(Serve(MakePack("test-course", 2)), p => last = p);
        Assert.Equal(1.0, last);
        Assert.Equal(2, CoursePack.Read(Path.Combine(Store, "test-course")).Version);
        Assert.Equal(new[] { "test-course" }, Directory.GetFileSystemEntries(Store).Select(Path.GetFileName));   // nothing left lying around

        store.Remove("test-course");
        Assert.False(store.Has("test-course"));
    }

    [Fact]
    public async Task ABadDownloadLeavesTheCourseAsItWas()
    {
        var store = NewStore();
        var entry = Serve(MakePack("test-course", 1));
        await store.Install(entry);
        var good = CoursePack.Read(Path.Combine(Store, "test-course"));

        var update = Serve(MakePack("test-course", 2));
        var corrupt = (byte[])_served[update.File].Clone();
        corrupt[corrupt.Length / 2] ^= 0xFF;
        _served[update.File] = corrupt;
        await Assert.ThrowsAsync<InvalidDataException>(() => store.Install(update));
        _served[update.File] = _served[update.File][..100];   // cut short
        await Assert.ThrowsAsync<InvalidDataException>(() => store.Install(update));

        Assert.Equal(good, CoursePack.Read(Path.Combine(Store, "test-course")));
        Assert.Equal(new[] { "test-course" }, Directory.GetFileSystemEntries(Store).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("subfolder/map.osm")]
    [InlineData("evil.dll")]
    public async Task AZipWithAnythingUnexpectedIsRefused(string name)
    {
        var entry = ServeZip("test-course", 1, (name, Encoding.UTF8.GetBytes("x")));
        await Assert.ThrowsAsync<InvalidDataException>(() => NewStore().Install(entry));
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
        Assert.False(NewStore().Has("test-course"));
    }

    [Fact]
    public async Task ACourseThatDoesntLoadOrIsntWhatWasListedIsRefused()
    {
        byte[] manifest = Encoding.UTF8.GetBytes("""{"id":"test-course","name":"Broken","version":1}""");
        var broken = ServeZip("test-course", 1, (CoursePack.ManifestFile, manifest), (CoursePack.MapFile, Encoding.UTF8.GetBytes("<osm/>")));
        await Assert.ThrowsAsync<InvalidDataException>(() => NewStore().Install(broken));

        var other = Serve(MakePack("other-course", 1));
        var claimed = other with { Id = "test-course" };
        await Assert.ThrowsAsync<InvalidDataException>(() => NewStore().Install(claimed));
        Assert.False(NewStore().Has("test-course"));
    }

    [Fact]
    public void TheCatalogLeavesOutAnythingMalformed()
    {
        var good = new CatalogCourse("a-course", "A", "B", "C", 1, "a-course-1.zip", 10, new string('a', 64), 18, 72, 6000);
        Assert.Equal(new[] { good }, CourseCatalog.Parse(Encoding.UTF8.GetBytes(CourseCatalog.ToJson(new[] { good }))));
        foreach (var bad in new[]
                 {
                     good with { Id = "../a" }, good with { File = "../x.zip" }, good with { File = "http://evil/x.zip" }, good with { File = "x.exe" },
                     good with { Sha256 = "abc" }, good with { Bytes = CourseCatalog.MaxBytes + 1 }, good with { Bytes = 0 }, good with { Version = 0 },
                 })
            Assert.Empty(CourseCatalog.Parse(Encoding.UTF8.GetBytes(CourseCatalog.ToJson(new[] { bad }))));
        Assert.Throws<InvalidDataException>(() => CourseCatalog.Parse(Encoding.UTF8.GetBytes("not json")));
        Assert.Throws<InvalidDataException>(() => CourseCatalog.Parse(Encoding.UTF8.GetBytes("""{"format":99,"courses":[]}""")));
    }

    [Fact]
    public void ScanTakesTheHighestVersionAndSkipsWhatItCant()
    {
        MakePack("test-course", 1, "shipped");
        MakePack("test-course", 2, "downloaded");
        MakePack("second-course", 3, "shipped");
        Directory.CreateDirectory(Path.Combine(_root, "shipped", "broken"));
        Directory.CreateDirectory(Path.Combine(_root, "shipped", ".new-installing"));
        File.WriteAllText(Path.Combine(_root, "downloaded", "stray.txt"), "");

        var (found, problems) = CourseLibrary.Find(Path.Combine(_root, "shipped"), Path.Combine(_root, "downloaded"), Path.Combine(_root, "not-there"));

        Assert.Single(problems);
        Assert.Contains("broken", problems[0]);
        var packs = found.ToDictionary(p => p.Id);
        Assert.Equal(2, packs.Count);
        Assert.Equal(2, packs["test-course"].Version);
    }

    /// <summary>A map with an outline, 18 holes running up it, and whatever else is added.</summary>
    private static CourseDef ImportWithExtras(Func<Func<double, double, int?, int, string>, string> extras)
    {
        var body = new StringBuilder();
        int id = 1;
        string Hole(double lat, double lon, int? number, int par)
        {
            int a = id++, b = id++, way = id++;
            string reference = number == null ? "" : $"<tag k='ref' v='{number}'/>";
            return $"<node id='{a}' lat='{lat:0.00000}' lon='{lon:0.00000}'/><node id='{b}' lat='{lat + 0.003:0.00000}' lon='{lon:0.00000}'/>" +
                   $"<way id='{way}'><nd ref='{a}'/><nd ref='{b}'/><tag k='golf' v='hole'/>{reference}<tag k='par' v='{par}'/></way>";
        }
        for (int n = 1; n <= 18; n++) body.Append(Hole(34.0 + (n % 2) * 0.0035, -87.0 + n * 0.002, n, 4));
        body.Append(extras(Hole));
        double[][] corners = { new[] { 33.99, -87.0 }, new[] { 34.02, -87.0 }, new[] { 34.02, -86.95 }, new[] { 33.99, -86.95 } };
        int first = id;
        foreach (var c in corners) body.Append($"<node id='{id++}' lat='{c[0]:0.00000}' lon='{c[1]:0.00000}'/>");
        body.Append($"<way id='{id}'>" + string.Concat(Enumerable.Range(0, 4).Select(i => $"<nd ref='{first + i}'/>")) +
                    $"<nd ref='{first}'/><tag k='leisure' v='golf_course'/><tag k='name' v='Test Links'/></way>");
        using var map = new MemoryStream(Encoding.UTF8.GetBytes($"<osm>{body}</osm>"));
        return OsmCourse.Load(map, "Test Links");
    }

    [Fact]
    public void ANeighbouringCoursesHolesAreLeftOut()
    {
        var course = ImportWithExtras(hole => hole(34.05, -86.97, 5, 4) + hole(34.06, -86.97, 19, 4));
        Assert.Equal(Enumerable.Range(1, 18), course.Holes.Select(h => h.Number));
        Assert.All(course.Holes, h => Assert.True(h.Tee.X < 4000));
    }

    [Fact]
    public void ASecondCourseInsideTheOutlineIsLeftOut()
    {
        // A par-3 course up in a corner, numbered 1-3 like the main course's first three.
        var course = ImportWithExtras(hole => hole(34.015, -86.955, 1, 3) + hole(34.015, -86.953, 2, 3) + hole(34.015, -86.951, 3, 3));
        Assert.Equal(18, course.Holes.Count);
        Assert.All(course.Holes, h => Assert.Equal(4, h.Par));
    }

    [Fact]
    public void AnOldSettingsFileStillChecksForUpdates()
    {
        File.WriteAllText(Path.Combine(_root, GrimesGolf.Core.Storage.AppSettings.FileName), "{}");
        Assert.True(GrimesGolf.Core.Storage.AppSettings.Load(_root, _ => { }).CheckForUpdates);
    }

    [Fact]
    public void AManifestHasToMakeSense()
    {
        foreach (var json in new[] { """{"name":"x","version":1}""", """{"id":"Bad Id","name":"x","version":1}""", """{"id":"x"}""", """{"id":"x","name":"y"}""", "nonsense" })
        {
            string folder = Path.Combine(_root, "m");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, CoursePack.MapFile), "");
            File.WriteAllText(Path.Combine(folder, CoursePack.ManifestFile), json);
            Assert.Throws<InvalidDataException>(() => CoursePack.Read(folder));
        }
    }

    [Fact]
    public void TheFingerprintNoticesAMovedBunkerOrTree()
    {
        var course = DeerRun.Load();
        string before = CourseFingerprint.Of(course);
        var hole = course.Holes[0];
        var bunker = (Polygon)hole.Bunkers[0];
        var moved = bunker with { Points = bunker.Points.Select(p => p + new P2(3, 0)).ToList() };
        var holes = course.Holes.Select((h, i) => i == 0 ? h with { Bunkers = h.Bunkers.Select((b, k) => k == 0 ? moved : b).ToList() } : h).ToList();
        Assert.NotEqual(before, CourseFingerprint.Of(holes, course.Terrain, course.Trees));

        var trees = course.Trees!.Select((t, i) => i == 0 ? t with { Height = t.Height + 5 } : t).ToList();
        Assert.NotEqual(before, CourseFingerprint.Of(course.Holes, course.Terrain, trees));
    }

    [Theory]
    [InlineData("v0.9.8", "0.9.7", true)]
    [InlineData("v1.0", "0.9.7", true)]
    [InlineData("v0.9.7", "v0.9.7", false)]
    [InlineData("v0.9.6", "0.9.7", false)]
    [InlineData("courses", "0.9.7", false)]
    [InlineData("v1.0.0-beta", "v0.9.8", true)]
    [InlineData("v1.0.0", "v1.0.0-beta", true)]
    [InlineData("v1.0.0-beta.2", "v1.0.0-beta", true)]
    [InlineData("v1.0.0-beta", "v1.0.0-beta", false)]
    [InlineData("v1.0.0-beta", "v1.0.0", false)]
    [InlineData("v1.0", "v1.0.0", false)]
    public void AnUpdateIsOnlyOfferedWhenItsNewer(string tag, string current, bool offered)
    {
        string json = $$"""{"tag_name":"{{tag}}","html_url":"https://github.com/devjared1/grimes-golf/releases/tag/{{tag}}"}""";
        Assert.Equal(offered, Releases.Newer(json, current) != null);
    }

    [Theory]
    [InlineData("""{"tag_name":"v9.0","html_url":"https://evil.example/download"}""")]
    [InlineData("""{"message":"rate limited"}""")]
    [InlineData("not json")]
    public void ANonsenseAnswerOffersNoUpdate(string json) => Assert.Null(Releases.Newer(json, "0.9.7"));
}
