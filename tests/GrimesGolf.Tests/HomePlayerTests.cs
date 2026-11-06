using GrimesGolf.Core;
using GrimesGolf.Core.Course;
using GrimesGolf.Core.Square;
using GrimesGolf.Core.Storage;
using GrimesGolf.Physics;
using Xunit;

namespace GrimesGolf.Tests;

public class HomePlayerSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gg-home-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void AFreshInstallAsksForAHomePlayerAndOffersNoName()
    {
        var s = AppSettings.Load(_dir);
        Assert.True(s.NeedsHomePlayer);
        Assert.Null(s.HomeCandidate);
    }

    [Fact]
    public void AnEarlierInstallOffersItsFirstPlayer()
    {
        var s = new AppSettings(new[] { new Golfer(" Jared ", true), new Golfer("Mike") });
        Assert.True(s.NeedsHomePlayer);
        Assert.Equal(new Golfer("Jared", true), s.HomeCandidate);
    }

    [Fact]
    public void ABlankSavedNameStillAsks() => Assert.True(new AppSettings { Home = new HomePlayer("  ") }.NeedsHomePlayer);

    [Fact]
    public void TheHomePlayerIsSavedAndLoadedWithEverything()
    {
        var home = new HomePlayer("Jared", LeftHanded: true)
        {
            LaunchMonitor = "Square Golf (hardware 1.5)",
            ClubYards = new Dictionary<string, int> { ["DR"] = 240, ["I7"] = 150 },
        };
        AppSettings.Defaults.WithHome(home).Save(_dir);

        var loaded = AppSettings.Load(_dir).AtStartup();
        Assert.False(loaded.NeedsHomePlayer);
        Assert.Equal("Jared", loaded.Home!.Name);
        Assert.True(loaded.Home.LeftHanded);
        Assert.Equal("Square Golf (hardware 1.5)", loaded.Home.LaunchMonitor);
        Assert.Equal(150, loaded.Home.ClubYards!["I7"]);
        Assert.Equal(new Golfer("Jared", true), Assert.Single(loaded.Golfers));
    }

    [Fact]
    public void SettingsFromBeforeHomePlayersStillLoad()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, AppSettings.FileName), "{ \"Players\": [ { \"Name\": \"Mike\" } ] }");
        var s = AppSettings.Load(_dir);
        Assert.Null(s.Home);
        Assert.Equal("Mike", Assert.Single(s.AtStartup().Golfers).Name);   // nobody to start with yet: the list stands
    }

    [Fact]
    public void GuestsLastUntilTheAppCloses()
    {
        var s = AppSettings.Defaults.WithHome(new HomePlayer("Jared")) with { Players = new[] { new Golfer("Jared"), new Golfer("Mike") } };
        Assert.Equal(new[] { new Golfer("Jared") }, s.AtStartup().Golfers);
    }

    [Fact]
    public void AFirstHomePlayerTakesTheFirstPlace()
    {
        var s = new AppSettings(new[] { new Golfer("Player 1"), new Golfer("Mike") }).WithHome(new HomePlayer("Jared"));
        Assert.Equal(new[] { new Golfer("Jared"), new Golfer("Mike") }, s.Golfers);
    }

    [Fact]
    public void EditingTheHomePlayerUpdatesThemInTheList()
    {
        var s = new AppSettings(new[] { new Golfer("Mike"), new Golfer("Jared") }) { Home = new HomePlayer("Jared") }
            .WithHome(new HomePlayer("Jed", LeftHanded: true));
        Assert.Equal(new[] { new Golfer("Mike"), new Golfer("Jed", true) }, s.Golfers);
    }

    [Fact]
    public void EditingAHomePlayerWhoIsSittingOutLeavesTheListAlone()
    {
        var s = new AppSettings(new[] { new Golfer("Mike") }) { Home = new HomePlayer("Jared") }.WithHome(new HomePlayer("Jed"));
        Assert.Equal(new[] { new Golfer("Mike") }, s.Golfers);
    }

    [Fact]
    public void OnlyTheHomePlayerHasClubDistances()
    {
        var yards = new Dictionary<string, int> { ["I7"] = 150 };
        var s = new AppSettings { Home = new HomePlayer("Jared") { ClubYards = yards } };
        Assert.Same(yards, s.ClubYardsFor(new Golfer(" jared ")));
        Assert.Null(s.ClubYardsFor(new Golfer("Mike")));
        Assert.Null(new AppSettings().ClubYardsFor(new Golfer("Jared")));
    }

    [Fact]
    public void SavedGuestsSurviveARestartButDoNotJoinTheRoundByThemselves()
    {
        var s = AppSettings.Defaults.WithHome(new HomePlayer("Jared")).WithGuests(new[] { new Golfer("Mike", true), new Golfer("Ann") });
        s.Save(_dir);

        var loaded = AppSettings.Load(_dir).AtStartup();
        Assert.Equal(new[] { new Golfer("Mike", true), new Golfer("Ann") }, loaded.Guests);
        Assert.Equal(new[] { new Golfer("Jared") }, loaded.Golfers);
        Assert.Equal(new[] { new Golfer("Jared"), new Golfer("Mike", true), new Golfer("Ann") }, loaded.SavedPlayers);
    }

    [Fact]
    public void AGuestIsSavedAsNameAndHandOnly()
    {
        AppSettings.Defaults.WithGuests(new[] { new Golfer("Mike", true) }).Save(_dir);
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, AppSettings.FileName)));
        var guest = Assert.Single(json.RootElement.GetProperty("SavedGuests").EnumerateArray());
        Assert.Equal(new[] { "Name", "LeftHanded" }, guest.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void SettingsWithoutSavedGuestsStillLoad()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, AppSettings.FileName), "{ \"Players\": [ { \"Name\": \"Mike\" } ] }");
        Assert.Empty(AppSettings.Load(_dir).Guests);
    }

    [Fact]
    public void BlankAndRepeatedGuestsAreDropped()
    {
        var s = new AppSettings().WithGuests(new[] { new Golfer(" Mike "), new Golfer("  "), new Golfer("mike", true), new Golfer("Ann") });
        Assert.Equal(new[] { new Golfer("Mike"), new Golfer("Ann") }, s.Guests);
    }

    [Fact]
    public void TheHomePlayerIsNotAlsoAGuest()
    {
        var s = new AppSettings { Home = new HomePlayer("Jared") }.WithGuests(new[] { new Golfer("jared"), new Golfer("Ann") });
        Assert.Equal(new[] { new Golfer("Ann") }, s.Guests);
        // Nor after being renamed to a guest's name.
        var renamed = s.WithHome(new HomePlayer("Ann"));
        Assert.Empty(renamed.Guests);
    }

    [Fact]
    public void SavingAGuestAddsThemOrUpdatesTheirHand()
    {
        var s = new AppSettings().WithGuest(new Golfer("Mike")).WithGuest(new Golfer("Ann"));
        Assert.Equal(new[] { new Golfer("Mike"), new Golfer("Ann") }, s.Guests);
        Assert.Equal(new[] { new Golfer("Mike", true), new Golfer("Ann") }, s.WithGuest(new Golfer("Mike", true)).Guests);
    }

    [Fact]
    public void WithNoHomePlayerThePickListIsJustTheGuests() =>
        Assert.Equal(new[] { new Golfer("Ann") }, new AppSettings().WithGuests(new[] { new Golfer("Ann") }).SavedPlayers);

    [Theory]
    [InlineData("3.1", "Square Omni", "Square Omni (hardware 3.1)")]
    [InlineData("1.5", "Square Golf", "Square Golf (hardware 1.5)")]
    [InlineData(null, "Square Golf", "Square Golf")]
    [InlineData("odd", "Square Golf", "Square Golf (hardware odd)")]
    public void TheMonitorTypeComesFromItsHardwareVersion(string? hardware, string model, string description)
    {
        var info = new SquareDeviceInfo("S1", hardware, null);
        Assert.Equal(model, info.Model);
        Assert.Equal(description, info.Description);
    }
}

