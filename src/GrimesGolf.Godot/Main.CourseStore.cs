using Godot;
using GrimesGolf.Core;
using GrimesGolf.Core.Course;
using HttpClient = System.Net.Http.HttpClient;

namespace GrimesGolf.Game;

/// <summary>
/// Where courses are kept (the ones that came with the app, and those downloaded since, in the data folder), Get More
/// Courses, and the check for a newer version of the app. The app works without any of this: a course that came with
/// it needs no internet, and the update check says nothing if it can't get through.
/// </summary>
public partial class Main
{
    private static readonly HttpClient Web = NewWebClient();
    private CourseStore _store = null!;
    private IReadOnlyList<CatalogCourse> _catalog = Array.Empty<CatalogCourse>();
    private CancellationTokenSource? _download;

    /// <summary>
    /// The courses that came with the app: <c>courses</c> beside the exported app, or in the project when run from Godot.
    /// (A build with them packed inside, the iPad's, unpacks them into the data folder: see <see cref="ShippedFiles"/>.)
    /// </summary>
    private static string ShippedCourses =>
        ShippedFiles.Packed ? ShippedFiles.Courses
        : OS.HasFeature("template")
            ? Path.Combine(Path.GetDirectoryName(OS.GetExecutablePath()) ?? ".", "courses")
            : Path.Combine(ProjectSettings.GlobalizePath("res://"), "courses");

    private string DownloadedCourses => Path.Combine(_dataFolder, "courses");

