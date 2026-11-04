using SquareGspro.Physics;

namespace SquareGspro.Core.Course;

/// <param name="Flight">The ball's path in hole coordinates (Y = height of the ball), cut off where it actually stopped.</param>
/// <param name="Rest">Where the ball finished (the pin if holed). After a penalty or cart-path relief the next shot is from <see cref="NextBall"/>.</param>
public sealed record ShotOutcome(
    FlightResult Flight, P2 Start, P2 Rest, Lie RestLie, P2 NextBall, bool Holed, int Penalty, int Strokes, string Message)
{
    /// <summary>How the lie and stance changed the measured launch (null for putts and unplayed shots).</summary>
    public LieAdjustment? LieEffect { get; init; }
    /// <summary>The hole ended without holing out: the strokes reached the house-rules limit (double par).</summary>
    public bool PickedUp { get; init; }
    /// <summary>The hole is finished, holed or picked up.</summary>
    public bool HoleOver => Holed || PickedUp;
    /// <summary>How the lie changed the shot, as text, for an outcome received from another PC (which has no <see cref="LieEffect"/>).</summary>
    public string? LieNote { get; init; }

    /// <summary>"From the rough: −5% speed, −30% spin" when the lie or stance changed the shot (empty otherwise).</summary>
    public string LieText => LieNote ?? (LieEffect is { Changed: true, Summary: { Length: > 0 } summary } e ? $"{LieWords(e.Condition)}: {summary}" : "");

    private static string LieWords(LieCondition c)
    {
        string lie = c.Kind switch
        {
            LieKind.Rough => "From the rough", LieKind.DeepRough => "From deep rough", LieKind.Sand => "From the sand",
            LieKind.Fringe => "From the fringe", _ => "",
        };
        // A slope too slight to count as "sloping" can still nudge the launch; with no lie to name, call it the slope.
        string stance = c.IsLevel && lie.Length > 0 ? "" : "sloping lie";
        return lie.Length == 0 ? char.ToUpper(stance[0]) + stance[1..] : stance.Length == 0 ? lie : $"{lie}, {stance}";
    }
}

/// <summary>
/// Plays a round over a list of holes: positions the ball, applies each shot along an aim heading, works out where it
/// finishes and keeps score. Penalties are stroke-and-distance (one stroke, replay from the previous spot).
/// <see cref="HouseRules"/> set the tap-in distance, mulligans and the double-par pick-up.
/// <para>
/// The ball's motion comes from the physics engine (<see cref="SquareGspro.Physics"/>): aerodynamic flight, bounces
/// and roll over the hole's ground (<see cref="CourseGround"/>: real heights with <see cref="Terrain"/>, flat
/// without; surface by lie). This class adds the rules the physics doesn't know: water and out of bounds stop the
/// ball, a ball that rolls into a bunker stays there, a ball stopping on a cart path gets free relief, and the cup can
/// catch it.
/// </para>
/// </summary>
public sealed class HoleRound
{
    /// <summary>
    /// The ball meets the cup if its centre passes within <see cref="CaptureRadius"/> of the pin (cup radius plus
    /// ball radius). Dead centre it drops at up to <see cref="CaptureSpeed"/> m/s; nearer the edge it has to be
    /// slower, otherwise it lips out (rides the rim and turns away) or, if it's fast and well centred, skips over.
    /// </summary>
    public const double CaptureRadius = 0.075, CaptureSpeed = 1.6;
    /// <summary>Regulation cup: 4.25 in across, at least 4 in deep.</summary>
    public const double CupRadius = 0.054, CupDepth = 0.10;

    /// <summary>Gravity along a slope for a rolling ball (solid sphere: 5/7 g), for the putt read's first guess.</summary>
    private const double SlopeGravity = 5.0 / 7.0 * PhysicsConstants.Gravity;
    private const double RecordEvery = 0.02;

    private static readonly GolfPhysicsEngine Engine = new();

    private readonly int?[] _scores;
    private (P2 Ball, double Heading, double Speed, int Physics)? _read;
    /// <summary>The round before the last counted shot on this hole, for a mulligan.</summary>
    private State? _beforeLast;

    public HoleRound(IReadOnlyList<Hole> holes, Terrain? terrain = null, HouseRules? rules = null)
    {
        Holes = holes;
        Terrain = terrain;
        Rules = rules ?? HouseRules.Strict;
        MulligansLeft = Rules.Mulligans;
        _scores = new int?[holes.Count];
        StartHole(0);
    }

