namespace GrimesGolf.Physics;

/// <summary>Simulated against measured for one reference shot (yards, degrees, seconds). Errors are simulated − measured.</summary>
public sealed record SimulationComparison(
    ReferenceShot Shot, FlightResults Simulated,
    double? CarryError, double? TotalError, double? ApexError, double? FlightTimeError, double? LandingAngleError);

/// <summary>Root-mean-square errors over a set of shots (null where no shot had that measurement).</summary>
public sealed record CalibrationErrors(int Shots, double? CarryRmsYards, double? TotalRmsYards, double? ApexRmsYards,
                                       double? FlightTimeRmsSeconds, double? LandingAngleRmsDegrees)
{
    public override string ToString() =>
        $"{Shots} shots: carry {Fmt(CarryRmsYards)} yd, total {Fmt(TotalRmsYards)} yd, apex {Fmt(ApexRmsYards)} yd, " +
        $"flight time {Fmt(FlightTimeRmsSeconds)} s, landing {Fmt(LandingAngleRmsDegrees)}° RMS";

    private static string Fmt(double? v) => v is { } x ? x.ToString("0.0#") : "-";
}

/// <summary>
/// Compares the simulation with reference shots and fits a <see cref="CalibrationProfile"/>.
/// <para>
/// Fit: bounded Nelder-Mead over (drag scale, lift scale, spin-moment scale, drag Reynolds slope) minimising
/// Σ ((sim − measured)/σ)² over carry (σ = 3 yd), total (5 yd), apex (2 yd), flight time (0.25 s) and landing angle
/// (2°), plus a prior ((deviation from no adjustment / 0.25)²) that resists moving coefficients further than the data supports. Scales are
/// held within <see cref="CalibrationProfile"/>'s bounds, and the fitted model must still give coefficients in the
/// published range for golf balls (see <see cref="CheckPhysicalRange"/>), otherwise calibration fails rather than
/// returning an unphysical profile. Deterministic.
/// </para>
/// </summary>
public sealed class CalibrationService
{
    private const double CarrySigma = 3, TotalSigma = 5, ApexSigma = 2, FlightTimeSigma = 0.25, LandingSigma = 2, PriorSigma = 0.25;

    public CalibrationService(BallProperties? ball = null, ITerrainProvider? terrain = null, SimulationOptions? options = null)
    {
        Ball = ball ?? BallProperties.Standard;
        Terrain = terrain ?? new FlatTerrain(Surfaces.Fairway);
        Options = options ?? SimulationOptions.Default with { TrajectorySampleIntervalSeconds = 0.1 };
    }

    public BallProperties Ball { get; }
    public ITerrainProvider Terrain { get; }
    public SimulationOptions Options { get; }

    public SimulationComparison Compare(ReferenceShot shot, CalibrationProfile profile)
    {
        // Totals need the run-out; otherwise flight only is enough.
        var options = shot.TotalYards is null ? Options with { EnableBounce = false } : Options;
        var r = BallFlightSimulator.Simulate(shot.Launch, profile.Apply(Ball), shot.Environment, profile.Apply(Terrain), options);
        return new SimulationComparison(shot, r,
            r.CarryYards - shot.CarryYards, r.TotalYards - shot.TotalYards, r.ApexYards - shot.ApexYards,
            r.TimeOfFlight - shot.FlightTimeSeconds, r.LandingAngleDegrees - shot.LandingAngleDegrees);
    }

    public IReadOnlyList<SimulationComparison> Compare(IEnumerable<ReferenceShot> shots, CalibrationProfile profile) =>
        shots.Select(s => Compare(s, profile)).ToArray();

    public static CalibrationErrors Errors(IReadOnlyList<SimulationComparison> c)
    {
        static double? Rms(IEnumerable<double?> e)
        {
            var v = e.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
            return v.Length == 0 ? null : Math.Sqrt(v.Average(x => x * x));
        }
        return new CalibrationErrors(c.Count, Rms(c.Select(x => x.CarryError)), Rms(c.Select(x => x.TotalError)),
            Rms(c.Select(x => x.ApexError)), Rms(c.Select(x => x.FlightTimeError)), Rms(c.Select(x => x.LandingAngleError)));
    }

