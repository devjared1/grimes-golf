using System.Collections.Concurrent;
using GrimesGolf.Physics;

namespace GrimesGolf.Core.Course;

/// <summary>Shot shape for a shot hit from the screen: a draw curves toward the golfer's back foot side (left for a right-hander).</summary>
public enum ShotShape { Draw, Straight, Fade }

/// <summary>Spin for a shot hit from the screen: low flies lower with less backspin, high higher with more.</summary>
public enum SpinLevel { Low, Medium, High }

/// <summary>
/// A shot hit from the screen (no launch monitor): <see cref="Power"/> 0.01-1 (1 = full swing), a shape and a spin level.
/// </summary>
public sealed record Swing(double Power, ShotShape Shape = ShotShape.Straight, SpinLevel Spin = SpinLevel.Medium);

/// <summary>Club suggestions and demo shots for course play.</summary>
public static class Caddie
{
    private static readonly ConcurrentDictionary<string, double> CarryMetres = new();
    private static readonly ConcurrentDictionary<(string, LieKind, int, int, bool), double> LieCarries = new();
    private static int _physicsVersion;

    /// <summary>Typical carry of a club in metres from a clean, level lie in still air (0 for the putter).</summary>
    public static double Carry(string code)
    {
        Refresh();
        if (code == "PT" || ClubPresets.Find(code) is not { } preset) return 0;
        return CarryMetres.GetOrAdd(code, _ => FlyCarry(Launch(preset)));
    }

    /// <summary>
    /// Typical carry of a club in metres from <paramref name="lie"/>: the club's typical launch adjusted by
    /// <see cref="LieModel"/> (rough and sand cost distance; uphill lies launch higher). Slopes are rounded to half a degree.
    /// </summary>
    public static double CarryFrom(string code, LieCondition? lie)
    {
        if (lie == null || code == "PT") return Carry(code);
        var preset = ClubPresets.Find(code);
        if (preset == null) return 0;
        var model = ActivePhysics.LieModel;
        if (model.ResponseFor(lie.Kind) == LieResponse.None && lie.IsLevel) return Carry(code);
        Refresh();
        int up = (int)Math.Round(lie.UphillDegrees * 2), side = (int)Math.Round(lie.BallAboveFeetDegrees * 2);
        return LieCarries.GetOrAdd((code, lie.Kind, up, side, lie.LeftHanded), _ =>
            FlyCarry(model.Apply(Launch(preset), lie with { UphillDegrees = up / 2.0, BallAboveFeetDegrees = side / 2.0 }, preset.LoftDeg).Launch));
    }

    /// <summary>
    /// A player's carry with a club in metres, from the carries they know (<paramref name="known"/>, yards by club code).
    /// A club they haven't given is estimated: its typical carry, scaled by how the clubs either side of it compare
    /// with theirs. With nothing known it's the typical carry (0 for the putter either way).
    /// </summary>
    public static double Carry(string code, IReadOnlyDictionary<string, int>? known)
    {
        double typical = Carry(code);
        if (known is not { Count: > 0 } || typical <= 0) return typical;
        double Scale(int i) =>
            known.TryGetValue(ClubPresets.All[i].Code, out int yards) && yards > 0 && Carry(ClubPresets.All[i].Code) is > 0 and var carry
                ? yards * Units.YardsToMetres / carry : 0;

        int at = ClubPresets.IndexOf(code);
        if (Scale(at) is > 0 and var own) return own * typical;
        int longer = at - 1, shorter = at + 1;
        while (longer >= 0 && Scale(longer) <= 0) longer--;
        while (shorter < ClubPresets.All.Count && Scale(shorter) <= 0) shorter++;
        if (longer < 0 && shorter >= ClubPresets.All.Count) return typical;
        if (longer < 0) return typical * Scale(shorter);
        if (shorter >= ClubPresets.All.Count) return typical * Scale(longer);
        double share = (at - longer) / (double)(shorter - longer);
        return typical * (Scale(longer) + (Scale(shorter) - Scale(longer)) * share);
    }

