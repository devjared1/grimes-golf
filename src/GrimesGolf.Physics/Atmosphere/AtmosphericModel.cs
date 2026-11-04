namespace GrimesGolf.Physics;

/// <summary>
/// Weather at the course. Pressure: give <see cref="StationPressurePa"/> (what a barometer on site reads) if known;
/// otherwise the station pressure is worked out from <see cref="SeaLevelPressurePa"/> (what weather reports quote)
/// and <see cref="AltitudeMetres"/> with the ISA barometric formula. Altitude therefore affects the flight only through
/// air density.
/// </summary>
public sealed record AtmosphericConditions
{
    public double TemperatureCelsius { get; init; } = PhysicsConstants.StandardTemperatureCelsius;
    /// <summary>0-100 %.</summary>
    public double RelativeHumidityPercent { get; init; }
    public double AltitudeMetres { get; init; }
    public double SeaLevelPressurePa { get; init; } = PhysicsConstants.StandardPressurePa;
    public double? StationPressurePa { get; init; }

    public static AtmosphericConditions Standard { get; } = new();

    public IEnumerable<string> Validate()
    {
        if (!(TemperatureCelsius is >= -40 and <= 60)) yield return $"Temperature {TemperatureCelsius} °C is outside -40 to 60 °C.";
        if (!(RelativeHumidityPercent is >= 0 and <= 100)) yield return $"Relative humidity {RelativeHumidityPercent} % is outside 0-100 %.";
        if (!(AltitudeMetres is >= -500 and <= 5000)) yield return $"Altitude {AltitudeMetres} m is outside -500 to 5000 m.";
        if (!(SeaLevelPressurePa is >= 85000 and <= 110000)) yield return $"Sea-level pressure {SeaLevelPressurePa} Pa is outside 850-1100 hPa.";
        if (StationPressurePa is { } p && !(p is >= 40000 and <= 110000)) yield return $"Station pressure {p} Pa is outside 400-1100 hPa.";
    }
}

/// <summary>Air properties used by the aerodynamics.</summary>
/// <param name="Density">kg/m³.</param>
/// <param name="DynamicViscosity">Pa·s.</param>
public readonly record struct AirProperties(double Density, double DynamicViscosity, double TemperatureK, double PressurePa, double VapourPressurePa)
{
    public double KinematicViscosity => DynamicViscosity / Density;

    /// <summary>ISA sea level, dry.</summary>
    public static AirProperties Standard => AtmosphericModel.Evaluate(AtmosphericConditions.Standard);
}

/// <summary>
/// Moist-air density and viscosity.
/// <list type="bullet">
/// <item>Station pressure (if not given): p = p₀ (1 − L h / T₀)^(gM/RL), ISA T₀ = 288.15 K, L = 0.0065 K/m.</item>
/// <item>Saturation vapour pressure (Buck 1981, over water): e_s = 611.21 exp((18.678 − T/234.5)(T/(257.14 + T))), T in °C.</item>
/// <item>Density of moist air: ρ = (p − e)/(R_d T) + e/(R_v T), e = RH × e_s (humid air is lighter).</item>
/// <item>Viscosity: Sutherland's law for dry air (the humidity effect on viscosity, under 1 %, is neglected).</item>
/// </list>
/// The air is treated as uniform over the shot (density at the launch altitude); a 30 m apex changes density by ~0.3 %.
/// </summary>
public static class AtmosphericModel
{
    public static AirProperties Evaluate(AtmosphericConditions c)
    {
        double tK = c.TemperatureCelsius + PhysicsConstants.CelsiusToKelvin;
        double p = c.StationPressurePa ?? StationPressure(c.SeaLevelPressurePa, c.AltitudeMetres);
        double e = Math.Clamp(c.RelativeHumidityPercent, 0, 100) / 100 * SaturationVapourPressure(c.TemperatureCelsius);
        double rho = (p - e) / (PhysicsConstants.DryAirGasConstant * tK) + e / (PhysicsConstants.WaterVapourGasConstant * tK);
        return new AirProperties(rho, Viscosity(tK), tK, p, e);
    }

    public static double StationPressure(double seaLevelPressurePa, double altitudeMetres) =>
        seaLevelPressurePa * Math.Pow(1 - PhysicsConstants.IsaLapseRate * altitudeMetres / PhysicsConstants.IsaSeaLevelTemperatureK,
                                      PhysicsConstants.BarometricExponent);

    /// <summary>Buck (1981) saturation vapour pressure over water (Pa).</summary>
    public static double SaturationVapourPressure(double temperatureCelsius)
    {
        double t = temperatureCelsius;
        return 611.21 * Math.Exp((18.678 - t / 234.5) * (t / (257.14 + t)));
    }

    /// <summary>Sutherland's law (Pa·s).</summary>
    public static double Viscosity(double temperatureK)
    {
        const double t0 = PhysicsConstants.SutherlandReferenceTemperatureK, s = PhysicsConstants.SutherlandConstantK;
        return PhysicsConstants.SutherlandReferenceViscosity * Math.Pow(temperatureK / t0, 1.5) * (t0 + s) / (temperatureK + s);
    }
}
