using System.Text.Json;
using System.Text.Json.Serialization;
using SquareGspro.Core.Course;

namespace SquareGspro.Core.Storage;

/// <summary>One shot as it's kept on disk: what the monitor measured, what the app made of it, and where it was played.</summary>
public sealed record ShotRecord(
    DateTime At, string Player, string Club, bool LeftHanded, bool Demo,
    double BallMph, double LaunchDeg, double DirectionDeg, double SpinRpm, double SpinAxisDeg, double? ClubMph,
    double CarryYards, double TotalYards, double OfflineYards, double ApexYards)
{
    /// <summary>Course name (null on the range).</summary>
    public string? Course { get; init; }
    public int? Hole { get; init; }
    public int? Stroke { get; init; }
    /// <summary>The lie it was played from and the one it finished in.</summary>
    public Lie? Lie { get; init; }
    public Lie? RestLie { get; init; }
    public string? Result { get; init; }
    /// <summary>The physics tuner's replay of an earlier shot (same monitor numbers, new physics).</summary>
    public bool Replay { get; init; }
    /// <summary>
    /// Taken back with a mulligan (it didn't count). The shot is written again with this set, at the same
    /// <see cref="At"/>; <see cref="GolfData.LoadShots"/> keeps the later line.
    /// </summary>
    public bool Mulligan { get; init; }

    public static ShotRecord Of(RangeShot s, FlightResult f, string player, string club, bool leftHanded) =>
        new(DateTime.Now, player, club, leftHanded, s.IsDemo, s.BallMph, s.LaunchDeg, s.DirectionDeg, s.SpinRpm, s.SpinAxisDeg,
            s.ClubMph, f.CarryYards, f.TotalYards, f.LateralYards, f.ApexYards);

    /// <summary>The record for a shot, from the <see cref="ShotFinished"/> event.</summary>
    public static ShotRecord Of(ShotFinished e) =>
        Of(e.Shot, e.Flight, e.Player.Name, e.Club, e.Player.LeftHanded) with
        {
            At = e.At, Course = e.Course, Hole = e.Hole?.Number, Stroke = e.Stroke, Lie = e.Lie, RestLie = e.Outcome?.RestLie,
            Result = e.Outcome?.Message, Replay = e.Replay,
        };
}

/// <summary>
/// A round (a <see cref="Match"/> of 1-4 players) in progress or finished: the course, house rules, whose turn it is
/// and, per player, each hole's score and where their ball is, so it can be picked up again after the app is closed
/// or crashes. Finished rounds are the saved scorecards.
/// </summary>
public sealed record SavedRound(
    string Course, DateTime Started, DateTime Updated, HouseRules Rules, int[] Pars, SavedRound.Card[] Players, int Turn)
{
    public sealed record Position(int HoleIndex, double BallX, double BallZ, Lie Lie, int Strokes, bool Holed, int? Score, bool PickedUp);

    /// <summary>One player's scorecard and ball.</summary>
    /// <param name="FirstHole">The first hole they played (0 unless they joined part-way through).</param>
    public sealed record Card(string Name, bool LeftHanded, int?[] Scores, Position Current, int MulligansLeft, int FirstHole = 0)
    {
        [JsonIgnore] public int Total => Scores.Sum(s => s ?? 0);
    }

    [JsonIgnore] public bool Complete => Players.All(p => p.Scores.Skip(p.FirstHole).All(s => s != null));
    [JsonIgnore] public IReadOnlyList<Golfer> Golfers => Players.Select(p => new Golfer(p.Name, p.LeftHanded)).ToList();

    public static SavedRound From(Match match, string course, DateTime started) =>
        new(course, started, DateTime.Now, match.Rules, match.Holes.Select(h => h.Par).ToArray(),
            match.Players.Select((g, i) =>
            {
                var r = match.Rounds[i];
                var s = r.Save();
                return new Card(g.Name, g.LeftHanded, r.Scores.ToArray(),
                    new Position(s.HoleIndex, s.Ball.X, s.Ball.Z, s.Lie, s.Strokes, s.Holed, s.Score, s.PickedUp), r.MulligansLeft, r.FirstHole);
            }).ToArray(),
            match.Turn);

    /// <summary>Puts <paramref name="match"/> (a new match on the same course with <see cref="Golfers"/>) back where this one was.</summary>
    public void ApplyTo(Match match) =>
        match.Resume(Turn, Players.Select(p =>
        {
            var c = p.Current;
            return ((IReadOnlyList<int?>)p.Scores, new HoleRound.State(c.HoleIndex, new P2(c.BallX, c.BallZ), c.Lie, c.Strokes, c.Holed, c.Score, c.PickedUp),
                    p.MulligansLeft, p.FirstHole);
        }).ToList());

    /// <summary>Whether this save fits a course with these holes (the course data may have changed since).</summary>
    public bool Fits(IReadOnlyList<Hole> holes) =>
        holes.Count == Pars.Length && holes.Select(h => h.Par).SequenceEqual(Pars) && Players.Length is >= 1 and <= Match.MaxPlayers
        && Players.All(p => p.Current.HoleIndex < holes.Count && p.Current.HoleIndex == Players[0].Current.HoleIndex);
}

