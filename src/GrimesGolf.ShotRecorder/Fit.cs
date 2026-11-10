using System.Globalization;
using GrimesGolf.Core;

namespace GrimesGolf.ShotRecorder;

/// <summary>
/// Searches tuning values that bring our flights closest to the reference simulator's numbers for a set of captured
/// shots. Two stages, because they don't interact: first the air (carry and apex), then the fairway's bounce and roll
/// (how far each ball runs after landing, measured as total minus carry, so a carry still off doesn't count twice).
/// Every value stays in its tuner range, and a small pull toward the starting value keeps settings the shots say
/// little about where they were.
/// </summary>
public static class Fit
{
    public static readonly string[] AirKeys = { "flight.drag", "flight.lift", "flight.spinDecay", "flight.dragSpeedTilt" };

    public static readonly string[] GroundKeys =
    {
        "ground.fairway.bounce", "ground.fairway.crater", "ground.fairway.firmness", "ground.fairway.friction",
        "ground.fairway.grab", "ground.fairway.skid", "ground.fairway.drag", "ground.fairway.rolling",
    };

    /// <summary>Weight of the pull back toward the starting values (per parameter, in units of its whole range).</summary>
    private const double Anchor = 2.0;

    /// <summary>Air error: carry in yards, apex in feet counted a third (so a yard of apex weighs as a yard of carry).</summary>
    public static double AirError(IEnumerable<ComparedShot> shots) =>
        shots.Sum(s => Sq(s.DCarry) + Sq(s.DApex / 3));

    public static readonly string[] GreenKeys =
    {
        "ground.green.bounce", "ground.green.crater", "ground.green.firmness", "ground.green.friction",
        "ground.green.grab", "ground.green.skid", "ground.green.drag",   // not the stimp: that's the green's speed, set per course
    };

    /// <summary>Clubs whose high-spin shots should check and spin back, whatever the reference simulator does.</summary>
    public static readonly string[] Wedges = { "PW", "GW", "AW", "SW", "LW" };

    /// <summary>Backspin from which a wedge counts as a full, spinning wedge (full PW to LW is 9-11k; pitches are 3-6k).</summary>
    public const double SpinBackRpm = 9000;

    /// <summary>A run-out range to land in (yards; negative = spun back).</summary>
    public sealed record Band(double Min, double Max)
    {
        public double Miss(double v) => Math.Max(0, Min - v) + Math.Max(0, v - Max);
        public override string ToString() => $"{Min:+0;-0;0} … {Max:+0;-0;0}";
    }

    /// <summary>
    /// GSPro's run-outs are copied for most shots, but not where it plays differently from the real thing (judged by
    /// the user): full wedges check and may spin back a little instead of releasing, and irons release 4-5 yards, not 10.
    /// Null: match GSPro.
    /// </summary>
    public static Band? FairwayTarget(ComparedShot s) => IsFullWedge(s) ? new(-3, 1) : IsIron(s) ? new(4, 5) : null;

    /// <summary>On the green (GSPro's numbers are off the range, so none of them apply): full wedges spin back, pitches
    /// check and release a little, irons release a little more. Null: no target.</summary>
    public static Band? GreenTarget(ComparedShot s) =>
        IsFullWedge(s) ? new(-5, -1) : IsIron(s) ? new(2, 8) : s.Launch.SpinRpm is >= 4000 and <= 7000 ? new(2, 6) : null;

    public static bool IsFullWedge(ComparedShot s) => Wedges.Contains(s.Shot.ClubCode) && s.Launch.SpinRpm >= SpinBackRpm;
    public static bool IsIron(ComparedShot s) => s.Shot.ClubCode is ['I', >= '1' and <= '9'];

    public static double RunOut(FlightResult f) => f.TotalYards - f.CarryYards;

    /// <summary>Fairway error, in yards of run-out (total minus carry): how far ours misses its <see cref="FairwayTarget"/>, or GSPro's.</summary>
    public static double GroundError(IEnumerable<ComparedShot> shots) => shots.Sum(s =>
    {
        double ours = RunOut(s.Ours);
        if (FairwayTarget(s) is { } band) return Sq(band.Miss(ours));
        return s.Reference is { CarryYds: { } c, TotalYds: { } t } ? Sq(t - c - ours) : 0;
    });

    /// <summary>Green error: how far each run-out on the green misses its <see cref="GreenTarget"/>.</summary>
    public static double GreenError(IEnumerable<(ComparedShot Shot, double RunOut)> shots) =>
        shots.Sum(x => GreenTarget(x.Shot) is { } band ? Sq(band.Miss(x.RunOut)) : 0);

    /// <summary>
    /// Coordinate search: tries each value up and down by a step, keeps whatever lowers the error, and halves the
    /// steps when nothing does, until they're down to the tuner's own step.
    /// </summary>
    public static PhysicsTuning Search(PhysicsTuning start, IReadOnlyList<string> keys, Func<PhysicsTuning, double> error,
                                       Action<string>? progress = null)
    {
        var p = keys.Select(PhysicsTuning.Parameter).ToArray();
        double Penalty(PhysicsTuning t) => Anchor * p.Sum(q => Sq((t[q.Key] - start[q.Key]) / (q.Max - q.Min)));
        double Score(PhysicsTuning t) => error(t) + Penalty(t);

        var best = start;
        double bestScore = Score(best);
        var step = p.Select(q => (q.Max - q.Min) / 8).ToArray();
        for (int round = 0; round < 60 && step.Select((s, i) => s > p[i].Step).Any(b => b); round++)
        {
            bool improved = false;
            for (int i = 0; i < p.Length; i++)
            {
                if (step[i] <= p[i].Step) continue;
                foreach (int dir in new[] { 1, -1 })
                {
                    var t = best.With(p[i].Key, best[p[i].Key] + dir * step[i]);
                    if (t[p[i].Key] == best[p[i].Key]) continue;   // already at the end of its range
                    double sc = Score(t);
                    if (sc < bestScore - 1e-9) { best = t; bestScore = sc; improved = true; break; }
                }
            }
            if (!improved) for (int i = 0; i < step.Length; i++) step[i] /= 2;
            progress?.Invoke($"  error {error(best):0.0}");
        }
        // Round to the tuner's steps, as it would show and save them.
        foreach (var q in p) best = best.With(q.Key, Math.Round(best[q.Key] / q.Step) * q.Step);
        return best;
    }

    public static string Changes(PhysicsTuning before, PhysicsTuning after) =>
        string.Join(Environment.NewLine, after.ChangesFrom(before).Select(c =>
            $"  {c.Parameter.Key,-26} {c.Parameter.Format(c.Before),8} → {c.Parameter.Format(c.After),-8} {c.Parameter.Label}"));

    private static double Sq(double? v) => v is { } x ? x * x : 0;

    internal static string F(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);
}
