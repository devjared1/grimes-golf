namespace SquareGspro.Physics;

/// <summary>
/// Aerodynamic coefficients at one flow condition: drag C_D, lift C_L and the spin-decay moment coefficient C_M.
/// Forces are F = ½ρ C A v² and the spin-decay torque is τ = ½ρ C_M A v² r (A = πr², r = ball radius).
/// </summary>
public readonly record struct AeroCoefficients(double Drag, double Lift, double SpinMoment);

/// <summary>
/// Gives aerodynamic coefficients as a function of Reynolds number Re = ρ v d / μ and spin parameter S = r ω⊥ / v
/// (ω⊥ = spin perpendicular to the airflow). Implementations must be immutable (they are shared across threads).
/// </summary>
public interface IAerodynamicModel
{
    string Name { get; }
    AeroCoefficients Evaluate(double reynolds, double spinParameter);
}

/// <summary>
/// Coefficients from tables over Reynolds number × spin parameter, bilinearly interpolated and clamped at the table
/// edges (no extrapolation). Spin-decay moment: C_M = <see cref="SpinMomentPerSpinParameter"/> × S.
/// </summary>
public sealed class TabulatedAerodynamicModel : IAerodynamicModel
{
    private readonly double[] _re, _s;
    private readonly double[,] _cd, _cl;

    /// <param name="reynolds">Ascending Reynolds numbers (table rows).</param>
    /// <param name="spinParameters">Ascending spin parameters (table columns).</param>
    /// <param name="drag">C_D[row, column].</param>
    /// <param name="lift">C_L[row, column].</param>
    /// <param name="spinMomentPerSpinParameter">k in C_M = k·S.</param>
    public TabulatedAerodynamicModel(string name, double[] reynolds, double[] spinParameters, double[,] drag, double[,] lift,
                                     double spinMomentPerSpinParameter)
    {
        if (reynolds.Length < 2 || spinParameters.Length < 2) throw new ArgumentException("Tables need at least 2 rows and 2 columns.");
        if (!Ascending(reynolds) || !Ascending(spinParameters)) throw new ArgumentException("Table axes must be strictly ascending.");
        if (drag.GetLength(0) != reynolds.Length || drag.GetLength(1) != spinParameters.Length ||
            lift.GetLength(0) != reynolds.Length || lift.GetLength(1) != spinParameters.Length)
            throw new ArgumentException("Coefficient tables must be [reynolds, spinParameters] in size.");
        foreach (var v in drag) if (!(v > 0 && v < 2)) throw new ArgumentException($"Drag coefficient {v} is outside (0, 2).");
        foreach (var v in lift) if (!(v > -1 && v < 1)) throw new ArgumentException($"Lift coefficient {v} is outside (-1, 1).");
        if (spinMomentPerSpinParameter < 0) throw new ArgumentException("Spin moment coefficient cannot be negative.");

        Name = name;
        _re = (double[])reynolds.Clone();
        _s = (double[])spinParameters.Clone();
        _cd = (double[,])drag.Clone();
        _cl = (double[,])lift.Clone();
        SpinMomentPerSpinParameter = spinMomentPerSpinParameter;
    }

    public string Name { get; }
    public double SpinMomentPerSpinParameter { get; }
    public IReadOnlyList<double> ReynoldsAxis => _re;
    public IReadOnlyList<double> SpinParameterAxis => _s;

    public AeroCoefficients Evaluate(double reynolds, double spinParameter)
    {
        var (i, fi) = Locate(_re, reynolds);
        var (j, fj) = Locate(_s, spinParameter);
        return new AeroCoefficients(Bilinear(_cd, i, fi, j, fj), Bilinear(_cl, i, fi, j, fj),
                                    SpinMomentPerSpinParameter * Math.Max(0, spinParameter));
    }

    private static (int Index, double Fraction) Locate(double[] axis, double v)
    {
        if (!(v > axis[0])) return (0, 0);   // also catches NaN
        if (v >= axis[^1]) return (axis.Length - 2, 1);
        int i = Array.BinarySearch(axis, v);
        if (i < 0) i = ~i - 1;
        i = Math.Min(i, axis.Length - 2);
        return (i, (v - axis[i]) / (axis[i + 1] - axis[i]));
    }

    private static double Bilinear(double[,] t, int i, double fi, int j, double fj)
    {
        double a = t[i, j] + (t[i, j + 1] - t[i, j]) * fj;
        double b = t[i + 1, j] + (t[i + 1, j + 1] - t[i + 1, j]) * fj;
        return a + (b - a) * fi;
    }

    private static bool Ascending(double[] a)
    {
        for (int k = 1; k < a.Length; k++) if (!(a[k] > a[k - 1])) return false;
        return true;
    }
}

/// <summary>
/// Wraps a model and scales its coefficients; this is what a <see cref="CalibrationProfile"/> applies. The drag scale
/// may vary with Reynolds number: C_D × DragScale × (1 + DragReynoldsSlope · ln(Re / <see cref="ReferenceReynolds"/>)),
/// which lets calibration tilt the drag plateau without reshaping the table.
/// </summary>
public sealed class ScaledAerodynamicModel : IAerodynamicModel
{
    /// <summary>Reynolds number about which <see cref="DragReynoldsSlope"/> pivots (a mid-iron at full speed).</summary>
    public const double ReferenceReynolds = 1.5e5;