/// <summary>
/// Everything the app keeps between runs, as JSON files under one folder:
/// <list type="bullet">
/// <item><c>current-round.json</c>: the round being played (rewritten after every shot; resumed at start-up).</item>
/// <item><c>rounds/</c>: finished rounds, one file each.</item>
/// <item><c>sessions/</c>: every shot, one JSON line each, a file per app session.</item>
/// </list>
/// Writes never throw: a full disk or a locked file shouldn't stop a shot. Problems go to <see cref="Error"/>.
/// </summary>
public sealed class GolfData
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private static readonly JsonSerializerOptions JsonLine = new(Json) { WriteIndented = false };

    private readonly string _sessionFile;

    public GolfData(string folder, DateTime? sessionStart = null)
    {
        Folder = folder;
        _sessionFile = Path.Combine(folder, "sessions", $"{sessionStart ?? DateTime.Now:yyyy-MM-dd_HHmmss}.jsonl");
    }

    public string Folder { get; }
    public string CurrentRoundFile => Path.Combine(Folder, "current-round.json");
    public string RoundsFolder => Path.Combine(Folder, "rounds");
    public string SessionFile => _sessionFile;

    /// <summary>Raised (with a message) when something couldn't be read or written.</summary>
    public event Action<string>? Error;

    public void AppendShot(ShotRecord shot) =>
        Try("save the shot", () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_sessionFile)!);
            File.AppendAllText(_sessionFile, JsonSerializer.Serialize(shot, JsonLine) + Environment.NewLine);
        });

    /// <summary>Saves the round in progress; a finished one is also filed under <see cref="RoundsFolder"/> and stops being current.</summary>
    public void SaveRound(SavedRound round) =>
        Try("save the round", () =>
        {
            if (!round.Complete) { WriteAtomic(CurrentRoundFile, JsonSerializer.Serialize(round, Json)); return; }
            Directory.CreateDirectory(RoundsFolder);
            WriteAtomic(Path.Combine(RoundsFolder, $"{round.Started:yyyy-MM-dd_HHmm} {Safe(round.Course)}.json"),
                JsonSerializer.Serialize(round, Json));
            File.Delete(CurrentRoundFile);
        });

    /// <summary>The round left unfinished last time, if there is one (and it reads).</summary>
    public SavedRound? LoadCurrentRound()
    {
        SavedRound? r = null;
        Try("read the saved round", () =>
        {
            if (File.Exists(CurrentRoundFile)) r = JsonSerializer.Deserialize<SavedRound>(File.ReadAllText(CurrentRoundFile), Json);
        });
        return r;
    }

    public void ClearCurrentRound() => Try("clear the saved round", () => File.Delete(CurrentRoundFile));

    /// <summary>Finished rounds a player was in, newest first.</summary>
    public IReadOnlyList<SavedRound> LoadRounds(string player) =>
        LoadRounds().Where(r => r.Players.Any(p => p.Name.Equals(player, StringComparison.OrdinalIgnoreCase))).ToList();

    /// <summary>Finished rounds, newest first.</summary>
    public IReadOnlyList<SavedRound> LoadRounds()
    {
        var list = new List<SavedRound>();
        if (!Directory.Exists(RoundsFolder)) return list;
        foreach (var f in Directory.GetFiles(RoundsFolder, "*.json"))
            Try($"read {Path.GetFileName(f)}", () =>
            {
                if (JsonSerializer.Deserialize<SavedRound>(File.ReadAllText(f), Json) is { } r) list.Add(r);
            });
        return list.OrderByDescending(r => r.Started).ToList();
    }

    /// <summary>Every shot saved in every session, oldest first (a shot taken back by a mulligan appears once, marked).</summary>
    public IReadOnlyList<ShotRecord> LoadShots()
    {
        var list = new List<ShotRecord>();
        var byTime = new Dictionary<(DateTime, string), int>();
        string dir = Path.GetDirectoryName(_sessionFile)!;
        if (!Directory.Exists(dir)) return list;
        foreach (var f in Directory.GetFiles(dir, "*.jsonl").Order())
            Try($"read {Path.GetFileName(f)}", () =>
            {
                foreach (var line in File.ReadLines(f))
                {
                    if (line.Length == 0) continue;
                    ShotRecord? s;
                    try { s = JsonSerializer.Deserialize<ShotRecord>(line, Json); }
                    catch (JsonException) { continue; }   // a line cut short by a crash
                    if (s == null) continue;
                    if (s.Mulligan && byTime.TryGetValue((s.At, s.Player), out int i)) { list[i] = s; continue; }
                    byTime[(s.At, s.Player)] = list.Count;
                    list.Add(s);
                }
            });
        return list;
    }

    /// <summary>Writes a temporary file and swaps it in, so a crash mid-write leaves the old file whole.</summary>
    private static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    private static string Safe(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();

    private void Try(string what, Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Error?.Invoke($"Couldn't {what}: {ex.Message}");
        }
    }
}