public class ClubDistanceTests
{
    private static double Yards(double metres) => metres * Units.MetresToYards;
    private static ClubPreset Club(string code) => ClubPresets.Find(code)!;
    private static int Typical(string code) => (int)Math.Round(Yards(Caddie.Carry(code)));

    [Fact]
    public void NothingKnownIsTheTypicalCarry()
    {
        foreach (var club in ClubPresets.All)
        {
            Assert.Equal(Caddie.Carry(club.Code), Caddie.Carry(club.Code, null));
            Assert.Equal(Caddie.Carry(club.Code), Caddie.Carry(club.Code, new Dictionary<string, int>()));
        }
    }

    [Fact]
    public void AKnownCarryIsUsedAsGiven()
    {
        var known = new Dictionary<string, int> { ["I7"] = 137 };
        Assert.Equal(137, Yards(Caddie.Carry("I7", known)), 6);
    }

    [Fact]
    public void ClubsNotGivenFollowTheOnesThatAre()
    {
        // Someone who hits everything 20% shorter than typical, who only knows two clubs.
        var known = new Dictionary<string, int> { ["W3"] = (int)(Typical("W3") * 0.8), ["PW"] = (int)(Typical("PW") * 0.8) };
        foreach (string code in new[] { "DR", "I5", "I7", "LW" })
            Assert.InRange(Yards(Caddie.Carry(code, known)), Typical(code) * 0.77, Typical(code) * 0.83);
    }

