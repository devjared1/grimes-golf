namespace GrimesGolf.Physics;

/// <summary>
/// Equations of motion in the air: m dv/dt = F_gravity + F_drag + F_lift, I dω/dt = τ_spin-decay (see
/// <see cref="AerodynamicForces"/>), with the aerodynamics computed from the velocity relative to the wind.
/// </summary>
internal struct FlightDerivative : IStateDerivative
{
    public BallProperties Ball;
    public AirProperties Air;
    public IWindField Wind;
    public double ReferenceZ;

    private static readonly Vector3D GravityAcceleration = new(0, 0, -PhysicsConstants.Gravity);

    public BallState Evaluate(in BallState s, double time)
    {
        var wind = Wind.VelocityAt(s.Position, s.Position.Z - ReferenceZ, time);
        var a = AerodynamicForces.Evaluate(Ball, Air, s.Velocity - wind, s.AngularVelocity);
        return new BallState(s.Velocity, a.DragAcceleration + a.LiftAcceleration + GravityAcceleration, a.AngularAcceleration);
    }
}

/// <summary>
/// Runs a shot from launch to rest: RK4 flight at a fixed step until the ball meets the terrain (the contact time is
/// found by bisection within the step), an impulse bounce (<see cref="BounceModel"/>), further flights after each
/// bounce, and once the bounces die out the ground phase (<see cref="RollModel"/>) until the ball stops.
/// Deterministic: identical inputs give identical results.
/// </summary>
public static class BallFlightSimulator
{
    private const int ContactBisections = 40;