    /// <summary>Fits aerodynamic scales to <paramref name="training"/> and reports errors on both sets.</summary>
    public CalibrationProfile Calibrate(IReadOnlyList<ReferenceShot> training, IReadOnlyList<ReferenceShot> validation, string name,
                                        CalibrationProfile? start = null)
    {
        if (training.Count == 0) throw new ArgumentException("Calibration needs at least one training shot.");
        if (training.Intersect(validation).Any()) throw new ArgumentException("Training and validation shots must be separate.");
        start ??= CalibrationProfile.None;

        double[] lo = { CalibrationProfile.MinimumAeroScale, CalibrationProfile.MinimumAeroScale, CalibrationProfile.MinimumSpinMomentScale, -CalibrationProfile.MaximumDragReynoldsSlope };
        double[] hi = { CalibrationProfile.MaximumAeroScale, CalibrationProfile.MaximumAeroScale, CalibrationProfile.MaximumSpinMomentScale, CalibrationProfile.MaximumDragReynoldsSlope };
        double[] prior = { 1, 1, 1, 0 };
        CalibrationProfile With(double[] x) => start with { DragScale = x[0], LiftScale = x[1], SpinMomentScale = x[2], DragReynoldsSlope = x[3] };
        double Objective(double[] x)
        {
            double sum = 0;
            foreach (var c in Compare(training, With(x)))
                sum += Sq(c.CarryError / CarrySigma) + Sq(c.TotalError / TotalSigma) + Sq(c.ApexError / ApexSigma)
                     + Sq(c.FlightTimeError / FlightTimeSigma) + Sq(c.LandingAngleError / LandingSigma);
            for (int i = 0; i < x.Length; i++) sum += Sq((x[i] - prior[i]) / PriorSigma);
            return sum;
        }

        var best = NelderMead(Objective, new[] { start.DragScale, start.LiftScale, start.SpinMomentScale, start.DragReynoldsSlope }, lo, hi, iterations: 300);
        var profile = With(best);
        var range = CheckPhysicalRange(profile.Apply(Ball).Aerodynamics).ToArray();
        if (range.Length > 0)
            throw new InvalidOperationException("Calibration would need unphysical coefficients: " + string.Join(" ", range));

        return profile with
        {
            Name = name,
            TrainingErrors = Errors(Compare(training, profile)),
            ValidationErrors = validation.Count > 0 ? Errors(Compare(validation, profile)) : null,
            Notes = $"Fitted to {training.Count} shots ({string.Join(", ", training.Select(s => s.Name))}); validated on {validation.Count}.",
        };
    }

    /// <summary>
    /// Checks a model against coefficient ranges reported for golf balls at full-shot conditions (Re ≈ 1-2×10⁵):
    /// C_D 0.18-0.35 and C_L 0.08-0.30 at S = 0.1; C_L rising with S. Returns the problems found.
    /// </summary>
    public static IEnumerable<string> CheckPhysicalRange(IAerodynamicModel model)
    {
        foreach (double re in new[] { 1.2e5, 2.0e5 })
        {
            var c = model.Evaluate(re, 0.1);
            if (!(c.Drag is >= 0.18 and <= 0.35)) yield return $"C_D = {c.Drag:0.###} at Re {re:0.#e0}, S 0.1 is outside 0.18-0.35.";
            if (!(c.Lift is >= 0.08 and <= 0.30)) yield return $"C_L = {c.Lift:0.###} at Re {re:0.#e0}, S 0.1 is outside 0.08-0.30.";
            if (!(model.Evaluate(re, 0.3).Lift > c.Lift)) yield return $"C_L does not rise with spin at Re {re:0.#e0}.";
        }
    }

    private static double Sq(double? v) => v is { } x ? x * x : 0;

    /// <summary>
    /// Nelder-Mead simplex minimisation within [lo, hi]. The bounds are handled by searching over z with
    /// x = lo + (hi − lo)(1 + sin z)/2, so every trial point is inside the bounds and the simplex can't collapse onto
    /// a bound (as it does when points are simply clamped). Deterministic.
    /// </summary>
    internal static double[] NelderMead(Func<double[], double> f, double[] x0, double[] lo, double[] hi, int iterations, double step = 0.3)
    {
        int n = x0.Length;
        double[] ToX(double[] z) { var x = new double[n]; for (int i = 0; i < n; i++) x[i] = lo[i] + (hi[i] - lo[i]) * (1 + Math.Sin(z[i])) / 2; return x; }
        double F(double[] z) => f(ToX(z));
        var z0 = new double[n];
        for (int i = 0; i < n; i++) z0[i] = Math.Asin(Math.Clamp(2 * (x0[i] - lo[i]) / (hi[i] - lo[i]) - 1, -1, 1));

        var pts = new double[n + 1][];
        var val = new double[n + 1];
        pts[0] = z0;
        for (int i = 0; i < n; i++)
        {
            var p = (double[])z0.Clone();
            p[i] += z0[i] + step < Math.PI / 2 ? step : -step;
            pts[i + 1] = p;
        }
        for (int i = 0; i <= n; i++) val[i] = F(pts[i]);

        double[] Mix(double[] a, double[] b, double t) { var y = new double[n]; for (int i = 0; i < n; i++) y[i] = a[i] + (b[i] - a[i]) * t; return y; }
        for (int it = 0; it < iterations; it++)
        {
            var order = Enumerable.Range(0, n + 1).OrderBy(i => val[i]).ToArray();
            pts = order.Select(i => pts[i]).ToArray();
            val = order.Select(i => val[i]).ToArray();
            if (Math.Abs(val[n] - val[0]) < 1e-9 * (1 + Math.Abs(val[0]))) break;

            var centroid = new double[n];
            for (int i = 0; i < n; i++) for (int d = 0; d < n; d++) centroid[d] += pts[i][d] / n;
            var refl = Mix(centroid, pts[n], -1);
            double fr = F(refl);
            if (fr < val[0])
            {
                var exp = Mix(centroid, pts[n], -2);
                double fe = F(exp);
                if (fe < fr) { pts[n] = exp; val[n] = fe; } else { pts[n] = refl; val[n] = fr; }
            }
            else if (fr < val[n - 1]) { pts[n] = refl; val[n] = fr; }
            else
            {
                var con = Mix(centroid, pts[n], 0.5);
                double fc = F(con);
                if (fc < val[n]) { pts[n] = con; val[n] = fc; }
                else
                    for (int i = 1; i <= n; i++) { pts[i] = Mix(pts[0], pts[i], 0.5); val[i] = F(pts[i]); }
            }
        }
        return ToX(pts[Enumerable.Range(0, n + 1).OrderBy(i => val[i]).First()]);
    }
}