    /// <summary>A player's carry from <paramref name="lie"/>: their carry (above), less what the lie takes off the typical one.</summary>
    public static double CarryFrom(string code, LieCondition? lie, IReadOnlyDictionary<string, int>? known)
    {
        if (known is not { Count: > 0 }) return CarryFrom(code, lie);
        double typical = Carry(code);
        return typical <= 0 ? 0 : CarryFrom(code, lie) * Carry(code, known) / typical;
    }

    /// <summary>Forgets cached carries once the physics tuning has changed.</summary>
    private static void Refresh()
    {
        int v = ActivePhysics.Version;
        if (v == _physicsVersion) return;
        CarryMetres.Clear();
        LieCarries.Clear();
        PartialFactors.Clear();
        _physicsVersion = v;
    }

    private static LaunchConditions Launch(ClubPreset c) => LaunchConditions.FromDisplayUnits(c.BallMph, c.LaunchDeg, 0, c.SpinRpm, 0);

    private static double FlyCarry(LaunchConditions launch) =>
        BallFlightSimulator.Simulate(launch, ActivePhysics.Ball, ActivePhysics.StillAir, SimulationRequest.DefaultTerrain,
            SimulationOptions.Default with { EnableBounce = false, TrajectorySampleIntervalSeconds = 1 }).CarryDistance;

    /// <summary>
    /// The shortest club that carries the distance from this lie; the putter on (or just off) the green. No driver
    /// off the deck. <paramref name="stance"/> (if given) makes carries account for the lie and slope, and
    /// <paramref name="known"/> (if given) are the carries the player has set.
    /// </summary>
    public static string Suggest(double distanceMetres, Lie lie, LieCondition? stance = null, IReadOnlyDictionary<string, int>? known = null)
    {
        if (lie == Lie.Green || (lie == Lie.Fringe && distanceMetres < 20) || (lie is Lie.Fairway && distanceMetres < 12)) return "PT";
        // Long tee shots: driver, unless the target is well inside fairway-wood range.
        if (lie == Lie.Tee && distanceMetres > Carry("W3", known) * 0.85) return "DR";
        var usable = ClubPresets.All.Where(c => c.Code != "PT" && (lie == Lie.Tee || c.Code != "DR")).ToList();
        var fits = usable.Where(c => CarryFrom(c.Code, stance, known) >= distanceMetres * 0.97).OrderBy(c => CarryFrom(c.Code, stance, known)).FirstOrDefault();
        return (fits ?? usable.OrderByDescending(c => CarryFrom(c.Code, stance, known)).First()).Code;
    }

    /// <summary>A made-up putt struck at about <paramref name="speedMs"/> (e.g. from a putt read), slightly off line and pace.</summary>
    public static RangeShot DemoPutt(double speedMs, Random rng)
    {
        double mph = speedMs * (1 + (rng.NextDouble() * 2 - 1) * 0.06) * Units.MsToMph;
        return new RangeShot(mph, 1, (rng.NextDouble() * 2 - 1) * 0.8, 0, 0, mph, 1.0, IsDemo: true);
    }

    /// <summary>
    /// A made-up shot with the club toward a target: full swings are scaled down when the target is shorter than the
    /// club's carry, and putts are paced for the distance on the given lie. <paramref name="stance"/> (if given) is the
    /// lie and slope the shot is struck from: the swing is sized for what the club carries from there (as
    /// <see cref="Suggest"/> chose it), since <see cref="LieModel"/> takes its toll after the strike.
    /// </summary>
    public static RangeShot DemoShot(ClubPreset club, double targetMetres, Lie lie, Random rng, LieCondition? stance = null)
    {
        if (club.Code == "PT")
        {
            double decel = HoleRound.RollDecel(lie);
            double v = Math.Sqrt(2 * decel * (targetMetres + 0.25)) * (1 + (rng.NextDouble() * 2 - 1) * 0.06);
            double mph = v * Units.MsToMph;
            return new RangeShot(mph, 1, (rng.NextDouble() * 2 - 1) * 1.2, 0, 0, mph, 1.0, IsDemo: true);
        }

        var s = RangeShot.Demo(club, rng);
        double clean = Carry(club.Code), fromLie = CarryFrom(club.Code, stance);
        if (targetMetres >= fromLie * 0.95 || fromLie <= 0) return s;
        // A partial swing keeps the strike's random quality but is a slower swing with the spin and launch that go
        // with it. It's sized as if from a clean lie, for the distance the lie's losses will bring back to the target.
        double f = PartialSwingFactor(club, targetMetres * clean / fromLie);
        var p = PartialLaunch(club, f);
        double quality = s.BallMph / club.BallMph;
        return s with
        {
            BallMph = p.BallMph * quality, LaunchDeg = p.LaunchDeg * s.LaunchDeg / club.LaunchDeg, SpinRpm = p.SpinRpm * s.SpinRpm / club.SpinRpm,
            ClubMph = s.ClubMph * f,
        };
    }