    public IReadOnlyList<Hole> Holes { get; }
    /// <summary>Ground heights (null = flat).</summary>
    public Terrain? Terrain { get; }
    public HouseRules Rules { get; }
    public IReadOnlyList<int?> Scores => _scores;
    public int HoleIndex { get; private set; }
    public Hole Hole => Holes[HoleIndex];
    public P2 Ball { get; private set; }
    public Lie Lie { get; private set; }
    public int Strokes { get; private set; }
    /// <summary>The hole is finished (holed out, or picked up at the house-rules limit).</summary>
    public bool Holed { get; private set; }
    /// <summary>The hole was picked up at double par rather than holed.</summary>
    public bool PickedUp { get; private set; }
    public int MulligansLeft { get; private set; }
    /// <summary>A mulligan can take back the last shot: one is left, and the hole wasn't holed by it.</summary>
    public bool CanMulligan => MulligansLeft > 0 && _beforeLast != null && (!Holed || PickedUp);
    /// <summary>The first hole this player plays (0, unless they joined the round part-way through).</summary>
    public int FirstHole { get; private set; }
    public bool RoundComplete => _scores.Skip(FirstHole).All(s => s != null);
    public double DistanceToPin => Ball.DistanceTo(Hole.Pin);
    /// <summary>
    /// The wind on this hole for full shots (calm: the physics tuning's wind, if any). Putts roll in still air, as
    /// their read assumes.
    /// </summary>
    public CourseWind Wind { get; set; } = CourseWind.Calm;

    public void StartHole(int index)
    {
        HoleIndex = index;
        Ball = Hole.Tee;
        Lie = Lie.Tee;
        Strokes = 0;
        Holed = false;
        PickedUp = false;
        _beforeLast = null;
        _scores[index] = null;
    }

    /// <summary>Where the round stands (hole, ball, lie, strokes, that hole's score), to replay a shot from.</summary>
    public sealed record State(int HoleIndex, P2 Ball, Lie Lie, int Strokes, bool Holed, int? Score, bool PickedUp = false);

    public State Save() => new(HoleIndex, Ball, Lie, Strokes, Holed, _scores[HoleIndex], PickedUp);

    /// <summary>Puts the round back to <paramref name="s"/>, e.g. to replay the shot played from there.</summary>
    public void Restore(State s)
    {
        HoleIndex = s.HoleIndex;
        Ball = s.Ball;
        Lie = s.Lie;
        Strokes = s.Strokes;
        Holed = s.Holed;
        PickedUp = s.PickedUp;
        _beforeLast = null;
        _scores[s.HoleIndex] = s.Score;
    }

    /// <summary>
    /// Takes a saved round back up (after a restart): every hole's score, where the current hole stands and the
    /// mulligans left. Scores for holes outside this course are ignored.
    /// </summary>
    public void Resume(IReadOnlyList<int?> scores, State current, int mulligansLeft, int firstHole = 0)
    {
        for (int i = 0; i < _scores.Length; i++) _scores[i] = i < scores.Count ? scores[i] : null;
        Restore(current);
        MulligansLeft = Math.Clamp(mulligansLeft, 0, Rules.Mulligans);
        FirstHole = Math.Clamp(firstHole, 0, _scores.Length - 1);
    }

    /// <summary>The round before the last counted shot on this hole (what a mulligan goes back to), null if none.</summary>
    public State? BeforeLast => _beforeLast;

    /// <summary>
    /// Sets the round to exactly this, mulligan state included (unlike <see cref="Resume"/>, nothing is clamped or
    /// forgotten): for an online snapshot, so every PC's round is the host's.
    /// </summary>
    public void Load(IReadOnlyList<int?> scores, State current, int mulligansLeft, int firstHole, State? beforeLast)
    {
        for (int i = 0; i < _scores.Length; i++) _scores[i] = i < scores.Count ? scores[i] : null;
        Restore(current);
        MulligansLeft = Math.Max(0, mulligansLeft);
        FirstHole = Math.Clamp(firstHole, 0, _scores.Length - 1);
        _beforeLast = beforeLast;
    }

