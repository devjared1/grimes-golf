namespace GrimesGolf.Physics;

/// <summary>A putt (or any ball struck along the ground). Direction is a heading, degrees clockwise from world +Y.</summary>
public sealed record PuttingRequest
{
    public required ITerrainProvider Terrain { get; init; }
    public double StartX { get; init; }
    public double StartY { get; init; }
    /// <summary>Initial ball speed (m/s).</summary>
    public required double BallSpeed { get; init; }
    public double DirectionDegrees { get; init; }
    /// <summary>
    /// Initial spin as a fraction of rolling spin: 1 = rolling from the start (no skid), 0 = no spin (the ball skids
    /// until friction makes it roll), negative = backspin.
    /// </summary>
    public double InitialRollFraction { get; init; } = 1;
    public BallProperties Ball { get; init; } = BallProperties.Standard;
    public EnvironmentConditions Environment { get; init; } = EnvironmentConditions.Standard;
    public GroundOptions Ground { get; init; } = GroundOptions.Default;
    public double TrajectorySampleIntervalSeconds { get; init; } = 1.0 / 60;
    public double MaximumTimeSeconds { get; init; } = 60;
    /// <summary>Optional rules hook (see <see cref="SimulationOptions.Observer"/>); told about every step.</summary>
    public IGroundObserver? Observer { get; init; }

    public IEnumerable<string> Validate()
    {
        if (!(BallSpeed is > 0 and <= 20)) yield return $"Putt speed {BallSpeed} m/s is outside (0, 20].";
        if (!double.IsFinite(DirectionDegrees) || !double.IsFinite(StartX) || !double.IsFinite(StartY)) yield return "Putt start and direction must be finite.";
        if (!(InitialRollFraction is >= -2 and <= 2)) yield return $"Initial roll fraction {InitialRollFraction} is outside ±2.";
        if (!(TrajectorySampleIntervalSeconds >= 0)) yield return "Trajectory sample interval must be ≥ 0.";
        if (!(MaximumTimeSeconds is > 0 and <= 600)) yield return $"Maximum time {MaximumTimeSeconds} s is outside (0, 600].";
        foreach (var p in Ground.Validate()) yield return p;
        foreach (var p in Ball.Validate()) yield return p;
        foreach (var p in Environment.Atmosphere.Validate()) yield return p;
    }
}

public sealed record PuttingResults
{
    public required IReadOnlyList<TrajectoryPoint> Trajectory { get; init; }
    public Vector3D StartPosition { get; init; }
    public Vector3D FinalPosition { get; init; }
    /// <summary>Length of the path the ball rolled (m).</summary>
    public double DistanceTravelled { get; init; }
    /// <summary>Straight-line horizontal distance from start to finish (m).</summary>
    public double Displacement { get; init; }
    public double TimeToStop { get; init; }
    /// <summary>Acceleration along the direction of travel at the start (m/s²; negative = slowing).</summary>
    public double InitialAcceleration { get; init; }
    /// <summary>Where the ball finished relative to the start line: positive = right of it (m).</summary>
    public double Break { get; init; }
    /// <summary>Largest sideways distance from the start line during the roll (signed, m).</summary>
    public double MaximumBreak { get; init; }
    public SimulationEnd End { get; init; }
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

/// <summary>Putting: the same ground model as a full shot's run-out (<see cref="RollModel"/>), started on the ground.</summary>
public static class PuttingSimulator
{
    public static PuttingResults Simulate(PuttingRequest q)
    {
        var air = AtmosphericModel.Evaluate(q.Environment.Atmosphere);
        double groundZ = q.Terrain.GetElevation(q.StartX, q.StartY);
        var c = new GroundContext
        {
            Ball = q.Ball, Air = air, Wind = q.Environment.Wind, Terrain = q.Terrain, Options = q.Ground, ReferenceZ = groundZ,
        };
        var start = c.OnSurface(q.StartX, q.StartY);
        var n = q.Terrain.GetSurfaceNormal(q.StartX, q.StartY);
        var line = Vector3D.UnitY.RotateHeading(q.DirectionDegrees * UnitConversion.DegreesToRadians);
        var right = Vector3D.Cross(line, Vector3D.UnitZ);
        var v = line.ProjectOnPlane(n).Normalized() * q.BallSpeed;
        var s = new BallState(start, v, Vector3D.Cross(n, v) / q.Ball.RadiusMetres * q.InitialRollFraction);

        double interval = q.TrajectorySampleIntervalSeconds, dt = q.Ground.TimeStepSeconds;
        var trajectory = new List<TrajectoryPoint>(interval > 0 ? (int)(20 / interval) + 16 : 4096);
        var diagnostics = new List<string>();
        double t = 0, nextSample = 0, travelled = 0, maxBreak = 0, initialAccel = 0;
        void Record(in BallState b, BallPhase phase)
        {
            trajectory.Add(new TrajectoryPoint(t, b.Position, b.Velocity, b.SpinRate, b.AngularVelocity.Normalized(), phase));
            nextSample = interval > 0 ? (Math.Floor(t / interval + 1e-9) + 1) * interval : t;
        }
        Record(s, q.InitialRollFraction == 1 ? BallPhase.Rolling : BallPhase.Sliding);

        var end = SimulationEnd.TimeLimit;
        while (t < q.MaximumTimeSeconds)
        {
            var before = s;
            var outcome = RollModel.Step(ref s, ref c, t, dt);
            if (outcome == RollStepOutcome.AtRest) { end = SimulationEnd.Rest; Record(s, BallPhase.Rest); break; }
            t += dt;
            if (t <= dt * 1.5)
                initialAccel = Vector3D.Dot(s.Velocity - before.Velocity, before.Velocity.Normalized()) / dt;
            travelled += s.Position.DistanceTo(before.Position);
            double lat = Vector3D.Dot(s.Position - start, right);
            if (Math.Abs(lat) > Math.Abs(maxBreak)) maxBreak = lat;
            var phase = outcome == RollStepOutcome.Sliding ? BallPhase.Sliding : BallPhase.Rolling;
            if (q.Observer?.ShouldStop(s, t, GroundEventKind.Moving) == true) { end = SimulationEnd.Stopped; Record(s, phase); break; }
            if (t + 1e-12 >= nextSample) Record(s, phase);
        }
        if (end == SimulationEnd.TimeLimit) diagnostics.Add($"The putt was still moving after {q.MaximumTimeSeconds:0.#} s; simulation stopped.");

        return new PuttingResults
        {
            Trajectory = trajectory,
            StartPosition = start,
            FinalPosition = s.Position,
            DistanceTravelled = travelled,
            Displacement = start.HorizontalDistanceTo(s.Position),
            TimeToStop = t,
            InitialAcceleration = initialAccel,
            Break = Vector3D.Dot(s.Position - start, right),
            MaximumBreak = maxBreak,
            End = end,
            Diagnostics = diagnostics,
        };
    }
}