    /// <summary>
    /// A shot hit from the screen (no randomness). <see cref="Swing.Power"/> 1 is a full swing; less is a partial swing
    /// sized to carry that share of the club's distance (chips and pitches to finish there). A draw starts right of the
    /// aim and curves back left (for a right-hander; mirrored for a left-hander), a fade the opposite, finishing near the
    /// aim line. Low spin launches lower with less backspin (more roll), high spin higher with more (stops quicker).
    /// The lie's effect is applied afterwards, as for any shot. With <paramref name="known"/> carries, a full swing goes
    /// the player's distance with the club rather than the typical one.
    /// </summary>
    public static RangeShot SwingShot(ClubPreset club, Swing swing, bool leftHanded = false, IReadOnlyDictionary<string, int>? known = null)
    {
        double p = Math.Clamp(swing.Power, 0.01, 1);
        double mph = club.BallMph, launch = club.LaunchDeg, spin = club.SpinRpm;
        double typical = Carry(club.Code), full = Carry(club.Code, known);
        if (typical > 0 && (p < FullSwingPower || Math.Abs(full - typical) > 0.25))
            (mph, launch, spin) = PartialLaunch(club, PartialSwingFactor(club, p * full));

        // Shape: positive = fade for a right-hander (starts left, curves right). Smaller swings curve less.
        double shape = swing.Shape switch { ShotShape.Draw => -1, ShotShape.Fade => 1, _ => 0 } * (leftHanded ? -1 : 1);
        double effort = 0.4 + 0.6 * p;
        double direction = -shape * ShapeStartDegrees * effort, axis = shape * ShapeCurveDegrees * effort;

        double s = swing.Spin switch { SpinLevel.Low => -1, SpinLevel.High => 1, _ => 0 };
        launch = Math.Max(0, launch + s * SpinLaunchDegrees);
        spin *= 1 + s * SpinShare;

        return new RangeShot(mph, launch, direction, spin, axis, mph / club.Smash, club.Smash, IsDemo: true);
    }

    /// <summary>
    /// A putt hit from the screen. <see cref="Swing.Power"/> 1 rolls <paramref name="fullScaleMetres"/> on flat ground of
    /// <paramref name="lie"/>, straight along the aim. Shape and spin don't apply.
    /// </summary>
    public static RangeShot SwingPutt(Swing swing, double fullScaleMetres, Lie lie)
    {
        double distance = Math.Clamp(swing.Power, 0, 1) * fullScaleMetres;
        double mph = Math.Sqrt(2 * HoleRound.RollDecel(lie) * distance) * Units.MsToMph;
        return new RangeShot(mph, 1, 0, 0, 0, mph, 1.0, IsDemo: true);
    }

    /// <summary>
    /// The power (0.01-1) that carries <paramref name="targetMetres"/> with <paramref name="club"/> from
    /// <paramref name="stance"/> (the inverse of <see cref="SwingShot"/>'s sizing; 1 if it's out of reach).
    /// </summary>
    public static double PowerFor(string club, double targetMetres, LieCondition? stance = null, IReadOnlyDictionary<string, int>? known = null)
    {
        double full = CarryFrom(club, stance, known);
        return full <= 0 ? 1 : Math.Clamp(targetMetres / full, 0.01, 1);
    }