    /// <summary>
    /// Ends the hole without holing out (a player skipped online after dropping): it scores <paramref name="score"/>
    /// and can't be taken back. Nothing happens if the hole is already finished.
    /// </summary>
    public void PickUpHole(int score)
    {
        if (Holed) return;
        Strokes = Math.Max(Strokes, score);
        Holed = PickedUp = true;
        _scores[HoleIndex] = Strokes;
        _beforeLast = null;
    }

    /// <summary>What a skipped player scores on the hole: double par, or the strokes already taken if more.</summary>
    public int SkipScore => Math.Max(Strokes, 2 * Hole.Par);

    /// <summary>A player joining part-way through: they start on the tee of hole <paramref name="index"/>; the holes before don't count for them.</summary>
    public void JoinAt(int index)
    {
        FirstHole = index;
        StartHole(index);
    }

    /// <summary>Takes back the last shot (stroke, penalty and all) and uses up one mulligan. False if none can be taken.</summary>
    public bool Mulligan()
    {
        if (!CanMulligan) return false;
        var before = _beforeLast!;
        Restore(before);
        MulligansLeft--;
        return true;
    }

    public double GroundAt(P2 p) => Terrain?.HeightAt(p) ?? 0;
    private CourseGround Ground => new(Hole, Terrain);

    /// <summary>
    /// Heading (radians, 0 = +X, positive = right) for the next shot: at the tee aim point on the first stroke, at the
    /// pin after that, and on the green where the putt read says (allowing for break).
    /// </summary>
    public double DefaultAimHeading() => Lie == Lie.Green && !Holed ? ReadPutt().Heading : Heading(Ball, DefaultTarget);

    /// <summary>The default target: where the tee shot is aimed on the first stroke, the pin after that.</summary>
    public P2 DefaultTarget => Strokes == 0 ? Hole.TeeAim : Hole.Pin;

    /// <summary>
    /// Distance the shot to <paramref name="target"/> plays: its length plus the rise (uphill plays longer, downhill
    /// shorter; a metre of height is roughly a metre of carry), then longer into the wind and shorter with it.
    /// </summary>
    public double PlaysLike(P2 target)
    {
        double d = Ball.DistanceTo(target) + (GroundAt(target) - GroundAt(Ball));
        return Wind.IsCalm ? d : d * Wind.PlaysLikeFactor(Heading(Ball, target));
    }

    public static double Heading(P2 from, P2 to) => Math.Atan2(to.Z - from.Z, to.X - from.X);

    /// <summary>
    /// Approximate deceleration (m/s²) of a ball rolling on flat ground of this lie: the engine surface's rolling
    /// resistance (green ≈ stimp 10). Air drag adds a little on top. Used to pace putts.
    /// </summary>
    public static double RollDecel(Lie lie) => CourseGround.SurfaceFor(lie).RollingResistance * PhysicsConstants.Gravity;

    /// <summary>
    /// The ball's lie at <paramref name="at"/> for a shot along <paramref name="headingRad"/>: the surface, and the
    /// stance slope from the ground (uphill along the aim; ball above or below the feet, which depends on which side
    /// of the ball the golfer stands: a right-hander stands to its left). Tees are built level (and the ball is
    /// teed up), so tee shots are always level whatever the elevation data says.
    /// </summary>
    public LieCondition StanceAt(P2 at, double headingRad, bool leftHanded)
    {
        var kind = CourseGround.ToLieKind(Hole.LieAt(at));
        if (Terrain == null || kind == LieKind.Tee) return new LieCondition(kind, LeftHanded: leftHanded);
        var g = Terrain.Gradient(at);
        double c = Math.Cos(headingRad), s = Math.Sin(headingRad);
        double alongRise = g.X * c + g.Z * s, rightRise = -g.X * s + g.Z * c;
        const double toDeg = 180 / Math.PI;
        return new LieCondition(kind, Math.Atan(alongRise) * toDeg, Math.Atan(leftHanded ? -rightRise : rightRise) * toDeg, leftHanded);
    }

    /// <summary>Loft for the lie model: the club preset's, else a guess from the launch angle.</summary>
    private static double LoftFor(string? club, RangeShot shot) => ClubPresets.Find(club)?.LoftDeg ?? Math.Clamp(shot.LaunchDeg * 1.6, 8, 64);

