namespace SquareGspro.Physics;

/// <summary>Physical constants (SI).</summary>
public static class PhysicsConstants
{
    /// <summary>Standard gravity (m/s²).</summary>
    public const double Gravity = 9.80665;

    /// <summary>ISA sea-level air density (kg/m³), 15 °C, 101325 Pa, dry.</summary>
    public const double StandardAirDensity = 1.225;
    public const double StandardTemperatureCelsius = 15.0;
    public const double StandardPressurePa = 101325.0;

    public const double CelsiusToKelvin = 273.15;

    /// <summary>Specific gas constants (J/(kg·K)) for dry air and water vapour.</summary>
    public const double DryAirGasConstant = 287.058;
    public const double WaterVapourGasConstant = 461.495;

    /// <summary>ISA troposphere temperature lapse rate (K/m) and the barometric exponent g·M/(R·L).</summary>
    public const double IsaLapseRate = 0.0065;
    public const double IsaSeaLevelTemperatureK = 288.15;
    public const double BarometricExponent = 5.25588;

    /// <summary>Sutherland's law for air viscosity: reference viscosity (Pa·s) at the reference temperature (K), and the Sutherland constant (K).</summary>
    public const double SutherlandReferenceViscosity = 1.716e-5;
    public const double SutherlandReferenceTemperatureK = 273.15;
    public const double SutherlandConstantK = 110.4;
}

/// <summary>Unit conversions. The engine is SI inside; convert at the boundary with these.</summary>
public static class UnitConversion
{
    public const double MphToMetresPerSecond = 0.44704;
    public const double MetresPerSecondToMph = 1 / MphToMetresPerSecond;
    public const double YardsToMetres = 0.9144;
    public const double MetresToYards = 1 / YardsToMetres;
    public const double FeetToMetres = 0.3048;
    public const double MetresToFeet = 1 / FeetToMetres;
    public const double InchesToMetres = 0.0254;
    public const double RpmToRadiansPerSecond = 2 * Math.PI / 60;
    public const double RadiansPerSecondToRpm = 60 / (2 * Math.PI);
    public const double DegreesToRadians = Math.PI / 180;
    public const double RadiansToDegrees = 180 / Math.PI;
    public const double HectopascalsToPascals = 100;

    public static double FahrenheitToCelsius(double f) => (f - 32) * 5 / 9;
    public static double CelsiusToFahrenheit(double c) => c * 9 / 5 + 32;
}
