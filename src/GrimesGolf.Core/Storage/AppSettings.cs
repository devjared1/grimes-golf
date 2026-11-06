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
    /// <summary>The saved player, loaded every time the app starts (null = none yet: the app asks for one).</summary>
    public HomePlayer? Home { get; init; }
    /// <summary>Guests kept for next time, name and hand only. Read them through <see cref="Guests"/>.</summary>
    public IReadOnlyList<Golfer>? SavedGuests { get; init; }
    /// <summary>Play the sound effects (off for the club and ball's own noise alone).</summary>
    public bool SoundEffects { get; init; } = true;
    /// <summary>Ask GitHub at start-up whether there's a newer version of the app (it only ever offers: nothing installs by itself).</summary>
    public bool CheckForUpdates { get; init; } = true;
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

    /// <summary>No home player has been saved, so the app asks for one when it starts.</summary>
    [JsonIgnore] public bool NeedsHomePlayer => string.IsNullOrWhiteSpace(Home?.Name);

    /// <summary>
    /// The player to offer when none is saved: the first in the list from before home players existed, unless that's
    /// only the "Player 1" stand-in.
    /// </summary>
    [JsonIgnore]
    public Golfer? HomeCandidate =>
        Players is { Count: > 0 } p && !string.IsNullOrWhiteSpace(p[0].Name) && !p[0].Name.Trim().Equals("Player 1", StringComparison.OrdinalIgnoreCase)
            ? p[0] with { Name = p[0].Name.Trim() } : null;

    /// <summary>The settings as the app starts with them: just the home player (guests last until the app closes).</summary>
    public AppSettings AtStartup() => NeedsHomePlayer ? this : this with { Players = new[] { Home!.Golfer } };

    /// <summary>
    /// With <paramref name="home"/> as the home player, and in the player list: in the place of the home player they
    /// replace, or first if there was none. If the old home player had been taken off the list, the list stays as it is.
    /// </summary>
    public AppSettings WithHome(HomePlayer home)
    {
        var players = Golfers.ToList();
        int at = NeedsHomePlayer ? 0 : players.FindIndex(g => IsNamed(g, Home!.Name));
        if (at >= 0) players[at] = home.Golfer;
        return this with { Home = home, Players = players };
    }

    /// <summary>The carries <paramref name="golfer"/> has set: the home player's, nothing for a guest.</summary>
    public IReadOnlyDictionary<string, int>? ClubYardsFor(Golfer golfer) =>
        !NeedsHomePlayer && IsNamed(golfer, Home!.Name) ? Home.ClubYards : null;

    private static bool IsNamed(Golfer golfer, string name) => golfer.Name.Trim().Equals(name.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The saved guests: named, each name once, and not the home player.</summary>
    [JsonIgnore] public IReadOnlyList<Golfer> Guests => CleanGuests(SavedGuests);

    /// <summary>Everyone who can be picked for a round without typing: the home player, then the saved guests.</summary>
    [JsonIgnore]
    public IReadOnlyList<Golfer> SavedPlayers => NeedsHomePlayer ? Guests : Guests.Prepend(Home!.Golfer).ToList();

    /// <summary>With <paramref name="guests"/> as the saved guests (blank and repeated names dropped).</summary>
    public AppSettings WithGuests(IEnumerable<Golfer> guests) => this with { SavedGuests = CleanGuests(guests) };

    /// <summary>With <paramref name="guest"/> kept as a saved guest (replacing the hand of one by the same name).</summary>
    public AppSettings WithGuest(Golfer guest) =>
        WithGuests(Guests.Select(g => IsNamed(g, guest.Name) ? guest : g).Append(guest));

    private IReadOnlyList<Golfer> CleanGuests(IEnumerable<Golfer>? guests) =>
        (guests ?? Array.Empty<Golfer>())
        .Where(g => !string.IsNullOrWhiteSpace(g.Name) && (NeedsHomePlayer || !IsNamed(g, Home!.Name)))
        .Select(g => g with { Name = g.Name.Trim() })
        .DistinctBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
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