    /// <summary>
    /// Plays one shot. <paramref name="putt"/> rolls the ball along the ground instead of flying it. Full shots are
    /// adjusted for the lie and stance (<see cref="LieModel"/>; <paramref name="club"/> gives the loft for sidehill
    /// lies). A shot the engine can't simulate (impossible launch numbers) isn't counted and leaves the ball where it was.
    /// </summary>
    public ShotOutcome Play(RangeShot shot, bool putt, double headingRad, string? club = null, bool leftHanded = false)
    {
        if (Holed) throw new InvalidOperationException("The hole is finished.");

        var start = Ball;
        var path = new List<FlightPoint>();
        int landing = 0;
        double carryYd = 0, apexYd = 0, hang = 0, descent = 0;
        (P2 Stop, Lie Lie, bool Holed) end;
        LieAdjustment? lieEffect = null;

        try
        {
            if (putt)
            {
                double dir = headingRad + shot.DirectionDeg * Math.PI / 180;
                end = RollFrom(path, start, new P2(Math.Cos(dir), Math.Sin(dir)) * (shot.BallMph / ShotMapper.MsToMph), 0, cup: true);
            }
            else
            {
                var measured = LaunchConditions.FromDisplayUnits(shot.BallMph, shot.LaunchDeg, shot.DirectionDeg, Math.Max(0, shot.SpinRpm), shot.SpinAxisDeg);
                lieEffect = ActivePhysics.LieModel.Apply(measured, StanceAt(start, headingRad, leftHanded), LoftFor(club, shot));
                var rules = new HoleRules(Hole, cup: true);
                var r = Engine.SimulateShot(new SimulationRequest
                {
                    Launch = lieEffect.Launch,
                    Terrain = Ground,
                    Ball = ActivePhysics.Ball,
                    Environment = Wind.IsCalm ? ActivePhysics.Environment : ActivePhysics.Environment with { Wind = Wind.Field() },
                    Options = SimulationOptions.Default with
                    {
                        StartX = start.Z, StartY = start.X, AimHeadingDegrees = headingRad * 180 / Math.PI,
                        TrajectorySampleIntervalSeconds = RecordEvery, Observer = rules,
                    },
                });
                path.AddRange(r.Trajectory.Select(p => CourseGround.ToFlightPoint(p)));
                landing = r.LandingIndex;
                carryYd = r.CarryYards;
                apexYd = r.ApexYards;
                hang = r.TimeOfFlight;
                descent = r.LandingAngleDegrees;
                end = Finish(path, rules.Event, CourseGround.ToCourse(r.FinalPosition), cup: true);
            }
        }
        catch (SimulationValidationException e)
        {
            var still = new FlightResult(new[] { OnGround(start, 0), OnGround(start, 0) }, 0, 0, 0, 0, 0, 0, 0);
            return new ShotOutcome(still, start, start, Lie, Ball, false, 0, Strokes, "Shot not played: " + string.Join(" ", e.Problems));
        }

        var (rest, restLie, holed) = end;
        _beforeLast = Save();
        int penalty = restLie is Lie.Water or Lie.OutOfBounds ? 1 : 0;
        Strokes += 1 + penalty;
        int? max = Rules.MaxScore(Hole.Par);
        bool pickedUp = false;
        (P2 At, Lie Lie)? relief = null;
        string message;
        if (holed)
        {
            message = Strokes == 1 ? "Hole in one!" : $"Holed! {ScoreName(Strokes, Hole.Par)}";
        }
        else if (penalty > 0)
        {
            message = restLie == Lie.Water ? "In the water. 1 stroke penalty, replay from the previous spot."
                                           : "Out of bounds. 1 stroke penalty, replay from the previous spot.";
        }
        else if (restLie == Lie.Green && rest.DistanceTo(Hole.Pin) <= Rules.GimmeMetres && Strokes + 1 <= (max ?? int.MaxValue))
        {
            Strokes++;
            holed = true;
            message = $"Tapped in. {ScoreName(Strokes, Hole.Par)}";
        }
        else if (restLie == Lie.CartPath && CartPathRelief(rest) is { } drop)
        {
            relief = drop;
            message = $"Cart path: free relief to the {LieName(drop.Lie).ToLowerInvariant()}, {drop.At.DistanceTo(Hole.Pin) * Units.MetresToYards:0} yd to the pin";
        }
        else message = $"{LieName(restLie)}, {rest.DistanceTo(Hole.Pin) * Units.MetresToYards:0} yd to the pin";

        if (!holed && Strokes >= max)
        {
            // House rules: that's the most a hole can cost. Pick up and score double par.
            pickedUp = true;
            Strokes = max.Value;
            message = (penalty > 0 ? message.Split('.')[0] + ". " : "") + $"Picked up: double par, {Strokes}.";
        }

        if (holed) { Holed = true; Ball = Hole.Pin; Lie = Lie.Green; _scores[HoleIndex] = Strokes; }
        else
        {
            if (penalty == 0) { Ball = relief?.At ?? rest; Lie = relief?.Lie ?? restLie; }
            if (pickedUp) { Holed = PickedUp = true; _scores[HoleIndex] = Strokes; }
        }

        var flight = new FlightResult(path, landing, carryYd, start.DistanceTo(rest) * Units.MetresToYards, apexYd,
            Offline(start, rest, headingRad) * Units.MetresToYards, hang, descent);
        return new ShotOutcome(flight, start, rest, restLie, Ball, holed, penalty, Strokes, message) { LieEffect = lieEffect, PickedUp = pickedUp };
    }

