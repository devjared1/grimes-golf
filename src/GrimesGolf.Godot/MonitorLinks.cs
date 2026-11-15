using GrimesGolf.Core.Garmin;
using GrimesGolf.Core.Monitors;
#if GG_BLUETOOTH
using GrimesGolf.Bluetooth;
#endif

namespace GrimesGolf.Game;

/// <summary>
/// The Bluetooth side of launch monitors, which is different code on each platform. Only Windows has it so far
/// (GrimesGolf.Bluetooth, built in when <c>GG_BLUETOOTH</c> is defined: see the project file). Elsewhere (the iPad)
/// there's no scanner, so no monitor is ever found; shots come from the screen, a GSPro connector app or the
/// development stand-ins.
/// </summary>
internal static class MonitorLinks
{
#if GG_BLUETOOTH
    /// <summary>A scanner for monitors, or null where the app has no Bluetooth support.</summary>
    public static IMonitorScanner? NewScanner() => new MonitorScanner();

    public static ILaunchMonitorSession NewSquareSession(ulong address, Action<string> log) => new SquareMonitorAdapter(address, log);

    public static IR10Link NewR10Link(ulong address, Action<string> log) => new R10BleLink(address, log);
#else
    /// <summary>A scanner for monitors, or null where the app has no Bluetooth support.</summary>
    public static IMonitorScanner? NewScanner() => null;

    public static ILaunchMonitorSession NewSquareSession(ulong address, Action<string> log) => throw Unsupported();

    public static IR10Link NewR10Link(ulong address, Action<string> log) => throw Unsupported();

    private static NotSupportedException Unsupported() => new("Bluetooth launch monitors aren't supported on this device yet.");
#endif
}
