using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GrimesGolf.Core.Course;

/// <summary>A course that can be downloaded, as the catalog lists it.</summary>
/// <param name="File">The zip's name, beside the catalog.</param>
public sealed record CatalogCourse(
    string Id, string Name, string? Place, string Description, int Version, string File, long Bytes, string Sha256, int Holes, int Par, int Yards)
{
    /// <summary>The name it will have on the course list.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Title => Place == null ? Name : $"{Name} ({Place})";
}

/// <summary>The list of downloadable courses: <c>catalog.json</c>, published beside the courses' zips (<c>tools/publish-courses.ps1</c>).</summary>
public static partial class CourseCatalog
{
    public const string FileName = "catalog.json";
    /// <summary>A catalog with a higher format number needs a newer app.</summary>
    public const int Format = 1;
    /// <summary>Nothing bigger is downloaded (Deer Run's zip is about 7 MB).</summary>
    public const long MaxBytes = 300_000_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record Document(int Format, CatalogCourse[]? Courses);

    public static string ToJson(IEnumerable<CatalogCourse> courses) => JsonSerializer.Serialize(new Document(Format, courses.OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase).ToArray()), Json);

    /// <summary>The usable courses in a catalog (the catalog comes off the internet: anything malformed is left out).</summary>
    public static IReadOnlyList<CatalogCourse> Parse(ReadOnlySpan<byte> json)
    {
        Document? doc;
        try { doc = JsonSerializer.Deserialize<Document>(json, Json); }
        catch (JsonException ex) { throw new InvalidDataException($"The course list can't be read: {ex.Message}"); }
        if (doc == null) throw new InvalidDataException("The course list is empty.");
        if (doc.Format > Format) throw new InvalidDataException("The course list needs a newer version of the app.");
        return (doc.Courses ?? Array.Empty<CatalogCourse>()).Where(Usable).ToList();
    }

    private static bool Usable(CatalogCourse? c) =>
        c != null && CoursePack.ValidId(c.Id) && !string.IsNullOrWhiteSpace(c.Name) && c.Version > 0 && c.File != null && ZipName().IsMatch(c.File) &&
        c.Bytes > 0 && c.Bytes <= MaxBytes && c.Sha256 != null && Hash().IsMatch(c.Sha256);

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]*\\.zip$")]
    private static partial Regex ZipName();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Hash();
}

/// <summary>
/// Downloading courses into a folder (a folder per course, which <see cref="CourseLibrary.Scan"/> then finds). A download
/// is checked against the catalog's hash, unpacked beside the real folder, built once to prove it loads, and only then
/// swapped in, so a failure at any point leaves the course as it was.
/// </summary>
public sealed class CourseStore(HttpClient http, string catalogUrl, string folder)
{
    /// <summary>What an unpacked course may hold: anything else (or anything in a subfolder) in a zip refuses it.</summary>
    private static readonly string[] PackFiles = { CoursePack.ManifestFile, CoursePack.MapFile, CoursePack.GroundFile, CoursePack.TreesFile };
    private const long MaxUnpackedBytes = 2 * CourseCatalog.MaxBytes;

    /// <summary>The course list from the catalog. Throws if it can't be fetched or read.</summary>
    public async Task<IReadOnlyList<CatalogCourse>> FetchCatalog(CancellationToken cancel = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        return CourseCatalog.Parse(await http.GetByteArrayAsync(catalogUrl + CourseCatalog.FileName, timeout.Token));
    }

    /// <summary>Downloads a course and installs it, replacing any older copy here.</summary>
    /// <param name="progress">How far through the download, 0 to 1.</param>
    public async Task Install(CatalogCourse course, Action<double>? progress = null, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(folder);
        string zip = Path.Combine(folder, $".download-{course.Id}.zip");
        try
        {
            await Download(course, zip, progress, cancel);
            await Task.Run(() => Unpack(zip, course), cancel);
        }
        finally
        {
            File.Delete(zip);
        }
    }