    /// <summary>How far (m) a putt struck at <paramref name="speedMs"/> rolls on flat ground of <paramref name="lie"/>.</summary>
    public static double PuttRollMetres(double speedMs, Lie lie) => speedMs * speedMs / (2 * HoleRound.RollDecel(lie));

    /// <summary>The putter meter's full scale for a putt that needs to roll <paramref name="metres"/> on the flat: a round number of feet with the putt at about 70%.</summary>
    public static double PuttScaleMetres(double metres)
    {
        double feet = Math.Max(10, Math.Ceiling(metres / 0.7 * Units.MetresToYards * 3 / 5) * 5);
        return feet / 3 * Units.YardsToMetres;
    }

    /// <summary>Swings at or above this power are full swings.</summary>
    public const double FullSwingPower = 0.97;
    /// <summary>A full draw or fade starts this far off the aim and curves back with this much spin-axis tilt.</summary>
    private const double ShapeStartDegrees = 2, ShapeCurveDegrees = 7;
    /// <summary>Low / high spin: launch change and share of the club's spin taken off or added.</summary>
    private const double SpinLaunchDegrees = 1.5, SpinShare = 0.25;

    /// <summary>
    /// A less-than-full swing at <paramref name="f"/> of the club's full ball speed. Spin falls with the speed, but
    /// more slowly (a 40 yd sand wedge pitch spins about two thirds as much as a full one, a short chip under half),
    /// at about the full swing's launch, so short shots release on the green rather than stopping like a full wedge.
    /// </summary>
    private static (double BallMph, double LaunchDeg, double SpinRpm) PartialLaunch(ClubPreset c, double f) =>
        (c.BallMph * f, c.LaunchDeg, c.SpinRpm * Math.Pow(f, PartialSpinExponent));

    private const double PartialSpinExponent = 0.6;

    private static readonly ConcurrentDictionary<(string, int), double> PartialFactors = new();

    /// <summary>Targets up to this far (m) are chipped and pitched to finish there (carry and roll on a green)...</summary>
    private const double ShortGameMetres = 30;
    /// <summary>...and from this far on, flown there (carry), since the ball won't be landing on a green; in between, a blend.</summary>
    private const double ApproachMetres = 60;

    /// <summary>
    /// The partial-swing factor for a shot to <paramref name="targetMetres"/> on flat ground: a chip or pitch finishes
    /// there after rolling out on a green; a longer partial swing carries there (rolling out on a fast green would make
    /// it a much smaller swing, a long way short). A target past the club's typical carry (a player who hits it further)
    /// is a factor above 1.
    /// </summary>
    private static double PartialSwingFactor(ClubPreset club, double targetMetres)
    {
        Refresh();
        int key = (int)Math.Round(targetMetres * 2);
        return PartialFactors.GetOrAdd((club.Code, key), _ =>
        {
            double target = key / 2.0;
            double rollShare = Math.Clamp((ApproachMetres - target) / (ApproachMetres - ShortGameMetres), 0, 1);
            var terrain = new FlatTerrain(ActivePhysics.SurfaceFor(Lie.Green));
            var options = SimulationOptions.Default with { TrajectorySampleIntervalSeconds = 1, EnableRoll = rollShare > 0, EnableBounce = rollShare > 0 };
            double lo = 0.08, hi = target > Carry(club.Code) ? 3 : 1;
            for (int i = 0; i < 16; i++)
            {
                double mid = (lo + hi) / 2;
                var (mph, launch, spin) = PartialLaunch(club, mid);
                var r = BallFlightSimulator.Simulate(LaunchConditions.FromDisplayUnits(mph, launch, 0, spin, 0),
                    ActivePhysics.Ball, ActivePhysics.StillAir, terrain, options);
                double reach = r.CarryDistance + rollShare * (r.TotalDistance - r.CarryDistance);
                if (reach < target) lo = mid; else hi = mid;
            }
            return (lo + hi) / 2;
        });
    }
}