    private static HttpClient NewWebClient()
    {
        // (Timeouts are per request: a course download can take a while on a slow connection.)
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"GrimesGolf/{Hud.Version.TrimStart('v')}");
        return client;
    }

    /// <summary>Finds the courses on this PC (the one selected stays selected if it's still there).</summary>
    private void ScanCourses()
    {
        string? selected = _courseIndex < CourseLibrary.All.Count ? CourseLibrary.All[_courseIndex].Name : null;
        foreach (var problem in CourseLibrary.Scan(ShippedCourses, DownloadedCourses)) Log(problem);
        if (CourseLibrary.All.Count == 0) Log($"No courses found in {ShippedCourses}.");
        _courseIndex = Math.Max(0, CourseLibrary.All.ToList().FindIndex(e => e.Name == selected));
    }

    /// <summary>Hooks up Get More Courses. <c>--catalog=url/</c> (debug builds) gets the courses from somewhere else, to try them before publishing.</summary>
    private void SetupCourseStore(string[] args)
    {
        string catalog = OS.IsDebugBuild() && args.FirstOrDefault(a => a.StartsWith("--catalog="))?[10..] is { Length: > 0 } url ? url : Releases.CoursesUrl;
        _store = new CourseStore(Web, catalog, DownloadedCourses);
        var panel = _hud.Store;
        _hud.MainMenu.GetCoursesPressed += OpenCourseStore;
        panel.RetryPressed += OpenCourseStore;
        panel.DownloadPressed += DownloadCourse;
        panel.RemovePressed += RemoveCourse;
        panel.CancelDownload += () => _download?.Cancel();
        panel.Closed += () => { ShowMainMenu(); _hud.MainMenu.OpenCoursePage(); };
    }

    private async void OpenCourseStore()
    {
        _hud.Store.Open();
        try
        {
            _catalog = await _store.FetchCatalog();
            if (_hud.Store.IsOpen) _hud.Store.ShowRows(StoreRows());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            Log($"Couldn't get the course list: {ex.Message}");
            if (_hud.Store.IsOpen)
                _hud.Store.ShowProblem(ex is InvalidDataException ? ex.Message : "Couldn't get the list of courses. Check the internet connection and try again.");
        }
    }

    private List<StoreRow> StoreRows() => _catalog
        .Select(c => new StoreRow(c, CourseLibrary.All.FirstOrDefault(e => e.Pack.Id == c.Id)?.Pack.Version, _store.Has(c.Id)))
        .ToList();

    private async void DownloadCourse(CatalogCourse course)
    {
        if (_download != null) return;
        var panel = _hud.Store;
        panel.ShowDownloading(course.Title);
        using var cancel = new CancellationTokenSource();
        _download = cancel;
        string message;
        try
        {
            await _store.Install(course, p => Post(() => panel.SetProgress(p)), cancel.Token);
            ScanCourses();
            message = $"{course.Title} is ready: pick it on the New Game list.";
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { message = "Download canceled."; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or TaskCanceledException)
        {
            Log($"Couldn't install {course.Title}: {ex.Message}");
            message = ex is InvalidDataException ? ex.Message : $"Couldn't download {course.Title}. Check the internet connection and try again.";
        }
        _download = null;
        panel.ShowRows(StoreRows(), message);
    }

    private void RemoveCourse(CatalogCourse course)
    {
        string message;
        try
        {
            _store.Remove(course.Id);
            ScanCourses();
            message = $"{course.Title} removed.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"Couldn't remove {course.Title}: {ex.Message}");
            message = $"Couldn't remove {course.Title}.";
        }
        _hud.Store.ShowRows(StoreRows(), message);
    }

    /// <summary>
    /// Exported builds, if updates are allowed: fetches the published physics tuning, and if it's newer than the one last
    /// applied, saves it as this PC's start-up tuning (replacing what was there) and switches to it. Failing just means
    /// the tuning stays as it is. <c>--fetch-tuning=url</c> (debug builds) tries one from somewhere else; it saves into the
    /// tuning folder of whatever's running, so use a copy of the project's.
    /// </summary>
    private async void FetchPublishedTuning(string[] args)
    {
        string? custom = OS.IsDebugBuild() ? args.FirstOrDefault(a => a.StartsWith("--fetch-tuning="))?[15..] : null;
        if (custom == null && (!OS.HasFeature("template") || !_settings.CheckForUpdates)) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            string json = await Web.GetStringAsync(string.IsNullOrEmpty(custom) ? Releases.TuningUrl : custom, timeout.Token);
            if (TuningFeed.Read(json) is not { } published) { Log("Published tuning: not a valid tuning file, ignored."); return; }
            string versionFile = Path.Combine(_dataFolder, "tuning-version.txt");
            int applied = TuningFeed.ParseApplied(File.Exists(versionFile) ? File.ReadAllText(versionFile) : null);
            // Not while online play is using the host's numbers (the next start-up gets it).
            if (published.Version <= applied || TunerWindow.FreezeStartup || _online != null) return;
            Directory.CreateDirectory(TunerWindow.PresetFolder);
            File.WriteAllText(Path.Combine(TunerWindow.PresetFolder, TunerWindow.StartupFile), published.Tuning.ToJson());
            File.WriteAllText(versionFile, published.Version.ToString());
            ActivePhysics.Apply(published.Tuning);
            Log($"Physics: updated to published tuning version {published.Version} (\"{published.Tuning.Name}\").");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            GD.Print($"Tuning update: {ex.Message}");
        }
    }

    /// <summary>
    /// Exported builds, if it's allowed: asks GitHub for the newest version once, and offers it on the main menu. Failing
    /// just means no offer. Not on a phone or tablet: the offer is the Windows installer, and theirs update another way.
    /// </summary>
    private async void CheckForUpdate(string[] args)
    {
        bool forced = OS.IsDebugBuild() && args.Contains("--check-update");
        if (!forced && (!OS.HasFeature("template") || OS.HasFeature("mobile") || !_settings.CheckForUpdates)) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            string json = await Web.GetStringAsync(Releases.LatestUrl, timeout.Token);
            if (Releases.Newer(json, Hud.Version) is { } update)
            {
                Log($"Version {update.Version} is available: {update.Page}");
                _hud.MainMenu.ShowUpdate(update);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            GD.Print($"Update check: {ex.Message}");
        }
    }
}