    private FlightPoint OnGround(P2 p, double t) => new(t, p.X, GroundAt(p), p.Z);

    /// <summary>How far clear of a cart path the relief point is (the ball and the golfer's stance), m.</summary>
    public const double ReliefClearance = 0.6;
    /// <summary>Furthest the search for relief looks (m); beyond it the ball is played from the path.</summary>
    private const double ReliefSearch = 20;

    /// <summary>
    /// Free relief from a cart path at <paramref name="at"/>: the nearest point at least <see cref="ReliefClearance"/>
    /// from every path, no nearer the hole, in bounds and on grass (not a bunker, green or water). Of equally near
    /// points, the one closest to the hole. Null if there's none within <see cref="ReliefSearch"/>.
    /// </summary>
    public (P2 At, Lie Lie)? CartPathRelief(P2 at)
    {
        var hole = Hole;
        double toPin = at.DistanceTo(hole.Pin);
        var paths = hole.CartPaths.Where(c => c.IsNear(at, ReliefSearch + ReliefClearance)).ToList();
        for (double r = 0.25; r <= ReliefSearch; r += 0.25)
        {
            (P2 At, Lie Lie)? best = null;
            double bestToPin = double.MaxValue;
            int steps = Math.Max(72, (int)Math.Ceiling(2 * Math.PI * r / 0.25));
            for (int k = 0; k < steps; k++)
            {
                double a = 2 * Math.PI * k / steps;
                var p = at + new P2(Math.Cos(a), Math.Sin(a)) * r;
                double d = p.DistanceTo(hole.Pin);
                if (d < toPin || d >= bestToPin || paths.Any(c => c.DistanceTo(p) < ReliefClearance)) continue;
                var lie = hole.LieAt(p);
                if (lie is Lie.Fairway or Lie.Rough or Lie.DeepRough or Lie.Fringe or Lie.Tee) { best = (p, lie); bestToPin = d; }
            }
            if (best != null) return best;
        }
        return null;
    }

    /// <summary>What stopped the ball (a rule, not the physics).</summary>
    private enum StopKind { Hazard, Bunker, LandedInCup, Cup }

    /// <summary>
    /// The hole's rules, applied at every ground impact and ground step of a simulation: water and out of bounds
    /// stop the ball; a ball that rolls into a bunker from outside stays there; a ball landing on the cup goes in; a
    /// rolling ball reaching the cup is handed to <see cref="AtTheCup"/>. One per simulation (it keeps state).
    /// </summary>
    private sealed class HoleRules : IGroundObserver
    {
        private readonly Hole _hole;
        private readonly bool _cup;
        private Lie? _rollLie;

        public HoleRules(Hole hole, bool cup) { _hole = hole; _cup = cup; }

        public (StopKind Kind, P2 At, Lie Lie, P2 Velocity)? Event { get; private set; }

