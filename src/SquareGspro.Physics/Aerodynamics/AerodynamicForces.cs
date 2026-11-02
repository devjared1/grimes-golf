namespace SquareGspro.Physics;

/// <summary>Aerodynamic state at one instant: flow numbers, coefficients and the resulting accelerations.</summary>
public readonly record struct AeroSample(
    double AirSpeed, double Reynolds, double SpinParameter, AeroCoefficients Coefficients,
    Vector3D DragAcceleration, Vector3D LiftAcceleration, Vector3D AngularAcceleration);

/// <summary>
/// Drag, Magnus lift and spin-decay torque on the ball.
/// <list type="bullet">
/// <item>v_rel = v_ball − v_wind; Re = ρ |v_rel| d / μ; S = r |ω⊥| / |v_rel|, ω⊥ = ω − (ω·v̂)v̂.</item>
/// <item>Drag: F_D = −½ρ C_D A |v_rel|² v̂ (opposes the relative airflow).</item>
/// <item>Lift: F_L = ½ρ C_L A |v_rel|² (ω⊥ × v̂)/|ω⊥ × v̂| (perpendicular to both airflow and spin axis).</item>
/// <item>Spin decay: τ = −½ρ C_M A |v_rel|² r ω̂, giving dω/dt = τ / I.</item>
/// </list>
/// </summary>
public static class AerodynamicForces
{
    public static AeroSample Evaluate(BallProperties ball, in AirProperties air, in Vector3D relativeVelocity, in Vector3D angularVelocity)
    {
        double v = relativeVelocity.Length;
        if (v < 1e-9) return default;

        var vHat = relativeVelocity / v;
        var spinPerp = angularVelocity - vHat * Vector3D.Dot(angularVelocity, vHat);
        double r = ball.RadiusMetres;
        double reynolds = air.Density * v * ball.DiameterMetres / air.DynamicViscosity;
        double s = r * spinPerp.Length / v;
        var c = ball.Aerodynamics.Evaluate(reynolds, s);

        double qa = 0.5 * air.Density * v * v * ball.CrossSectionAreaSquareMetres;   // dynamic pressure × area
        double invMass = 1 / ball.MassKg;
        var drag = vHat * (-qa * c.Drag * invMass);
        var liftDir = Vector3D.Cross(spinPerp, vHat).Normalized();
        var lift = liftDir * (qa * c.Lift * invMass);
        var spinDecay = angularVelocity.Normalized() * (-qa * c.SpinMoment * r / ball.MomentOfInertia);
        return new AeroSample(v, reynolds, s, c, drag, lift, spinDecay);
    }
}
