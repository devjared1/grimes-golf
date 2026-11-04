using System.Text.Json;
using System.Text.Json.Serialization;
using GrimesGolf.Core.Course;

namespace GrimesGolf.Core.Storage;

/// <summary>How big the on-screen text and controls are drawn (for reading from the mat or on a projector).</summary>
public enum TextSize { Small, Normal, Large, ExtraLarge }

/// <summary>How much the 3D view spends on looks: lower for laptops and older graphics cards, Ultra for a fast one.</summary>
public enum GraphicsQuality { Low, Medium, High, Ultra }

/// <summary>
/// The app's settings, kept as <c>settings.json</c> in the data folder and changed from the settings screen.
/// Everything a guest would otherwise need a command-line flag for lives here.
/// </summary>
/// <param name="Players">The players for the next round (1-4), in teeing order for the first hole.</param>
/// <param name="HouseRules">Rules for course rounds. The default is relaxed for a garage round with friends.</param>
/// <param name="AutoConnect">Connect to a launch monitor as soon as one is found (the last one used, if it's there).</param>
/// <param name="LastMonitor">Bluetooth address of the monitor last connected to.</param>
/// <param name="TextSize">On-screen text and control size.</param>
/// <param name="Course">Course last picked (remembered).</param>
/// <param name="CameraFollow">Camera follows the ball rather than staying behind it (remembered).</param>
/// <param name="FullScreen">Fill the screen (a TV or projector) rather than a window.</param>
/// <param name="HoleFlyover">Fly the camera down each hole before the first tee shot.</param>
/// <param name="Graphics">3D quality: anti-aliasing, shadows and lighting effects.</param>
/// <param name="Game">The game, holes, weather and course conditions picked on the Game Setup screen last time.</param>
public sealed record AppSettings(
    IReadOnlyList<Golfer>? Players = null, HouseRules? HouseRules = null, bool AutoConnect = true, ulong? LastMonitor = null,
    TextSize TextSize = TextSize.Normal, string? Course = null, bool CameraFollow = true,
    bool FullScreen = false, bool HoleFlyover = true, GraphicsQuality Graphics = GraphicsQuality.High,
    GameSetup? Game = null)
{
    /// <summary>Online play: the relay's address (<c>wss://…/ws</c>), remembered.</summary>
    public string? RelayUrl { get; init; }
    /// <summary>Online play: this PC's secret token, which the relay and the host know it by when it reconnects.</summary>
    public string? OnlineToken { get; init; }
    /// <summary>Online play: the lobby of a round this PC was in that can still be rejoined, until <see cref="RejoinUntil"/>.</summary>
    public string? RejoinCode { get; init; }
    public DateTime? RejoinUntil { get; init; }

    public const string FileName = "settings.json";

    /// <summary>Two mulligans a round, 2 ft gimmes, pick up at double par.</summary>
    public static readonly HouseRules GarageRules = new(Mulligans: 2, PickUpAtDoublePar: true);

    [JsonIgnore] public HouseRules Rules => HouseRules ?? GarageRules;
    [JsonIgnore] public GameSetup Setup => Game ?? GameSetup.Default;

    /// <summary>The players, never empty and at most <see cref="Match.MaxPlayers"/>, with blank names filled in.</summary>
    [JsonIgnore]
    public IReadOnlyList<Golfer> Golfers =>
        (Players is { Count: > 0 } p ? p.Take(Match.MaxPlayers) : new[] { new Golfer("Player 1") })
        .Select((g, i) => string.IsNullOrWhiteSpace(g.Name) ? g with { Name = $"Player {i + 1}" } : g with { Name = g.Name.Trim() })
        .ToList();

    /// <summary>UI scale for <see cref="TextSize"/>.</summary>
    [JsonIgnore] public float UiScale => TextSize switch { TextSize.Small => 0.85f, TextSize.Large => 1.15f, TextSize.ExtraLarge => 1.3f, _ => 1f };

    public static AppSettings Defaults => new(new[] { new Golfer("Player 1") }, GarageRules);

    /// <summary>Reads the settings in <paramref name="folder"/>, creating the file with the defaults if it's missing.</summary>
    public static AppSettings Load(string folder, Action<string>? error = null)
    {
        string path = Path.Combine(folder, FileName);
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), GolfData.Json) ?? Defaults;
            var defaults = Defaults;
            defaults.Save(folder, error);
            return defaults;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error?.Invoke($"Couldn't read {path}: {ex.Message}. Using the defaults.");
            return Defaults;
        }
    }

    public void Save(string folder, Action<string>? error = null)
    {
        string path = Path.Combine(folder, FileName);
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, GolfData.Json));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error?.Invoke($"Couldn't save {path}: {ex.Message}");
        }
    }
}