    [Fact]
    public void BetweenTwoKnownClubsTheEstimateBlends()
    {
        var known = new Dictionary<string, int> { ["I6"] = (int)(Typical("I6") * 0.8), ["I8"] = (int)(Typical("I8") * 1.0) };
        Assert.InRange(Yards(Caddie.Carry("I7", known)), Typical("I7") * 0.87, Typical("I7") * 0.93);
    }

    [Fact]
    public void EstimatesKeepTheBagInOrder()
    {
        // One known club, far from typical: the estimates mustn't leave the next club out-hitting it.
        var known = new Dictionary<string, int> { ["I7"] = Typical("I7") - 30 };
        var carries = ClubPresets.All.Where(c => c.Code != "PT").Select(c => Caddie.Carry(c.Code, known)).ToList();
        for (int i = 1; i < carries.Count; i++) Assert.True(carries[i] < carries[i - 1], $"{ClubPresets.All[i].Code} carries further than the club above it");
    }

    [Fact]
    public void ThePutterHasNoCarry() => Assert.Equal(0, Caddie.Carry("PT", new Dictionary<string, int> { ["PT"] = 30, ["I7"] = 150 }));

    [Fact]
    public void AClubOutOfTheBagOrABlankValueIsIgnored()
    {
        var known = new Dictionary<string, int> { ["XX"] = 300, ["I7"] = 0 };
        Assert.Equal(Caddie.Carry("I7"), Caddie.Carry("I7", known));
    }

    [Fact]
    public void TheCaddieSuggestsFromThePlayersDistances()
    {
        var stock = Caddie.Suggest(Caddie.Carry("I7"), Lie.Fairway);
        Assert.Equal("I7", stock);
        // A shorter hitter needs more club for the same shot.
        var known = new Dictionary<string, int> { ["I7"] = Typical("I7") - 30 };
        Assert.Equal("I7", Caddie.Suggest(Caddie.Carry("I7", known), Lie.Fairway, null, known));
        int longer = ClubPresets.IndexOf(Caddie.Suggest(Caddie.Carry("I7"), Lie.Fairway, null, known));
        Assert.True(longer < ClubPresets.IndexOf("I7"), "should take more club than a 7 iron");
    }

    [Fact]
    public void ALieTakesThePlayersCarryDownByTheSameShare()
    {
        var known = new Dictionary<string, int> { ["I7"] = 140 };
        var rough = new LieCondition(LieKind.DeepRough);
        double share = Caddie.CarryFrom("I7", rough) / Caddie.Carry("I7");
        Assert.True(share < 1, "deep rough should cost distance");
        Assert.Equal(140 * share, Yards(Caddie.CarryFrom("I7", rough, known)), 6);
    }

    [Fact]
    public void PowerIsAShareOfThePlayersCarry()
    {
        var known = new Dictionary<string, int> { ["I7"] = 140 };
        Assert.Equal(0.5, Caddie.PowerFor("I7", 70 * Units.YardsToMetres, null, known), 6);
    }

    [Theory]
    [InlineData(-40)]   // shorter than typical
    [InlineData(25)]    // further than typical
    public void AFullSwingFromTheScreenGoesThePlayersDistance(int yardsOff)
    {
        int yards = Typical("I7") + yardsOff;
        var known = new Dictionary<string, int> { ["I7"] = yards };
        Assert.InRange(Caddie.SwingShot(Club("I7"), new Swing(1), known: known).Fly().CarryYards, yards * 0.97, yards * 1.03);
    }

    [Fact]
    public void APartialSwingFromTheScreenIsAShareOfThePlayersDistance()
    {
        var known = new Dictionary<string, int> { ["I7"] = 140 };
        Assert.InRange(Caddie.SwingShot(Club("I7"), new Swing(0.7), known: known).Fly().CarryYards, 98 * 0.95, 98 * 1.05);
    }

    [Fact]
    public void WithNoDistancesAFullSwingIsTheClubsOwn()
    {
        var club = Club("I7");
        var s = Caddie.SwingShot(club, new Swing(1), known: new Dictionary<string, int>());
        Assert.Equal(club.BallMph, s.BallMph);
        Assert.Equal(club.SpinRpm, s.SpinRpm);
    }
}