        public bool ShouldStop(in BallState s, double time, GroundEventKind kind)
        {
            var p = CourseGround.ToCourse(s.Position);
            var lie = _hole.LieAt(p);
            var v = CourseGround.ToCourse(s.Velocity);
            if (lie is Lie.Water or Lie.OutOfBounds) return Stop(StopKind.Hazard, p, lie, v);
            if (kind == GroundEventKind.Impact)
            {
                _rollLie = null;   // a new roll starts after the last bounce
                return _cup && p.DistanceTo(_hole.Pin) <= CaptureRadius && Stop(StopKind.LandedInCup, p, Lie.Green, v);
            }
            _rollLie ??= lie;
            if (lie == Lie.Bunker && _rollLie != Lie.Bunker) return Stop(StopKind.Bunker, p, lie, v);
            return _cup && p.DistanceTo(_hole.Pin) <= CaptureRadius && Stop(StopKind.Cup, p, Lie.Green, v);
        }

        private bool Stop(StopKind kind, P2 at, Lie lie, P2 velocity)
        {
            Event = (kind, at, lie, velocity);
            return true;
        }
    }

    /// <summary>Where a simulated ball ends up once the rules are applied. <paramref name="rest"/> is where the physics left it.</summary>
    private (P2 Stop, Lie Lie, bool Holed) Finish(List<FlightPoint> path, (StopKind Kind, P2 At, Lie Lie, P2 Velocity)? ev, P2 rest, bool cup)
    {
        double t = path.Count > 0 ? path[^1].T : 0;
        if (ev is not { } e)
        {
            if (cup && rest.DistanceTo(Hole.Pin) <= CupRadius)
            {
                AddDrop(path, rest, t);   // stopped over the hole: it falls
                return (Hole.Pin, Lie.Green, true);
            }
            return (rest, Hole.LieAt(rest), false);
        }
        switch (e.Kind)
        {
            case StopKind.LandedInCup:
                AddDrop(path, e.At, t);   // straight in
                return (Hole.Pin, Lie.Green, true);
            case StopKind.Cup:
            {
                // Closest approach to the pin along the line it's rolling.
                double speed = e.Velocity.Length;
                var dir = speed > 1e-9 ? e.Velocity * (1 / speed) : new P2(1, 0);
                double ahead = Math.Max(0, (Hole.Pin.X - e.At.X) * dir.X + (Hole.Pin.Z - e.At.Z) * dir.Z);
                var near = e.At + dir * ahead;
                double tNear = t + (speed > 1e-9 ? ahead / speed : 0);
                path.Add(OnGround(near, tNear));
                return AtTheCup(path, near, dir, speed, tNear);
            }
            default:
                return (e.At, e.Lie, false);
        }
    }

    /// <summary>
    /// Rolls the ball from <paramref name="from"/> with horizontal velocity <paramref name="v"/> (m/s) through the
    /// engine's putting simulation, appending the path (times offset by <paramref name="t"/>) and applying the rules.
    /// </summary>
    private (P2 Stop, Lie Lie, bool Holed) RollFrom(List<FlightPoint> path, P2 from, P2 v, double t, bool cup)
    {
        double speed = v.Length;
        if (speed < 1e-3)
        {
            path.Add(OnGround(from, t));
            return Finish(path, null, from, cup);
        }
        var rules = new HoleRules(Hole, cup);
        var r = Engine.SimulatePutt(new PuttingRequest
        {
            Terrain = Ground, StartX = from.Z, StartY = from.X, BallSpeed = Math.Min(speed, MaxRollSpeed),
            DirectionDegrees = Math.Atan2(v.Z, v.X) * 180 / Math.PI, TrajectorySampleIntervalSeconds = RecordEvery, Observer = rules,
            Ball = ActivePhysics.Ball, Environment = ActivePhysics.Environment,
        });
        path.AddRange(r.Trajectory.Select(p => CourseGround.ToFlightPoint(p, t)));
        return Finish(path, rules.Event, CourseGround.ToCourse(r.FinalPosition), cup);
    }

    /// <summary>Fastest ball the putting simulation accepts (m/s); anything struck harder along the ground is capped.</summary>
    private const double MaxRollSpeed = 20;

    /// <summary>Fastest the ball can be going and still drop, for a given miss from the centre of the cup.</summary>
    public static double DropSpeed(double offset) =>
        offset >= CaptureRadius ? 0 : CaptureSpeed * Math.Sqrt(1 - (offset / CaptureRadius) * (offset / CaptureRadius));

