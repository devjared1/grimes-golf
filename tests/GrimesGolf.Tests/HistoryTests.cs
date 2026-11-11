using GrimesGolf.Core;
using GrimesGolf.Core.Course;
using GrimesGolf.Core.Storage;
using Xunit;

namespace GrimesGolf.Tests;

public class HistoryTests
{
    private static ShotRecord Shot(DateTime at, string player, string club, double carry, double total, bool demo = false) =>
        new(at, player, club, false, demo, 120, 16, 0, 6500, 0, null, carry, total, 0, 30);

    [Fact]
    public void ShotsAreGroupedByDayNewestFirstWithClubAveragesInBagOrder()
    {
        var mon = new DateTime(2026, 11, 9, 19, 0, 0);
        var tue = new DateTime(2026, 11, 10, 20, 0, 0);
        var shots = new[]
        {
            Shot(mon, "Jared", "I7", 160, 170),
            Shot(mon.AddMinutes(1), "Jared", "I7", 150, 162),
            Shot(mon.AddMinutes(2), "Jared", "DR", 240, 265),
            Shot(mon.AddMinutes(3), "jared", "PT", 0, 12),                        // putts don't count; same player
            Shot(mon.AddMinutes(4), "Jared", "I9", 999, 999, demo: true),         // on-screen swing: counted, not averaged
            Shot(mon.AddMinutes(5), "Mike", "PW", 110, 118) with { Course = "Deer Run" },
            Shot(mon.AddMinutes(6), "Jared", "I7", 999, 999) with { Replay = true },   // the tuner's replay: left out
            Shot(tue, "Jared", "SW", 80, 84),
        };

        var days = History.Days(shots);

        Assert.Equal(2, days.Count);
        Assert.Equal(tue.Date, days[0].Day);
        var monday = days[1];
        Assert.Equal(5, monday.MonitorShots);
        Assert.Equal(1, monday.ScreenShots);
        Assert.Equal(new[] { "Range", "Deer Run" }, monday.Places);
        Assert.Equal(new[] { "Jared", "Mike" }, monday.Players.Select(p => p.Player));

        var jared = monday.Players[0];
        Assert.Equal(5, jared.Shots);
        Assert.Equal(new[] { "DR", "I7" }, jared.Clubs.Select(c => c.Club));
        var seven = jared.Clubs[1];
        Assert.Equal("7 Iron", seven.Label);
        Assert.Equal(2, seven.Shots);
        Assert.Equal(155, seven.CarryYards);
        Assert.Equal(166, seven.TotalYards);
        Assert.Equal(170, seven.LongestYards);
    }

    [Fact]
    public void RoundTotalsCountOnlyTheHolesPlayed()
    {
        var round = new SavedRound("Test holes", DateTime.Now, DateTime.Now, HouseRules.Strict, new[] { 4, 3, 5 },
            new[]
            {
                new SavedRound.Card("Jared", false, new int?[] { 5, 3, 4 }, new SavedRound.Position(2, 0, 0, Lie.Green, 0, true, 4, false), 0),
                new SavedRound.Card("Mike", false, new int?[] { null, 4, 6 }, new SavedRound.Position(2, 0, 0, Lie.Green, 0, true, 6, false), 0, FirstHole: 1),
            }, 0);

        var totals = History.Totals(round);

        Assert.Equal(new RoundTotal("Jared", 12, 0, 3), totals[0]);
        Assert.Equal(new RoundTotal("Mike", 10, 2, 2), totals[1]);
        Assert.Equal(new[] { 1, 2, 3 }, History.HoleNumbers(round));
        Assert.Equal(new[] { 10, 11, 12 }, History.HoleNumbers(round with { HoleNumbers = new[] { 10, 11, 12 } }));
    }
}
