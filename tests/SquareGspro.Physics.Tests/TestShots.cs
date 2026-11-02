namespace SquareGspro.Physics.Tests;

/// <summary>Shared shot set-ups for the tests.</summary>
internal static class TestShots
{
    public static readonly ITerrainProvider Fairway = new FlatTerrain(Surfaces.Fairway);

    public static LaunchConditions Driver(double axisDeg = 0, double directionDeg = 0) =>
        LaunchConditions.FromDisplayUnits(167, 10.9, directionDeg, 2686, axisDeg);

    public static LaunchConditions SevenIron(double axisDeg = 0) => LaunchConditions.FromDisplayUnits(120, 16.3, 0, 7097, axisDeg);

    public static FlightResults Fly(LaunchConditions launch, EnvironmentConditions? env = null, ITerrainProvider? terrain = null,
                                    SimulationOptions? options = null, BallProperties? ball = null) =>
        BallFlightSimulator.Simulate(launch, ball ?? BallProperties.Standard, env ?? EnvironmentConditions.Standard,
                                     terrain ?? Fairway, options ?? SimulationOptions.Default);

    public static FlightResults Carry(LaunchConditions launch, EnvironmentConditions? env = null, ITerrainProvider? terrain = null) =>
        Fly(launch, env, terrain, SimulationOptions.Default with { EnableBounce = false });

    public static EnvironmentConditions WithWind(IWindField wind) => EnvironmentConditions.Standard with { Wind = wind };

    public static EnvironmentConditions WithAir(double tempC = 15, double altitude = 0, double humidity = 0) =>
        EnvironmentConditions.Standard with
        {
            Atmosphere = new AtmosphericConditions { TemperatureCelsius = tempC, AltitudeMetres = altitude, RelativeHumidityPercent = humidity },
        };
}
