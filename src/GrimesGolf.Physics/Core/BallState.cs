namespace GrimesGolf.Physics;

/// <summary>
/// The ball's rigid-body state: centre position (m), velocity (m/s) and angular velocity (rad/s, right-hand rule).
/// Also used for the time derivative of the state (velocity, acceleration, angular acceleration) by the integrator.
/// </summary>
public readonly record struct BallState(Vector3D Position, Vector3D Velocity, Vector3D AngularVelocity)
{
    public double Speed => Velocity.Length;
    public double SpinRate => AngularVelocity.Length;

    public static BallState operator +(in BallState a, in BallState b) =>
        new(a.Position + b.Position, a.Velocity + b.Velocity, a.AngularVelocity + b.AngularVelocity);

    public static BallState operator *(in BallState a, double k) =>
        new(a.Position * k, a.Velocity * k, a.AngularVelocity * k);
}

/// <summary>Time derivative of a <see cref="BallState"/> at a given state and time.</summary>
public interface IStateDerivative
{
    BallState Evaluate(in BallState state, double time);
}

/// <summary>Fixed-step integrators. Generic over a struct derivative so the hot loop has no allocations or boxing.</summary>
public static class NumericalIntegrator
{
    /// <summary>One classical fourth-order Runge-Kutta step of size <paramref name="dt"/>.</summary>
    public static BallState Rk4<T>(ref T f, in BallState s, double t, double dt) where T : struct, IStateDerivative
    {
        var k1 = f.Evaluate(s, t);
        var k2 = f.Evaluate(s + k1 * (dt / 2), t + dt / 2);
        var k3 = f.Evaluate(s + k2 * (dt / 2), t + dt / 2);
        var k4 = f.Evaluate(s + k3 * dt, t + dt);
        return s + (k1 + k2 * 2 + k3 * 2 + k4) * (dt / 6);
    }

    /// <summary>One explicit Euler step (reference only; used by tests to compare accuracy).</summary>
    public static BallState Euler<T>(ref T f, in BallState s, double t, double dt) where T : struct, IStateDerivative =>
        s + f.Evaluate(s, t) * dt;
}
