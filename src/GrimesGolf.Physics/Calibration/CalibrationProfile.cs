using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GrimesGolf.Physics;

/// <summary>
/// Calibration adjustments: scale factors on the ball's aerodynamic coefficients and on surface bounce and roll.
/// Scales are applied to whatever models are in use, so a profile records how far calibration had to move them.
/// Immutable; save and load with <see cref="ToJson"/> / <see cref="FromJson"/>.
/// </summary>
public sealed record CalibrationProfile
{
    public string Name { get; init; } = "Uncalibrated";
    public double DragScale { get; init; } = 1;
    public double LiftScale { get; init; } = 1;
    public double SpinMomentScale { get; init; } = 1;
    /// <summary>Tilt of the drag scale with Reynolds number (see <see cref="ScaledAerodynamicModel"/>).</summary>
    public double DragReynoldsSlope { get; init; }
    public double RestitutionScale { get; init; } = 1;
    public double RollingResistanceScale { get; init; } = 1;
    public string? Notes { get; init; }
    /// <summary>Error metrics on the shots used to fit and to check the profile, if it came from <see cref="CalibrationService"/>.</summary>
    public CalibrationErrors? TrainingErrors { get; init; }
    public CalibrationErrors? ValidationErrors { get; init; }

    public static CalibrationProfile None { get; } = new();

    /// <summary>Bounds that keep calibration physically reasonable (also enforced by <see cref="Validate"/>).</summary>
    public const double MinimumAeroScale = 0.6, MaximumAeroScale = 1.4, MinimumSpinMomentScale = 0.25, MaximumSpinMomentScale = 4,
                        MaximumDragReynoldsSlope = 0.5;

    public BallProperties Apply(BallProperties ball) =>
        DragScale == 1 && LiftScale == 1 && SpinMomentScale == 1 && DragReynoldsSlope == 0
            ? ball
            : ball with { Aerodynamics = new ScaledAerodynamicModel(ball.Aerodynamics, DragScale, LiftScale, SpinMomentScale, DragReynoldsSlope) };

    public ITerrainProvider Apply(ITerrainProvider terrain) =>
        RestitutionScale == 1 && RollingResistanceScale == 1 ? terrain : new CalibratedTerrain(terrain, this);

    public IEnumerable<string> Validate()
    {
        if (!(DragScale is >= MinimumAeroScale and <= MaximumAeroScale)) yield return $"Drag scale {DragScale} is outside {MinimumAeroScale}-{MaximumAeroScale}.";
        if (!(LiftScale is >= MinimumAeroScale and <= MaximumAeroScale)) yield return $"Lift scale {LiftScale} is outside {MinimumAeroScale}-{MaximumAeroScale}.";
        if (!(SpinMomentScale is >= MinimumSpinMomentScale and <= MaximumSpinMomentScale)) yield return $"Spin moment scale {SpinMomentScale} is outside {MinimumSpinMomentScale}-{MaximumSpinMomentScale}.";
        if (!(Math.Abs(DragReynoldsSlope) <= MaximumDragReynoldsSlope)) yield return $"Drag Reynolds slope {DragReynoldsSlope} is outside ±{MaximumDragReynoldsSlope}.";
        if (!(RestitutionScale is >= 0.25 and <= 4)) yield return $"Restitution scale {RestitutionScale} is outside 0.25-4.";
        if (!(RollingResistanceScale is >= 0.25 and <= 4)) yield return $"Rolling resistance scale {RollingResistanceScale} is outside 0.25-4.";
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static CalibrationProfile FromJson(string json) => JsonSerializer.Deserialize<CalibrationProfile>(json, Json)
        ?? throw new InvalidDataException("Not a calibration profile.");
}

/// <summary>Applies a profile's surface scales to every surface of a terrain.</summary>
internal sealed class CalibratedTerrain : ITerrainProvider
{
    private readonly ITerrainProvider _inner;
    private readonly CalibrationProfile _profile;
    private readonly ConcurrentDictionary<SurfaceProperties, SurfaceProperties> _scaled = new(ReferenceEqualityComparer.Instance);

    public CalibratedTerrain(ITerrainProvider inner, CalibrationProfile profile) { _inner = inner; _profile = profile; }

    public double GetElevation(double x, double y) => _inner.GetElevation(x, y);
    public Vector3D GetSurfaceNormal(double x, double y) => _inner.GetSurfaceNormal(x, y);
    public SurfaceProperties GetSurfaceProperties(double x, double y) =>
        _scaled.GetOrAdd(_inner.GetSurfaceProperties(x, y), s => s with
        {
            RestitutionScale = s.RestitutionScale * _profile.RestitutionScale,
            RollingResistance = s.RollingResistance * _profile.RollingResistanceScale,
            RollingResistancePerSpeed = s.RollingResistancePerSpeed * _profile.RollingResistanceScale,
        });
}
