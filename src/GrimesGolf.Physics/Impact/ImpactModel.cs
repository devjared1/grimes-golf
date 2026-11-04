namespace GrimesGolf.Physics;

/// <summary>Result of the club impact model. Launch values are marked <see cref="ValueSource.Calculated"/>.</summary>
public sealed record ImpactResult(
    LaunchConditions Launch, double SmashFactor, double SpinLoftDegrees, double FaceToPathDegrees,
    Vector3D BallVelocity, Vector3D BallSpin, Vector3D ClubVelocityAfter, Vector3D ClubAngularVelocityAfter, bool Slid,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Impulse-based oblique impact between a free rigid club head and a ball at rest (aim frame: X right, Y target, Z up).
/// <list type="bullet">
/// <item>Club velocity V = |V|(sin p cos a, cos p cos a, sin a) from path p and attack a; face normal
///   n = (sin f cos L, cos f cos L, sin L) from face angle f and dynamic loft L.</item>
/// <item>The contact is at the face point given by the impact location; r_h runs from the head CG (behind the face by
///   the CG depth) to it, r_b = −R n from the ball centre.</item>
/// <item>For an impulse J on the ball (−J on the club) the contact-point relative velocity changes by K·J with
///   K J = J/m + (I_b⁻¹(r_b × J)) × r_b + J/M + (I_h⁻¹(r_h × J)) × r_h — linear and angular momentum are conserved.</item>
/// <item>Normal: u_n' = −e u_n. Tangential: first try u_t' = −e_t u_t (gripping); if that needs more than μ J_n of
///   friction, the ball slides: J = J_n (n − μ t̂), with J_n set by the normal condition.</item>
/// </list>
/// Gear effect, off-centre speed loss and face-to-path curvature all come out of the rigid-body solution.
/// </summary>
public static class ImpactModel
{
    public static ImpactResult Simulate(ClubDelivery delivery, ClubProperties club, BallProperties ball)
    {
        var warnings = new List<string>();
        double loftDeg = delivery.DynamicLoftDegrees ?? club.LoftDegrees;
        if (delivery.DynamicLoftDegrees is null)
            warnings.Add($"Dynamic loft not given; estimated as the club's static loft ({club.LoftDegrees:0.#}°).");

        const double d = UnitConversion.DegreesToRadians;
        double p = delivery.PathDegrees * d, a = delivery.AttackAngleDegrees * d, f = delivery.FaceAngleDegrees * d, l = loftDeg * d;
        var vClub = new Vector3D(Math.Sin(p) * Math.Cos(a), Math.Cos(p) * Math.Cos(a), Math.Sin(a)) * delivery.ClubSpeed;
        var n = new Vector3D(Math.Sin(f) * Math.Cos(l), Math.Cos(f) * Math.Cos(l), Math.Sin(l));
        var toe = Vector3D.Cross(n, Vector3D.UnitZ).Normalized() * (delivery.LeftHanded ? -1 : 1);
        var up = (Vector3D.UnitZ - n * n.Z).Normalized();
        var heelToe = Vector3D.Cross(n, Vector3D.UnitZ).Normalized();   // handedness-independent face horizontal axis

        double m = ball.MassKg, rBall = ball.RadiusMetres, iBall = ball.MomentOfInertia, mClub = club.Mass;
        var rb = n * -rBall;
        var rh = n * club.CgDepthMetres + toe * delivery.ImpactToeMetres + up * (delivery.ImpactHeightMetres - club.CgHeightMetres);

        // Head inverse inertia in the face frame: vertical axis (up), heel-toe axis, face normal (taken as the vertical MOI).
        Vector3D InvHead(in Vector3D torque) =>
            up * (Vector3D.Dot(torque, up) / club.MoiVertical) + heelToe * (Vector3D.Dot(torque, heelToe) / club.MoiHorizontal)
            + n * (Vector3D.Dot(torque, n) / club.MoiVertical);
        Vector3D K(in Vector3D j) =>
            j / m + Vector3D.Cross(Vector3D.Cross(rb, j) / iBall, rb) + j / mClub + Vector3D.Cross(InvHead(Vector3D.Cross(rh, j)), rh);

        double offCentreCm = Math.Sqrt(delivery.ImpactToeMetres * delivery.ImpactToeMetres + delivery.ImpactHeightMetres * delivery.ImpactHeightMetres) * 100;
        double e = club.Restitution * Math.Max(0.5, 1 - club.RestitutionLossPerSquareCm * offCentreCm * offCentreCm);

        var u = -vClub;   // ball contact point (at rest) relative to the club contact point
        double un = Vector3D.Dot(u, n);
        if (un >= 0) throw new ArgumentException("The club is not moving into the ball (check face angle, loft and path).");
        var ut = u - n * un;

        var kMat = Matrix3.FromColumns(K(Vector3D.UnitX), K(Vector3D.UnitY), K(Vector3D.UnitZ));
        var target = n * (-e * un) + ut * -club.TangentialRestitution;
        var j = kMat.Solve(target - u);
        double jn = Vector3D.Dot(j, n);
        var jt = j - n * jn;
        bool slid = jt.Length > club.FaceFriction * jn;
        if (slid)
        {
            var dir = n - ut.Normalized() * club.FaceFriction;
            jn = -(1 + e) * un / Vector3D.Dot(n, K(dir));
            j = dir * jn;
        }

        var vBall = j / m;
        var wBall = Vector3D.Cross(rb, j) / iBall;
        var vClubAfter = vClub - j / mClub;
        var wClubAfter = InvHead(Vector3D.Cross(rh, -j));

        double speed = vBall.Length;
        var dirBall = vBall / speed;
        var (total, axis, rifle) = LaunchConditions.DecomposeSpin(wBall, dirBall);
        var launch = new LaunchConditions
        {
            BallSpeed = speed,
            LaunchAngle = Math.Asin(Math.Clamp(dirBall.Z, -1, 1)),
            LaunchDirection = Math.Atan2(dirBall.X, dirBall.Y),
            TotalSpin = total,
            SpinAxis = axis,
            RifleSpin = rifle,
            Sources = Enum.GetValues<LaunchParameter>().ToDictionary(k => k, _ => ValueSource.Calculated),
        };
        double spinLoft = Math.Acos(Math.Clamp(Vector3D.Dot(vClub.Normalized(), n), -1, 1)) * UnitConversion.RadiansToDegrees;
        return new ImpactResult(launch, speed / delivery.ClubSpeed, spinLoft, delivery.FaceAngleDegrees - delivery.PathDegrees,
                                vBall, wBall, vClubAfter, wClubAfter, slid, warnings);
    }

    /// <summary>3×3 matrix (columns), for the impulse solve.</summary>
    private readonly record struct Matrix3(Vector3D C0, Vector3D C1, Vector3D C2)
    {
        public static Matrix3 FromColumns(Vector3D c0, Vector3D c1, Vector3D c2) => new(c0, c1, c2);

        /// <summary>Solves M x = b by Cramer's rule (M here is symmetric positive definite, so well conditioned).</summary>
        public Vector3D Solve(in Vector3D b)
        {
            double det = Vector3D.Dot(C0, Vector3D.Cross(C1, C2));
            if (Math.Abs(det) < 1e-300) throw new InvalidOperationException("Impact matrix is singular.");
            return new Vector3D(Vector3D.Dot(b, Vector3D.Cross(C1, C2)), Vector3D.Dot(C0, Vector3D.Cross(b, C2)), Vector3D.Dot(C0, Vector3D.Cross(C1, b))) / det;
        }
    }
}