    private async Task Download(CatalogCourse course, string path, Action<double>? progress, CancellationToken cancel)
    {
        using var response = await http.GetAsync(catalogUrl + course.File, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancel);
        await using var target = File.Create(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 16];
        long total = 0;
        while (await source.ReadAsync(buffer, cancel) is var n and > 0)
        {
            total += n;
            if (total > course.Bytes) throw new InvalidDataException("The download is bigger than the course list says.");
            hash.AppendData(buffer, 0, n);
            await target.WriteAsync(buffer.AsMemory(0, n), cancel);
            progress?.Invoke((double)total / course.Bytes);
        }
        if (total != course.Bytes || Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != course.Sha256)
            throw new InvalidDataException("The download doesn't match the course list (it may have been cut short). Try again.");
    }

    /// <summary>Unpacks a checked zip and swaps it in.</summary>
    internal void Unpack(string zip, CatalogCourse course)
    {
        string fresh = Path.Combine(folder, $".new-{course.Id}"), old = Path.Combine(folder, $".old-{course.Id}"), target = Path.Combine(folder, course.Id);
        DeleteFolder(fresh);
        try
        {
            Directory.CreateDirectory(fresh);
            Extract(zip, fresh);
            var pack = CoursePack.Read(fresh);
            if (pack.Id != course.Id || pack.Version != course.Version)
                throw new InvalidDataException($"The download holds {pack.Id} version {pack.Version}, not {course.Id} version {course.Version}.");
            try { pack.Load(); }
            catch (Exception ex) { throw new InvalidDataException($"The downloaded course doesn't load: {ex.Message}"); }

            DeleteFolder(old);
            if (Directory.Exists(target)) Directory.Move(target, old);
            try { Directory.Move(fresh, target); }
            catch
            {
                if (Directory.Exists(old)) Directory.Move(old, target);
                throw;
            }
            DeleteFolder(old);
        }
        finally
        {
            DeleteFolder(fresh);
        }
    }

    private static void Extract(string zip, string into)
    {
        using var archive = ZipFile.OpenRead(zip);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            // Only the known names, so nothing can land outside the folder.
            if (!PackFiles.Contains(entry.FullName)) throw new InvalidDataException($"The download holds an unexpected file, {entry.FullName}.");
            total += entry.Length;
            if (total > MaxUnpackedBytes) throw new InvalidDataException("The download unpacks to too much.");
            entry.ExtractToFile(Path.Combine(into, entry.FullName));
        }
    }

    /// <summary>Removes a downloaded course (one that came with the app is in another folder, and stays).</summary>
    public void Remove(string id)
    {
        if (CoursePack.ValidId(id)) DeleteFolder(Path.Combine(folder, id));
    }

    public bool Has(string id) => CoursePack.ValidId(id) && Directory.Exists(Path.Combine(folder, id));

    /// <summary>Zips a course folder for publishing: its entry in the catalog, with the zip beside it in <paramref name="outFolder"/>.</summary>
    public static CatalogCourse Publish(CoursePack pack, Course course, string outFolder)
    {
        Directory.CreateDirectory(outFolder);
        string name = $"{pack.Id}-{pack.Version}.zip", path = Path.Combine(outFolder, name);
        File.Delete(path);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach (var file in PackFiles.Where(f => File.Exists(Path.Combine(pack.Folder, f))))
                archive.CreateEntryFromFile(Path.Combine(pack.Folder, file), file, CompressionLevel.Optimal);
        byte[] bytes = File.ReadAllBytes(path);
        return new CatalogCourse(pack.Id, pack.Name, pack.Place, pack.Description, pack.Version, name, bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), course.Holes.Count, course.Holes.Sum(h => h.Par), (int)Math.Round(course.Holes.Sum(h => h.LengthYards)));
    }

    private static void DeleteFolder(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
