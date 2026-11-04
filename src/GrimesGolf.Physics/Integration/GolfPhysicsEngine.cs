namespace GrimesGolf.Physics;

public enum SimulationInputMode
{
    /// <summary>Measured ball launch if the input has it, otherwise the club impact model.</summary>
    Auto,
    /// <summary>Fly the measured ball launch (Mode A).</summary>
    MeasuredLaunch,
    /// <summary>Calculate the launch from club delivery with the impact model (Mode B).</summary>
    ClubImpact,
}

/// <summary>
/// One shot to simulate. Give either <see cref="Input"/> (launch monitor data, normalised and validated by
/// <see cref="LaunchMonitorAdapter"/>) or <see cref="Launch"/> (already normalised launch conditions).
/// </summary>
public sealed record SimulationRequest
{
    public SimulationInputMode InputMode { get; init; } = SimulationInputMode.Auto;
    public LaunchMonitorInput? Input { get; init; }
    public LaunchConditions? Launch { get; init; }
    /// <summary>Club head for the impact model (Mode B).</summary>
    public ClubProperties? Club { get; init; }
    public BallProperties Ball { get; init; } = BallProperties.Standard;
    public EnvironmentConditions Environment { get; init; } = EnvironmentConditions.Standard;
    public ITerrainProvider Terrain { get; init; } = DefaultTerrain;
    public SimulationOptions Options { get; init; } = SimulationOptions.Default;
    public CalibrationProfile Calibration { get; init; } = CalibrationProfile.None;

    public static ITerrainProvider DefaultTerrain { get; } = new FlatTerrain(Surfaces.Fairway);
}

/// <summary>A request that can't be simulated. <see cref="Problems"/> lists every reason found.</summary>
public sealed class SimulationValidationException : ArgumentException
{
    public SimulationValidationException(IReadOnlyList<string> problems)
        : base("Invalid simulation request: " + string.Join(" ", problems)) => Problems = problems;

    public IReadOnlyList<string> Problems { get; }
}

public interface IGolfPhysicsEngine
{
    FlightResults SimulateShot(SimulationRequest request);
    PuttingResults SimulatePutt(PuttingRequest request);
    IReadOnlyList<FlightResults> SimulateBatch(IEnumerable<SimulationRequest> requests);
}

/// <summary>
/// The engine's entry point. Stateless and thread-safe: requests carry everything (ball, environment, terrain,
/// options, calibration). Invalid requests throw <see cref="SimulationValidationException"/>; warnings (assumed
/// values, inconsistent inputs) and simulation diagnostics are returned in <see cref="FlightResults.Diagnostics"/> and
/// also sent to the optional diagnostics sink.
/// </summary>
public sealed class GolfPhysicsEngine : IGolfPhysicsEngine
{
    private readonly Action<string>? _diagnostics;

    public GolfPhysicsEngine(Action<string>? diagnostics = null) => _diagnostics = diagnostics;

    public FlightResults SimulateShot(SimulationRequest request)
    {
        var problems = new List<string>();
        var warnings = new List<string>();
        problems.AddRange(request.Ball.Validate());
        problems.AddRange(request.Environment.Atmosphere.Validate());
        problems.AddRange(request.Options.Validate());
        problems.AddRange(request.Calibration.Validate());

        LaunchConditions? launch = request.Launch;
        ImpactResult? impact = null;
        if (launch is not null)
        {
            if (request.Input is not null) problems.Add("Give either launch monitor input or launch conditions, not both.");
            problems.AddRange(launch.Validate());
        }
        else if (request.Input is not { } input) problems.Add("No launch: give launch monitor input or launch conditions.");
        else
        {
            var mode = request.InputMode == SimulationInputMode.Auto
                ? (input.HasBallLaunch ? SimulationInputMode.MeasuredLaunch : SimulationInputMode.ClubImpact)
                : request.InputMode;
            if (mode == SimulationInputMode.MeasuredLaunch)
            {
                var norm = LaunchMonitorAdapter.NormalizeBallLaunch(input);
                problems.AddRange(norm.Errors);
                warnings.AddRange(norm.Warnings);
                launch = norm.Launch;
            }
            else
            {
                if (input.HasBallLaunch) warnings.Add("Club impact mode: the measured ball launch is ignored in favour of the impact model.");
                if (request.Club is null) problems.Add("Club impact mode needs club properties.");
                else problems.AddRange(request.Club.Validate());
                var norm = LaunchMonitorAdapter.NormalizeClubDelivery(input);
                problems.AddRange(norm.Errors);
                warnings.AddRange(norm.Warnings);
                if (problems.Count == 0 && norm.Club is { } delivery)
                {
                    try
                    {
                        impact = ImpactModel.Simulate(delivery, request.Club!, request.Ball);
                        warnings.AddRange(impact.Warnings);
                        launch = impact.Launch;
                        problems.AddRange(launch.Validate());
                    }
                    catch (ArgumentException e) { problems.Add(e.Message); }
                }
            }
        }

        if (problems.Count > 0)
        {
            foreach (var p in problems) _diagnostics?.Invoke("Rejected: " + p);
            throw new SimulationValidationException(problems);
        }

        var result = BallFlightSimulator.Simulate(launch!, request.Calibration.Apply(request.Ball), request.Environment,
                                                  request.Calibration.Apply(request.Terrain), request.Options);
        var diagnostics = warnings.Concat(result.Diagnostics).ToArray();
        foreach (var d in diagnostics) _diagnostics?.Invoke(d);
        return result with { Diagnostics = diagnostics, Impact = impact, Input = request.Input };
    }

    public PuttingResults SimulatePutt(PuttingRequest request)
    {
        var problems = request.Validate().ToArray();
        if (problems.Length > 0)
        {
            foreach (var p in problems) _diagnostics?.Invoke("Rejected: " + p);
            throw new SimulationValidationException(problems);
        }
        var result = PuttingSimulator.Simulate(request);
        foreach (var d in result.Diagnostics) _diagnostics?.Invoke(d);
        return result;
    }

    /// <summary>Runs the requests in parallel; results are in request order and identical to running them one by one.</summary>
    public IReadOnlyList<FlightResults> SimulateBatch(IEnumerable<SimulationRequest> requests)
    {
        var list = requests.ToArray();
        var results = new FlightResults[list.Length];
        Parallel.For(0, list.Length, i => results[i] = SimulateShot(list[i]));
        return results;
    }
}