    public static FlightResults Simulate(LaunchConditions launch, BallProperties ball, EnvironmentConditions environment,
                                         ITerrainProvider terrain, SimulationOptions options)
    {
        var o = options;
        var air = AtmosphericModel.Evaluate(environment.Atmosphere);
        double heading = o.AimHeadingDegrees * UnitConversion.DegreesToRadians;
        double r = ball.RadiusMetres;

        double groundZ = terrain.GetElevation(o.StartX, o.StartY);
        var n0 = terrain.GetSurfaceNormal(o.StartX, o.StartY);
        var start = new Vector3D(o.StartX, o.StartY, groundZ + r / n0.Z + o.TeeHeightMetres);
        var direction = launch.Direction.RotateHeading(heading);
        var state = new BallState(start, direction * launch.BallSpeed, launch.SpinVector.RotateHeading(heading));

        var aimRight = Vector3D.UnitX.RotateHeading(heading);
        var launchHorizontal = direction.Horizontal.Normalized();
        if (launchHorizontal == Vector3D.Zero) launchHorizontal = Vector3D.UnitY.RotateHeading(heading);
        var launchRight = Vector3D.Cross(launchHorizontal, Vector3D.UnitZ);
        double Lateral(in Vector3D p) => Vector3D.Dot(p - start, aimRight);

        var flight = new FlightDerivative { Ball = ball, Air = air, Wind = environment.Wind, ReferenceZ = groundZ };
        var ground = new GroundContext { Ball = ball, Air = air, Wind = environment.Wind, Terrain = terrain, Options = o.Ground, ReferenceZ = groundZ };
        double Clearance(in BallState s)
        {
            var p = s.Position;
            return (p.Z - terrain.GetElevation(p.X, p.Y)) * terrain.GetSurfaceNormal(p.X, p.Y).Z - r;
        }

        double interval = o.TrajectorySampleIntervalSeconds;
        var trajectory = new List<TrajectoryPoint>(interval > 0 ? (int)(12 / interval) + 64 : 4096);
        var contacts = new List<GroundContactEvent>();
        var diagnostics = new List<string>();
        double t = 0, nextSample = 0;
        void Record(in BallState s, BallPhase phase, bool force = false)
        {
            if (!force && t + 1e-12 < nextSample) return;
            trajectory.Add(new TrajectoryPoint(t, s.Position, s.Velocity, s.SpinRate, s.AngularVelocity.Normalized(), phase));
            nextSample = interval > 0 ? (Math.Floor(t / interval + 1e-9) + 1) * interval : t;
        }
        Record(state, BallPhase.Flight, force: true);

        bool landed = false;
        int landingIndex = 0, impacts = 0, bounces = 0;
        double apex = 0, maxLateral = 0, timeOfFlight = 0;
        Vector3D landingPos = start, landingVel = Vector3D.Zero;
        SimulationEnd end;
        double dt = o.TimeStepSeconds;

        while (true)
        {
            // Flight until the ball meets the ground.
            double segmentStart = t;
            bool hit = false;
            while (t - segmentStart < o.MaximumFlightTimeSeconds)
            {
                var next = NumericalIntegrator.Rk4(ref flight, state, t, dt);
                if (Clearance(next) < 0)
                {
                    double lo = 0, hi = dt;
                    for (int i = 0; i < ContactBisections; i++)
                    {
                        double mid = (lo + hi) / 2;
                        if (Clearance(NumericalIntegrator.Rk4(ref flight, state, t, mid)) < 0) hi = mid; else lo = mid;
                    }
                    state = NumericalIntegrator.Rk4(ref flight, state, t, hi);
                    t += hi;
                    hit = true;
                    break;
                }
                state = next;
                t += dt;
                if (!landed)
                {
                    apex = Math.Max(apex, state.Position.Z - start.Z);
                    double lat = Lateral(state.Position);
                    if (Math.Abs(lat) > Math.Abs(maxLateral)) maxLateral = lat;
                }
                Record(state, landed ? BallPhase.Bounce : BallPhase.Flight);
            }
            if (!hit)
            {
                diagnostics.Add($"The ball was still in the air after {o.MaximumFlightTimeSeconds:0.#} s; simulation stopped.");
                end = SimulationEnd.TimeLimit;
                break;
            }

            // Ground impact.
            impacts++;
            var p = state.Position;
            state = state with { Position = ground.OnSurface(p.X, p.Y) };
            var n = terrain.GetSurfaceNormal(p.X, p.Y);
            var surface = ground.SurfaceAt(p.X, p.Y);
            string surfaceName = terrain.GetSurfaceProperties(p.X, p.Y).Name;
            bool first = !landed;
            if (first)
            {
                landed = true;
                timeOfFlight = t;
                landingPos = state.Position;
                landingVel = state.Velocity;
            }
            Record(state, first ? BallPhase.Flight : BallPhase.Bounce, force: true);
            if (first) landingIndex = trajectory.Count - 1;

            var kind = first ? GroundContactKind.Landing : GroundContactKind.Bounce;
            if (o.Observer?.ShouldStop(state, t, GroundEventKind.Impact) == true)
            {
                contacts.Add(new GroundContactEvent(kind, t, state.Position, surfaceName, -Vector3D.Dot(state.Velocity, n), 0, 0, 0, false));
                end = SimulationEnd.Stopped;
                break;
            }
            if (!o.EnableBounce)
            {
                contacts.Add(new GroundContactEvent(kind, t, state.Position, surfaceName, -Vector3D.Dot(state.Velocity, n), 0, 0, 0, false));
                end = SimulationEnd.Landed;
                break;
            }

            var b = BounceModel.Resolve(state, n, surface, ball);
            state = b.Outgoing;
            contacts.Add(new GroundContactEvent(kind, t, state.Position, surfaceName, b.NormalSpeedIn, b.NormalSpeedOut, b.Restitution,
                                                b.CraterAngle * UnitConversion.RadiansToDegrees, b.Slid));

            if (b.NormalSpeedOut < o.RollTransitionNormalSpeed)
            {
                state = state with { Velocity = state.Velocity.ProjectOnPlane(n) };
                contacts.Add(new GroundContactEvent(GroundContactKind.StartedRolling, t, state.Position, surfaceName, 0, 0, 0, 0, b.Slid));
                if (!o.EnableRoll) { end = SimulationEnd.StoppedBouncing; break; }
                end = RunGroundPhase(ref state, ref ground, ref t, o, Record, contacts, diagnostics);
                break;
            }

            bounces++;
            if (impacts >= o.MaximumGroundImpacts)
            {
                diagnostics.Add($"Stopped after {impacts} ground impacts without the ball settling.");
                end = SimulationEnd.TimeLimit;
                break;
            }
        }

        if (trajectory[^1].Time < t) Record(state, end == SimulationEnd.Rest ? BallPhase.Rest : BallPhase.Bounce, force: true);
        if (!landed)
        {
            landingPos = state.Position;
            timeOfFlight = t;
            landingIndex = trajectory.Count - 1;
        }

        var final = state.Position;
        double hLanding = landingVel.HorizontalLength;
        return new FlightResults
        {
            Launch = launch,
            Trajectory = trajectory,
            LandingIndex = landingIndex,
            StartPosition = start,
            AimHeadingDegrees = o.AimHeadingDegrees,
            CarryDistance = start.HorizontalDistanceTo(landingPos),
            TotalDistance = start.HorizontalDistanceTo(final),
            ApexHeight = apex,
            TimeOfFlight = timeOfFlight,
            LandingAngleDegrees = landed ? Math.Atan2(-landingVel.Z, hLanding) * UnitConversion.RadiansToDegrees : 0,
            LandingSpeed = landingVel.Length,
            LandingPosition = landingPos,
            LandingVelocity = landingVel,
            RollDistance = landed ? landingPos.HorizontalDistanceTo(final) : 0,
            FinalPosition = final,
            CarryLateral = Lateral(landingPos),
            FinalLateral = Lateral(final),
            MaximumLateralDeviation = maxLateral,
            Curvature = Vector3D.Dot(landingPos - start, launchRight),
            BounceCount = bounces,
            GroundContacts = contacts,
            TotalTime = t,
            End = end,
            Diagnostics = diagnostics,
        };
    }

    private delegate void Recorder(in BallState s, BallPhase phase, bool force = false);

    private static SimulationEnd RunGroundPhase(ref BallState state, ref GroundContext ground, ref double t, SimulationOptions o,
                                                Recorder record, List<GroundContactEvent> contacts, List<string> diagnostics)
    {
        double dt = o.Ground.TimeStepSeconds, t0 = t;
        while (t - t0 < o.MaximumGroundTimeSeconds)
        {
            var outcome = RollModel.Step(ref state, ref ground, t, dt);
            if (outcome == RollStepOutcome.AtRest)
            {
                var p = state.Position;
                contacts.Add(new GroundContactEvent(GroundContactKind.CameToRest, t, p, ground.Terrain.GetSurfaceProperties(p.X, p.Y).Name, 0, 0, 0, 0, false));
                record(state, BallPhase.Rest, force: true);
                return SimulationEnd.Rest;
            }
            t += dt;
            if (o.Observer?.ShouldStop(state, t, GroundEventKind.Moving) == true)
            {
                record(state, outcome == RollStepOutcome.Sliding ? BallPhase.Sliding : BallPhase.Rolling, force: true);
                return SimulationEnd.Stopped;
            }
            record(state, outcome == RollStepOutcome.Sliding ? BallPhase.Sliding : BallPhase.Rolling);
        }
        diagnostics.Add($"The ball was still moving on the ground after {o.MaximumGroundTimeSeconds:0.#} s; simulation stopped.");
        return SimulationEnd.TimeLimit;
    }
}
