namespace GrimesGolf.Core.Storage;

/// <summary>One club's numbers for a player on one day (launch monitor shots only).</summary>
public sealed record ClubStats(string Club, string Label, int Shots, double CarryYards, double TotalYards, double BallMph, double LongestYards);

/// <summary>What one player hit on one day.</summary>
/// <param name="Shots">Every shot, on-screen swings included.</param>
/// <param name="Clubs">Launch monitor shots per club, longest club first (putts left out).</param>
public sealed record PlayerDay(string Player, int Shots, IReadOnlyList<ClubStats> Clubs);

/// <summary>A day's saved shots.</summary>
/// <param name="Places">Where they were hit: "Range" and course names, in the order first played.</param>
public sealed record ShotDay(DateTime Day, int MonitorShots, int ScreenShots, IReadOnlyList<string> Places, IReadOnlyList<PlayerDay> Players);

/// <summary>A player's line for a finished round.</summary>
/// <param name="ToPar">Strokes against par on the holes they finished.</param>
public sealed record RoundTotal(string Player, int Strokes, int ToPar, int HolesPlayed);

/// <summary>The saved shots and rounds, summed up for the History screen.</summary>
public static class History
{
    /// <summary>The saved shots by day, newest first. The physics tuner's replays aren't real shots and are left out.</summary>
    public static IReadOnlyList<ShotDay> Days(IEnumerable<ShotRecord> shots) =>
        shots.Where(s => !s.Replay)
            .GroupBy(s => s.At.Date)
            .OrderByDescending(g => g.Key)
            .Select(g =>
            {
                var day = g.OrderBy(s => s.At).ToList();
                var places = day.Select(s => s.Course ?? "Range").Distinct().ToList();
                var players = day.GroupBy(s => s.Player, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new PlayerDay(p.First().Player, p.Count(), Clubs(p.Where(s => !s.Demo))))
                    .ToList();
                return new ShotDay(g.Key, day.Count(s => !s.Demo), day.Count(s => s.Demo), places, players);
            })
            .ToList();

    /// <summary>Averages per club, in bag order (unknown club codes last), putts left out.</summary>
    public static IReadOnlyList<ClubStats> Clubs(IEnumerable<ShotRecord> shots) =>
        shots.Where(s => !s.Club.Equals("PT", StringComparison.OrdinalIgnoreCase))
            .GroupBy(s => s.Club.ToUpperInvariant())
            .OrderBy(g => ClubPresets.IndexOf(g.Key) is var i and >= 0 ? i : int.MaxValue).ThenBy(g => g.Key)
            .Select(g => new ClubStats(g.Key, ClubPresets.Find(g.Key)?.Label ?? g.Key, g.Count(),
                g.Average(s => s.CarryYards), g.Average(s => s.TotalYards), g.Average(s => s.BallMph), g.Max(s => s.TotalYards)))
            .ToList();

    /// <summary>Each player's strokes and score against par in a round (only the holes they finished count).</summary>
    public static IReadOnlyList<RoundTotal> Totals(SavedRound round) =>
        round.Players.Select(p =>
        {
            var played = Enumerable.Range(0, Math.Min(p.Scores.Length, round.Pars.Length)).Where(i => p.Scores[i] != null).ToList();
            int strokes = played.Sum(i => p.Scores[i]!.Value);
            return new RoundTotal(p.Name, strokes, strokes - played.Sum(i => round.Pars[i]), played.Count);
        }).ToList();

    /// <summary>The hole numbers a round played, in order (rounds saved before they were kept: 1, 2, 3, ...).</summary>
    public static IReadOnlyList<int> HoleNumbers(SavedRound round) =>
        round.HoleNumbers is { } n && n.Length == round.Pars.Length ? n : Enumerable.Range(1, round.Pars.Length).ToArray();
}
