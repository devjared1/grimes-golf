namespace GrimesGolf.Core.Course;

/// <summary>
/// Friendly rules for a round. The defaults are strict (no mulligans, 2 ft tap-ins, hole out every hole).
/// </summary>
/// <param name="Mulligans">Shots per round that can be taken again without counting.</param>
/// <param name="GimmeMetres">Putts finishing this close are tapped in for one more stroke (0 = hole everything out).</param>
/// <param name="PickUpAtDoublePar">Stop a hole once the strokes reach twice its par, and score it as double par.</param>
public sealed record HouseRules(int Mulligans = 0, double GimmeMetres = HouseRules.DefaultGimmeMetres, bool PickUpAtDoublePar = false)
{
    /// <summary>About 2 ft.</summary>
    public const double DefaultGimmeMetres = 0.6;

    public static readonly HouseRules Strict = new();

    /// <summary>Most strokes a hole can count under these rules (null = no limit).</summary>
    public int? MaxScore(int par) => PickUpAtDoublePar ? 2 * par : null;
}
