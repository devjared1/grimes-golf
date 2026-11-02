namespace SquareGspro.Physics;

/// <summary>Outcome of one ball-ground impact.</summary>
/// <param name="Outgoing">Ball state just after the impact (position unchanged).</param>
/// <param name="NormalSpeedIn">Speed into the ground along the true surface normal (m/s, ≥ 0).</param>
/// <param name="NormalSpeedOut">Speed away from the ground along the true surface normal (m/s; can be ≤ 0 when the crater holds the ball).</param>
/// <param name="Restitution">Normal coefficient of restitution used.</param>
/// <param name="CraterAngle">Tilt of the effective normal (radians).</param>
/// <param name="Slid">True if the contact slid for the whole impact (friction limited), false if it reached rolling contact.</param>
public readonly record struct BounceResult(BallState Outgoing, double NormalSpeedIn, double NormalSpeedOut, double Restitution,
                                          double CraterAngle, bool Slid);

/// <summary>
/// Impulse-based ball-ground impact (rigid sphere on a compliant surface, after Penner 2002).
/// <list type="number">
/// <item>The effective contact normal n_e is the surface normal n tilted back toward the incoming ball by the crater angle θ_c.</item>
/// <item>Normal: v_n' = −e(v_n) v_n along n_e; normal impulse J_n = m (1 + e) |v_n|.</item>
/// <item>Tangential: the contact point slips at u = v_t + ω × (−r n_e). The gripping impulse, which leaves it slipping
///   at −e_t u (e_t = the surface's tangential restitution; 0 = rolling contact), is J_stick = −m (1 + e_t) u k/(1 + k)
///   (I = k m r²). If |J_stick| ≤ μ_s J_n it applies; otherwise the contact slides and J_t = −μ_d J_n û. Both
///   frictions are scaled by 1 / (1 + |v_t| / v_skid), v_t the ball's speed along the (tilted) surface
///   (<see cref="SurfaceProperties.ImpactSkidSpeed"/>): a fast landing skids, keeping its backspin for the next touch.</item>
/// <item>v' = v + (J_n n_e + J_t)/m, ω' = ω + (−r n_e) × J_t / I.</item>
/// <item>Turf drag (<see cref="SurfaceProperties.ImpactDrag"/>): v' loses that share of its speed along the true
///   ground (full share when |v_n| ≥ <see cref="SurfaceProperties.HardLandingSpeed"/>), through the centre, so ω' is unchanged.</item>
/// </list>
/// </summary>
public static class BounceModel
{
    public static BounceResult Resolve(in BallState incoming, in Vector3D surfaceNormal, SurfaceProperties effectiveSurface, BallProperties ball)
    {
        var n = surfaceNormal;
        var v = incoming.Velocity;
        double vnTrue = Vector3D.Dot(v, n);
        if (vnTrue >= 0) return new BounceResult(incoming, 0, vnTrue, 0, 0, false);   // not moving into the ground

        // Crater: tilt the normal back toward where the ball came from.
        var vt = v - n * vnTrue;
        double speed = v.Length, vtLen = vt.Length;
        double impactAngle = Math.Atan2(-vnTrue, vtLen);
        double crater = vtLen > 1e-9 ? effectiveSurface.CraterAngle(speed, impactAngle) : 0;
        var tHat = vtLen > 1e-9 ? vt / vtLen : Vector3D.Zero;
        var ne = n * Math.Cos(crater) - tHat * Math.Sin(crater);

        double vn = Vector3D.Dot(v, ne);
        if (vn >= 0) { ne = n; vn = vnTrue; crater = 0; }   // crater can't turn the impact around
        double e = effectiveSurface.RestitutionAt(-vn);
        double m = ball.MassKg, r = ball.RadiusMetres, k = ball.InertiaFactor, inertia = ball.MomentOfInertia;
        double jn = m * (1 + e) * -vn;

        var rc = ne * -r;   // contact point relative to the centre
        var u = v + Vector3D.Cross(incoming.AngularVelocity, rc);
        u -= ne * Vector3D.Dot(u, ne);
        // Gripping impulse: leaves the contact slipping at −e_t times its incoming slip (e_t = 0: rolling contact).
        var jStick = u * (-m * (1 + effectiveSurface.TangentialRestitution) * k / (1 + k));
        // Friction fades with the ball's speed across the turf (fast landings skid; see SurfaceProperties.ImpactSkidSpeed).
        // Its spin doesn't count: more backspin mustn't make the ball skid further.
        double skid = effectiveSurface.ImpactSkidSpeed;
        double grip = skid > 0 ? 1 / (1 + (v - ne * vn).Length / skid) : 1;
        bool slid = jStick.Length > effectiveSurface.StaticFriction * grip * jn;
        var jt = slid ? u.Normalized() * (-effectiveSurface.DynamicFriction * grip * jn) : jStick;

        var vOut = v + (ne * jn + jt) / m;
        var wOut = incoming.AngularVelocity + Vector3D.Cross(rc, jt) / inertia;
        // Turf drag through the centre: takes a share of the speed along the ground (spin untouched), the full share on a
        // hard landing, less on gentle bounces.
        if (effectiveSurface.ImpactDrag > 0)
        {
            double share = Math.Clamp(effectiveSurface.ImpactDrag, 0, 1) * Math.Min(1, -vnTrue / SurfaceProperties.HardLandingSpeed);
            vOut -= (vOut - n * Vector3D.Dot(vOut, n)) * share;
        }
        return new BounceResult(incoming with { Velocity = vOut, AngularVelocity = wOut }, -vnTrue, Vector3D.Dot(vOut, n), e, crater, slid);
    }
}
