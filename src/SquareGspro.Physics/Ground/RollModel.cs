namespace SquareGspro.Physics;

/// <summary>
/// Settings for the ground phase (sliding and rolling).
/// </summary>
public sealed record GroundOptions
{
    /// <summary>Ground-phase time step (s). Friction is stiff, so this is finer than the flight step.</summary>
    public double TimeStepSeconds { get; init; } = 1.0 / 960;
    /// <summary>Below this speed (m/s) a ball the slope can't move is at rest.</summary>
    public double RestSpeed { get; init; } = 0.01;
    /// <summary>Contact-point slip speed (m/s) below which the ball counts as rolling.</summary>
    public double SlipTolerance { get; init; } = 0.02;
    /// <summary>Include air drag on the rolling ball (with wind). Matters for fast run-outs.</summary>
    public bool IncludeAirDrag { get; init; } = true;

    public static GroundOptions Default { get; } = new();

    public IEnumerable<string> Validate()
    {
        if (!(TimeStepSeconds is > 0 and <= 0.01)) yield return $"Ground time step {TimeStepSeconds} s is outside (0, 0.01].";
        if (!(RestSpeed is > 0 and < 0.5)) yield return $"Rest speed {RestSpeed} m/s is outside (0, 0.5).";
        if (!(SlipTolerance is > 0 and < 0.5)) yield return $"Slip tolerance {SlipTolerance} m/s is outside (0, 0.5).";
    }
}

/// <summary>Everything a roll step needs. Mutable only for its surface cache; one per simulation.</summary>
internal struct GroundContext
{
    public BallProperties Ball;
    public AirProperties Air;
    public IWindField Wind;
    public ITerrainProvider Terrain;
    public GroundOptions Options;
    public double ReferenceZ;
    private SurfaceProperties? _raw, _effective;

    /// <summary>The effective surface at a point, cached while the terrain keeps returning the same instance.</summary>
    public SurfaceProperties SurfaceAt(double x, double y)
    {
        var raw = Terrain.GetSurfaceProperties(x, y);
        if (!ReferenceEquals(raw, _raw)) { _raw = raw; _effective = raw.Effective(); }
        return _effective!;
    }

    /// <summary>Ball centre position resting on the ground at (x, y): one radius out along the normal.</summary>
    public Vector3D OnSurface(double x, double y)
    {
        var n = Terrain.GetSurfaceNormal(x, y);
        return new Vector3D(x, y, Terrain.GetElevation(x, y) + Ball.RadiusMetres / n.Z);
    }
}

public enum RollStepOutcome { Sliding, Rolling, AtRest }

/// <summary>
/// The ball in contact with the ground, moving along it.
/// <list type="bullet">
/// <item>Normal force per unit mass N = g cos θ (n·Z g); curvature of the ground is not modelled, so the ball stays in
///   contact (it doesn't leave the ground over crests).</item>
/// <item>Sliding (contact point slipping at u = v + ω × (−r n)): Coulomb friction a_f = −μ_d N û on the centre and
///   torque (−r n) × m a_f, which drives the ball toward rolling.</item>
/// <item>Rolling: a = (g_t + a_air)/(1 + k) − μ_r(v) N v̂ (+ grain), ω = n × v / r. For μ_r = 0 and no air this reduces
///   to the ideal rolling sphere, a = 5/7 g sin θ.</item>
/// <item>At rest when slower than the rest speed and the slope pull (g_t/(1 + k)) can't overcome rolling resistance.</item>
/// </list>
/// Semi-implicit Euler at <see cref="GroundOptions.TimeStepSeconds"/>; spin about the surface normal is discarded once
/// rolling (it doesn't affect the path in this model).
/// </summary>
internal static class RollModel
{
    public static RollStepOutcome Step(ref BallState s, ref GroundContext c, double time, double dt)
    {
        var p = s.Position;
        var n = c.Terrain.GetSurfaceNormal(p.X, p.Y);
        var surf = c.SurfaceAt(p.X, p.Y);
        var ball = c.Ball;
        double r = ball.RadiusMetres, k = ball.InertiaFactor, g = PhysicsConstants.Gravity;
        double normalAccel = g * n.Z;
        var gt = new Vector3D(0, 0, -g).ProjectOnPlane(n);

        var v = s.Velocity;
        double speed = v.Length;
        v = v.ProjectOnPlane(n);
        if (v.Length > 1e-12) v = v.Normalized() * speed;   // follow the ground without losing speed to the projection
        var w = s.AngularVelocity;

        var aAir = Vector3D.Zero;
        if (c.Options.IncludeAirDrag && speed > 0.05)
        {
            var wind = c.Wind.VelocityAt(p, p.Z - c.ReferenceZ, time);
            aAir = AerodynamicForces.Evaluate(ball, c.Air, v - wind, w).DragAcceleration.ProjectOnPlane(n);
        }

        var rc = n * -r;
        var slip = (v + Vector3D.Cross(w, rc)).ProjectOnPlane(n);
        Vector3D vNew, wNew;
        RollStepOutcome outcome;
        if (slip.Length > c.Options.SlipTolerance)
        {
            outcome = RollStepOutcome.Sliding;
            var af = slip.Normalized() * (-surf.DynamicFriction * normalAccel);
            vNew = v + (gt + af + aAir) * dt;
            wNew = w + Vector3D.Cross(rc, af) * (dt / (k * r * r));
            var slipNew = (vNew + Vector3D.Cross(wNew, rc)).ProjectOnPlane(n);
            if (Vector3D.Dot(slipNew, slip) <= 0) wNew = Vector3D.Cross(n, vNew) / r;   // reached rolling within the step
        }
        else
        {
            outcome = RollStepOutcome.Rolling;
            var drive = (gt + aAir) / (1 + k);
            double mu = surf.RollingResistance + surf.RollingResistancePerSpeed * speed;
            var grainPush = Vector3D.Zero;
            if (speed >= c.Options.RestSpeed)
            {
                var vHat = v / speed;
                if (surf.GrainStrength > 0 && surf.GrainDirection != Vector3D.Zero)
                {
                    var gd = surf.GrainDirection.ProjectOnPlane(n).Normalized();
                    double along = Vector3D.Dot(vHat, gd);
                    grainPush = (gd - vHat * along) * (surf.GrainStrength * surf.RollingResistance * normalAccel);
                    mu *= 1 - surf.GrainStrength * along;
                }
                double resist = mu * normalAccel;
                vNew = v + (drive - vHat * resist + grainPush) * dt;
                if (Vector3D.Dot(vNew, v) <= 0 && drive.Length <= resist) vNew = Vector3D.Zero;   // resistance stopped it
            }
            else
            {
                double resist = mu * normalAccel, pull = drive.Length;
                if (pull <= resist)
                {
                    s = new BallState(c.OnSurface(p.X, p.Y), Vector3D.Zero, Vector3D.Zero);
                    return RollStepOutcome.AtRest;
                }
                vNew = v + drive * ((pull - resist) / pull * dt);
            }
            wNew = Vector3D.Cross(n, vNew) / r;
        }

        var q = p + vNew * dt;
        s = new BallState(c.OnSurface(q.X, q.Y), vNew, wNew);
        return outcome;
    }
}