    /// <summary>
    /// The ball reaches the cup at <paramref name="near"/> (its closest point to the pin) moving along
    /// <paramref name="dir"/>: it drops, skips over, or lips out and rolls on. Adds the rest of the path.
    /// </summary>
    private (P2 Stop, Lie Lie, bool Holed) AtTheCup(List<FlightPoint> path, P2 near, P2 dir, double speed, double t)
    {
        var pin = Hole.Pin;
        double offset = near.DistanceTo(pin);
        if (speed <= DropSpeed(offset))
        {
            AddDrop(path, near, t);
            return (pin, Lie.Green, true);
        }

        P2 from, outDir;
        double outSpeed;
        if (offset < CaptureRadius * 0.4)
        {
            // Too fast and dead straight: hops the hole and carries on, a little slower.
            from = near; outDir = dir; outSpeed = speed * 0.75;
        }
        else
        {
            // Catches the edge: rides the rim around the cup, turning toward the side it caught, and spins out.
            // Deeper and slower balls go further round.
            double depth = 1 - offset / CaptureRadius;
            double turn = Math.Clamp((0.35 + 2.6 * depth) * Math.Min(1, CaptureSpeed / speed), 0.3, 2.8);
            var radial = near - pin;
            double side = radial.X * dir.Z - radial.Z * dir.X >= 0 ? 1 : -1;   // keep going the way it was rolling
            double rim = Math.Max(offset, CupRadius + 0.005), a0 = Math.Atan2(radial.Z, radial.X);
            double rimSpeed = Math.Max(speed * 0.8, 0.3), top = GroundAt(pin);
            int steps = Math.Max(4, (int)(turn * 8));
            for (int k = 1; k <= steps; k++)
            {
                double f = (double)k / steps, ang = a0 + side * turn * f;
                path.Add(new FlightPoint(t + turn * rim * f / rimSpeed, pin.X + rim * Math.Cos(ang), top - 0.012 * Math.Sin(Math.PI * f), pin.Z + rim * Math.Sin(ang)));
            }
            t += turn * rim / rimSpeed;
            double c = Math.Cos(side * turn), s = Math.Sin(side * turn);
            from = new P2(path[^1].X, path[^1].Z);
            outDir = new P2(dir.X * c - dir.Z * s, dir.X * s + dir.Z * c);
            outSpeed = speed * (0.45 + 0.25 * (1 - depth));
        }
        return RollFrom(path, from, outDir * outSpeed, t, cup: false);
    }

    /// <summary>Falls into the cup from <paramref name="at"/>: slides to the middle while dropping to the bottom.</summary>
    private void AddDrop(List<FlightPoint> path, P2 at, double t)
    {
        const int steps = 8;
        const double fall = 0.16;   // seconds
        double top = GroundAt(Hole.Pin);
        for (int k = 1; k <= steps; k++)
        {
            double f = (double)k / steps, inward = 1 - (1 - f) * (1 - f);
            var p = at + (Hole.Pin - at) * inward;
            path.Add(new FlightPoint(t + fall * f, p.X, top - CupDepth * f * f, p.Z));
        }
    }

