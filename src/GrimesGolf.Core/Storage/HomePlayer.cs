using System.Text.Json.Serialization;
using GrimesGolf.Core.Course;

namespace GrimesGolf.Core.Storage;

/// <summary>
/// The person whose simulator this is: saved in the settings and loaded as the player every time the app starts.
/// Guests are added on top of them in the settings, for as long as the app is open.
/// </summary>
/// <param name="Name">Their name.</param>
/// <param name="LeftHanded">Left-handed (the monitor is switched to this hand on their turn).</param>
public sealed record HomePlayer(string Name, bool LeftHanded = false)
{
    /// <summary>The launch monitor they use, read from the monitor itself when it connects (null = none seen yet).</summary>
    public string? LaunchMonitor { get; init; }

    /// <summary>The carries they know, in yards by club code (DR, I7, ...). Clubs left out are estimated from these.</summary>
    public IReadOnlyDictionary<string, int>? ClubYards { get; init; }

    [JsonIgnore] public Golfer Golfer => new(Name, LeftHanded);
}