    public ScaledAerodynamicModel(IAerodynamicModel inner, double dragScale, double liftScale, double spinMomentScale, double dragReynoldsSlope = 0)
    {
        if (dragScale <= 0 || liftScale < 0 || spinMomentScale < 0) throw new ArgumentException("Coefficient scales must be positive.");
        if (Math.Abs(dragReynoldsSlope) > 1) throw new ArgumentException("Drag Reynolds slope must be within ±1.");
        Inner = inner;
        DragScale = dragScale;
        LiftScale = liftScale;
        SpinMomentScale = spinMomentScale;
        DragReynoldsSlope = dragReynoldsSlope;
    }

    public IAerodynamicModel Inner { get; }
    public double DragScale { get; }
    public double LiftScale { get; }
    public double SpinMomentScale { get; }
    public double DragReynoldsSlope { get; }
    public string Name => $"{Inner.Name} (×{DragScale:0.###} C_D, ×{LiftScale:0.###} C_L, ×{SpinMomentScale:0.###} C_M, C_D Re slope {DragReynoldsSlope:0.###})";

    public AeroCoefficients Evaluate(double reynolds, double spinParameter)
    {
        var c = Inner.Evaluate(reynolds, spinParameter);
        double drag = DragScale;
        if (DragReynoldsSlope != 0 && reynolds > 0) drag *= Math.Max(0.5, 1 + DragReynoldsSlope * Math.Log(reynolds / ReferenceReynolds));
        return new AeroCoefficients(c.Drag * drag, c.Lift * LiftScale, c.SpinMoment * SpinMomentScale);
    }
}

/// <summary>No air at all (vacuum). For tests and reference cases.</summary>
public sealed class VacuumAerodynamicModel : IAerodynamicModel
{
    public static readonly VacuumAerodynamicModel Instance = new();
    public string Name => "Vacuum";
    public AeroCoefficients Evaluate(double reynolds, double spinParameter) => default;
}

/// <summary>
/// The default coefficient tables.
/// <para>
/// PROVISIONAL, REQUIRES CALIBRATION. These are not measured data. They are smooth fits that follow the trends
/// reported for dimpled golf balls in published wind-tunnel work (Bearman &amp; Harvey 1976; Smits &amp; Smith 1994):
/// a drag crisis completed below Re ≈ 8×10⁴ with C_D ≈ 0.23-0.25 above it, drag rising with spin parameter, and lift
/// rising with spin parameter with a diminishing slope. Below the critical Reynolds number lift is reduced. Replace
/// these tables with measurements for a specific ball where available, or calibrate them with
/// <see cref="CalibrationService"/>.
/// </para>
/// </summary>
public static class DefaultAerodynamics
{
    /// <summary>Spin-decay moment slope: C_M = k·S. Chosen so a driver-type shot loses a few percent of its spin per second; provisional.</summary>
    public const double SpinMomentPerSpinParameter = 0.012;

    /// <summary>Drag rise per unit spin parameter (provisional fit).</summary>
    public const double DragPerSpinParameter = 0.18;

    /// <summary>
    /// Lift at high Reynolds number: C_L = a·S + b·S², held at its peak above S = −a/(2b) ≈ 0.31 (provisional fit;
    /// a quadratic of this form is commonly fitted to the Bearman &amp; Harvey lift data).
    /// </summary>
    public const double LiftLinear = 1.99, LiftQuadratic = -3.25;

    private static readonly double[] ReynoldsAxis = { 3e4, 5e4, 7e4, 9e4, 1.2e5, 1.6e5, 2.0e5, 2.6e5 };
    /// <summary>Zero-spin drag coefficient at each <see cref="ReynoldsAxis"/> entry (drag crisis, then a plateau).</summary>
    private static readonly double[] ZeroSpinDrag = { 0.48, 0.38, 0.29, 0.26, 0.245, 0.235, 0.235, 0.24 };
    /// <summary>Fraction of the high-Re lift kept at each Reynolds number (sub-critical flow lifts less).</summary>
    private static readonly double[] LiftReynoldsFactor = { 0.6, 0.8, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0 };
    private static readonly double[] SpinAxis = { 0, 0.025, 0.05, 0.075, 0.1, 0.15, 0.2, 0.25, 0.3, 0.4, 0.5 };

    public static TabulatedAerodynamicModel Provisional { get; } = Build();

    private static TabulatedAerodynamicModel Build()
    {
        var cd = new double[ReynoldsAxis.Length, SpinAxis.Length];
        var cl = new double[ReynoldsAxis.Length, SpinAxis.Length];
        for (int i = 0; i < ReynoldsAxis.Length; i++)
            for (int j = 0; j < SpinAxis.Length; j++)
            {
                double s = SpinAxis[j];
                cd[i, j] = ZeroSpinDrag[i] + DragPerSpinParameter * s;
                double sl = Math.Min(s, -LiftLinear / (2 * LiftQuadratic));
                cl[i, j] = LiftReynoldsFactor[i] * (LiftLinear * sl + LiftQuadratic * sl * sl);
            }
        return new TabulatedAerodynamicModel("Provisional dimpled ball (uncalibrated)", ReynoldsAxis, SpinAxis, cd, cl,
                                             SpinMomentPerSpinParameter);
    }
}