    /// <summary>
    /// Reads the putt from the ball: the aim (heading) and starting speed (m/s) that roll it into the cup at a
    /// comfortable pace (about 40 cm past if it missed), allowing for the slope, worked out with trial putts through
    /// the engine. On flat ground the aim is straight at the pin. The result is cached for the ball's position.
    /// </summary>
    public (double Heading, double Speed) ReadPutt()
    {
        if (_read is { } r && r.Ball == Ball && r.Physics == ActivePhysics.Version) return (r.Heading, r.Speed);
        var pin = Hole.Pin;
        double straight = Heading(Ball, pin), d = Ball.DistanceTo(pin);
        double friction = RollDecel(Lie);
        double rise = GroundAt(pin) - GroundAt(Ball);
        double speed = Math.Sqrt(Math.Max(0.05, 2 * friction * (d + 0.4) + 2 * SlopeGravity * rise));
        double heading = straight;

        if (d > 0.3)
        {
            var across = new P2(-Math.Sin(straight), Math.Cos(straight));   // right of the line
            var ground = new CourseGround(Hole, Terrain, CourseGround.SurfaceFor(Lie));
            // Where a putt passes the hole: sideways miss at its closest approach, the distance it rolls to get
            // there, and the total distance it rolls (engine putting simulation, no cup).
            (double Miss, double ToHole, double Total) Trial(double h, double v)
            {
                var trace = new PuttTrace(pin, across);
                Engine.SimulatePutt(new PuttingRequest
                {
                    Terrain = ground, StartX = Ball.Z, StartY = Ball.X, BallSpeed = Math.Min(v, MaxRollSpeed),
                    DirectionDegrees = h * 180 / Math.PI, Ground = ReadGroundOptions, TrajectorySampleIntervalSeconds = 1, Observer = trace,
                    Ball = ActivePhysics.Ball, Environment = ActivePhysics.Environment,
                });
                return (trace.Miss, trace.ToHole, trace.Rolled);
            }

            for (int round = 0; round < 4; round++)
            {
                if (Terrain != null)
                {
                    // Aim: bisect the sideways miss to zero (aiming further right moves the miss right).
                    double lo = straight - 0.6, hi = straight + 0.6;
                    double missLo = Trial(lo, speed).Miss, missHi = Trial(hi, speed).Miss;
                    if (Math.Sign(missLo) == Math.Sign(missHi)) break;   // can't get there (e.g. it rolls back): keep the last
                    for (int k = 0; k < 24; k++)
                    {
                        double mid = (lo + hi) / 2, m = Trial(mid, speed).Miss;
                        if (Math.Sign(m) == Math.Sign(missLo)) { lo = mid; missLo = m; } else hi = mid;
                    }
                    heading = (lo + hi) / 2;
                }
                // Pace: scale so it would roll ~40 cm past the hole.
                var (_, toHole, total) = Trial(heading, speed);
                if (total < 0.01) break;
                double want = toHole + 0.4;
                speed *= Math.Clamp(Math.Sqrt(want / total), 0.7, 1.4);
            }
        }
        _read = (Ball, heading, speed, ActivePhysics.Version);
        return (heading, speed);
    }

    /// <summary>Putt reads use a coarser ground step than play (a read rolls dozens of trial putts).</summary>
    private static readonly GroundOptions ReadGroundOptions = GroundOptions.Default with { TimeStepSeconds = 1.0 / 480 };

    /// <summary>Follows a trial putt: closest approach to the pin, sideways miss there, and distance rolled.</summary>
    private sealed class PuttTrace : IGroundObserver
    {
        private readonly P2 _pin, _across;
        private P2? _last;
        private double _best = double.MaxValue;

        public PuttTrace(P2 pin, P2 across) { _pin = pin; _across = across; }

        public double Miss { get; private set; }
        public double ToHole { get; private set; }
        public double Rolled { get; private set; }

        public bool ShouldStop(in BallState s, double time, GroundEventKind kind)
        {
            var p = CourseGround.ToCourse(s.Position);
            if (_last is { } q) Rolled += p.DistanceTo(q);
            _last = p;
            double dist = p.DistanceTo(_pin);
            if (dist < _best)
            {
                _best = dist;
                Miss = (p - _pin).X * _across.X + (p - _pin).Z * _across.Z;
                ToHole = Rolled;
            }
            return false;
        }
    }

    /// <summary>Rotates a flight from its own frame (X along the aim line) onto the hole, starting at <paramref name="origin"/>.</summary>
    public static List<FlightPoint> Place(IReadOnlyList<FlightPoint> pts, P2 origin, double headingRad)
    {
        double c = Math.Cos(headingRad), s = Math.Sin(headingRad);
        return pts.Select(p => p with { X = origin.X + p.X * c - p.Z * s, Z = origin.Z + p.X * s + p.Z * c }).ToList();
    }

    private static double Offline(P2 start, P2 rest, double heading)
    {
        var d = rest - start;
        return -Math.Sin(heading) * d.X + Math.Cos(heading) * d.Z;
    }

    public static string LieName(Lie lie) => lie switch
    {
        Lie.OutOfBounds => "Out of bounds", Lie.DeepRough => "Deep rough", Lie.CartPath => "Cart path", _ => lie.ToString(),
    };

    public static string ScoreName(int strokes, int par) => (strokes - par) switch
    {
        _ when strokes == 1 => "Hole in one",
        <= -3 => "Albatross", -2 => "Eagle", -1 => "Birdie", 0 => "Par", 1 => "Bogey", 2 => "Double bogey",
        var d => $"+{d}",
    };
}
