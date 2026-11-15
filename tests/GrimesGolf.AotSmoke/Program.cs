using System.Text;
using System.Text.Json;
using GrimesGolf.Core;
using GrimesGolf.Core.Course;
using GrimesGolf.Core.Monitors;
using GrimesGolf.Core.OpenConnect;
using GrimesGolf.Core.Storage;
using GrimesGolf.Online.Session;
using GrimesGolf.Online.Protocol;

// Saves and reads back every kind of JSON the app keeps or sends, and says which ones came back different or
// failed. Only worth running as a NativeAOT build (see the project file): an ordinary build always passes.
// Given a file name, it also writes the JSON there, so the NativeAOT build's can be compared with an ordinary
// build's (they must be the same: the iPad and Windows read each other's messages and files).

int failed = 0;
string folder = Path.Combine(Path.GetTempPath(), "grimes-golf-aot-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
var written = new StringBuilder();
var when = new DateTime(2026, 11, 14, 9, 30, 0);
Guid Id(int n) => new(n, 0, 0, new byte[8]);

void Check(string what, Func<(string Before, string After)> roundTrip)
{
    try
    {
        var (before, after) = roundTrip();
        written.AppendLine($"---- {what}").AppendLine(before);
        if (before == after && before.Length > 2) { Console.WriteLine($"ok    {what}"); return; }
        Console.WriteLine($"FAIL  {what}: came back different\n  before: {before}\n  after:  {after}");
    }
    catch (Exception ex) { Console.WriteLine($"FAIL  {what}: {ex.GetType().Name}: {ex.Message}"); }
    failed++;
}

string Text<T>(T value) => JsonSerializer.Serialize(value, typeof(T) == typeof(OnlineSave) ? OnlineSave.Json : GolfData.Json);

var rules = new HouseRules(Mulligans: 2, GimmeMetres: 1.8, PickUpAtDoublePar: true, AutoAlignPutts: true);
var setup = new GameSetup(GameType.Scramble, HoleSet.Back9, WindStrength.Breezy, AltitudeSetting.Course, Weather.Wet, FairwaySpeed.Fast, GreenFirmness.Firm, 11);
var shot = new RangeShot(151.2, 12.5, -1.5, 2650, 4.0, 104.9, 1.44) { Source = ShotSource.Monitor };
var position = new MatchSnapshot.Position(3, 201.5, -12.25, Lie.Bunker, 2, false, null, false);
var snapshot = new MatchSnapshot(1, 0, new[] { 1, 0 },
    new[]
    {
        new MatchSnapshot.Player(new int?[] { 4, 5, null }, position, 1, 0, position with { Lie = Lie.Fairway }, true, position, false),
        new MatchSnapshot.Player(new int?[] { 3, null, null }, position with { Holed = true, Score = 4 }, 0, 1, null, false, null, true),
    },
    new MatchSnapshot.WindState(12, 270, 18), new MatchSnapshot.Pick(0, 1, "Ann's drive"));
var seats = new[] { new Seat("pc-1", "Ann", false), new Seat("pc-2", "Bob", true) };
var config = new RoundConfig("Deer Run", "abc123", setup, rules, PhysicsTuning.Defaults.ToJson(), seats, when, new[] { 10, 11, 12 });
var outcome = new OutcomeData("AAAA", 12, 251.5, 268.0, 31.2, -4.5, 6.1, 38.0, 0, 0, 240, -4, Lie.Rough, 240, -4, false, false, 1, 3, "In the rough", "Sitting down", 7);
var result = new ShotResult(4, Id(1), 1, shot, "DR", 3.5, false, outcome, snapshot);

Check("settings.json", () =>
{
    var settings = AppSettings.Defaults with
    {
        Players = new[] { new Golfer("Ann"), new Golfer("Bob", true) }, HouseRules = rules, LastMonitor = 0xC0FFEE123456UL,
        TextSize = TextSize.Large, Graphics = GraphicsQuality.Ultra, Game = setup, Course = "Deer Run",
        Home = new HomePlayer("Ann") { LaunchMonitor = "Square", ClubYards = new Dictionary<string, int> { ["DR"] = 230, ["I7"] = 150 } },
        SavedGuests = new[] { new Golfer("Cat", true) }, MonitorType = LaunchMonitorType.GarminR10, OpenConnect = true,
        RejoinCode = "ABCD", RejoinUntil = when.AddHours(3),
    };
    string? error = null;
    settings.Save(folder, e => error = e);
    var back = AppSettings.Load(folder, e => error = e);
    if (error != null) throw new InvalidOperationException(error);
    return (Text(settings), Text(back));
});

var data = new GolfData(folder);
string? dataError = null;
data.Error += e => dataError = e;
(string, string) Stored(string before, Func<string?> after)
{
    dataError = null;
    string? back = after();
    if (dataError != null) throw new InvalidOperationException(dataError);
    return (before, back ?? "nothing came back");
}

Check("current-round.json", () =>
{
    var card = new SavedRound.Card("Ann", false, new int?[] { 4, null, 6 }, new SavedRound.Position(1, 120.5, 3.25, Lie.Green, 2, false, null, false), 1);
    var round = new SavedRound("Deer Run", when, when.AddHours(1), rules, new[] { 4, 3, 5 }, new[] { card, card with { Name = "Bob", LeftHanded = true, FirstHole = 1 } }, 1)
    {
        Setup = setup, HoleNumbers = new[] { 10, 11, 12 },
    };
    data.SaveRound(round);
    return Stored(Text(round), () => data.LoadCurrentRound() is { } back ? Text(back) : null);
});

Check("sessions (one shot per line)", () =>
{
    var record = new ShotRecord(when, "Ann", "I7", false, false, 110.4, 17.2, 1.5, 6400, -3.0, 82.1, 148.5, 153.0, 2.5, 26.0)
    {
        Course = "Deer Run", Hole = 4, Stroke = 2, Lie = Lie.Fairway, RestLie = Lie.Green, Result = "On the green", Mulligan = true,
    };
    data.AppendShot(record);
    return Stored(Text(record), () => data.LoadShots().LastOrDefault() is { } back ? Text(back) : null);
});

Check("online-round.json", () =>
{
    var save = new OnlineSave("ABCD", "token", config, snapshot, 7, when);
    data.SaveOnlineRound(save, OnlineSave.Json);
    return Stored(Text(save), () => data.LoadOnlineRound<OnlineSave>(OnlineSave.Json) is { } back ? Text(back) : null);
});

Check("match snapshot", () => (snapshot.ToJson(), MatchSnapshot.FromJson(snapshot.ToJson()).ToJson()));

var lobby = new LobbyState("ABCD", new[] { new PcInfo("pc-1", "Ann's PC", true, true, false) }, seats, "Deer Run", setup, rules, false, "Waiting for Bob", false);
OnlineMessage[] messages =
{
    new Hello(OnlineProtocol.Version, "1.0.0", "pc-2", seats, true), new Status(true, "abc123"),
    new ShotRequest(Id(2), 4, 1, shot, "DR", 3.5, false), new ShotShown(4), new Aim(4, 1, -2.5, "I7", 0.8),
    new MulliganRequest(4), new Bye(), new LobbyUpdate(lobby), new Refused("The lobby is full"),
    new RoundStart(config, snapshot, 1), result, new TurnOpen(5, snapshot, TurnReason.NextHole),
    new Sync(config, 5, snapshot, true, result), new AwayNotice(1, true, 42.5, "Bob dropped out"),
    new Notice("Ann took a mulligan", Id(3)), new RoundOver(false, "Ann wins", snapshot),
};
foreach (var message in messages)
    Check($"online message: {message.GetType().Name}", () =>
    {
        string sent = message.Encode();
        var back = OnlineMessage.Decode(sent) ?? throw new InvalidOperationException("It didn't decode.");
        if (back.GetType() != message.GetType()) throw new InvalidOperationException($"It decoded as {back.GetType().Name}.");
        return (sent, back.Encode());
    });

Check("Open Connect shot (in)", () =>
{
    const string sent = """{"DeviceID":"R10","Units":"Yards","ShotNumber":3,"APIversion":"1","BallData":{"Speed":"147.5","SpinAxis":-3.2,"TotalSpin":2800,"HLA":1.5,"VLA":13.2},"ClubData":{"Speed":101.2,"Path":2.1},"ShotDataOptions":{"ContainsBallData":true,"ContainsClubData":true,"IsHeartBeat":false}}""";
    var message = OpenConnectProtocol.Parse(sent) ?? throw new InvalidOperationException("It didn't parse.");
    string read = FormattableString.Invariant($"{message.DeviceID} {message.ShotNumber} {message.IsShot} {message.BallData?.Speed} {message.BallData?.VLA} {message.ClubData?.Path} {message.ShotDataOptions?.IsHeartBeat}");
    return ("R10 3 True 147.5 13.2 2.1 False", read);
});

Check("Open Connect reply (out)", () =>
    ("""{"Code":201,"Message":"Player info","Player":{"Handed":"LH","Club":"DR","DistanceToTarget":250}}""",
     OpenConnectProtocol.Serialize(new OpenConnectResponse(OpenConnectProtocol.PlayerInformation, "Player info", new OpenConnectPlayer("LH", "DR", 250)))));

Check("course catalog", () =>
{
    var courses = new[] { new CatalogCourse("deer-run", "Deer Run", "Somewhere", "A course", 3, "deer-run.zip", 7_000_000, new string('a', 64), 18, 72, 6500) };
    string json = CourseCatalog.ToJson(courses);
    return (json, CourseCatalog.ToJson(CourseCatalog.Parse(Encoding.UTF8.GetBytes(json))));
});

Check("physics tuning preset", () =>
{
    var tuning = PhysicsTuning.Defaults.With(PhysicsTuning.Parameters[0].Key, PhysicsTuning.Parameters[0].Max).Named("Smoke test");
    return (tuning.ToJson(), PhysicsTuning.FromJson(tuning.ToJson()).ToJson());
});

try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
if (args.Length > 0) File.WriteAllText(args[0], written.ToString());
Console.WriteLine(failed == 0 ? "All JSON round trips passed." : $"{failed} JSON round trip(s) failed.");
return failed == 0 ? 0 : 1;
