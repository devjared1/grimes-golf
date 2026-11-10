using System.Text.Json.Serialization;

namespace GrimesGolf.Core.Course;

/// <summary>
/// Friendly rules for a round. The defaults are strict (no mulligans, 2 ft tap-ins, hole out every hole).
/// </summary>
/// <param name="Mulligans">Shots per round that can be taken again without counting.</param>
/// <param name="GimmeMetres">
/// Radius of the gimme circle round the hole: putts finishing inside it are tapped in for one more stroke (0 = hole
/// everything out). The settings screen offers <see cref="GimmeFeetChoices"/>.
/// </param>
/// <param name="PickUpAtDoublePar">Stop a hole once the strokes reach twice its par, and score it as double par.</param>
/// <param name="AutoAlignPutts">On the green, aim along the putt's read (allowing for the break) rather than straight at the hole.</param>
/// <param name="AutoPutt">
/// Nobody putts: a ball that comes to rest on the green is holed out in the putts its distance from the hole calls for
/// (<see cref="AutoPutts"/>).
/// </param>
public sealed record HouseRules(int Mulligans = 0, double GimmeMetres = HouseRules.DefaultGimmeMetres, bool PickUpAtDoublePar = false,
                                bool AutoAlignPutts = false, bool AutoPutt = false)
{
    /// <summary>About 2 ft.</summary>
    public const double DefaultGimmeMetres = 0.6;

    /// <summary>The gimme circles the settings offer (ft), and the one a new player starts with.</summary>
    public static readonly int[] GimmeFeetChoices = { 4, 6, 8, 10, 12 };
    public const int DefaultGimmeFeet = 6;

    public static readonly HouseRules Strict = new();

    /// <summary>The gimme circle's radius in feet.</summary>
    [JsonIgnore] public double GimmeFeet => GimmeMetres / Units.FeetToMetres;

    /// <summary>Auto-putting's second circle round the hole (twice the gimme circle): inside it is two putts.</summary>
    [JsonIgnore] public double SecondCircleMetres => 2 * GimmeMetres;

    /// <summary>Most strokes a hole can count under these rules (null = no limit).</summary>
    public int? MaxScore(int par) => PickUpAtDoublePar ? 2 * par : null;

    /// <summary>
    /// Auto-putting: the putts a ball resting <paramref name="metres"/> from the hole takes. One inside the gimme
    /// circle, two inside the second circle, and two or three (even odds, from <paramref name="random"/>) beyond it.
    /// </summary>
    public int AutoPutts(double metres, Random random) =>
        metres <= GimmeMetres ? 1 : metres <= SecondCircleMetres ? 2 : random.Next(2, 4);

    /// <summary>"2 mulligans each, 6 ft gimmes, auto-putting, pick up at double par".</summary>
    public string Summary()
    {
        var parts = new List<string>
        {
            Mulligans > 0 ? $"{Mulligans} mulligan{(Mulligans == 1 ? "" : "s")} each" : "no mulligans",
            GimmeMetres > 0 ? $"{GimmeFeet:0.#} ft gimmes" : "hole everything out",
        };
        if (AutoPutt) parts.Add("auto-putting");
        else if (AutoAlignPutts) parts.Add("putts auto-aligned");
        if (PickUpAtDoublePar) parts.Add("pick up at double par");
        return string.Join(", ", parts);
    }
}
