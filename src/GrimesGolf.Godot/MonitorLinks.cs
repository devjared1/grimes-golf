using GrimesGolf.Core.Garmin;
using GrimesGolf.Core.Monitors;
#if GG_BLUETOOTH
using GrimesGolf.Bluetooth;
#endif

namespace GrimesGolf.Game;

/// <summary>
/// The Bluetooth side of launch monitors, which is different code on each platform (GrimesGolf.Bluetooth, built in
/// when <c>GG_BLUETOOTH</c> is defined: see the project file). Windows has Squares and the Garmin R10; the iPad
/// (<c>GG_IOS</c>) has Squares only so far. Where there's none there's no scanner, so no monitor is ever found, and
/// shots come from the screen, a GSPro connector app or the development stand-ins.
/// </summary>
internal static class MonitorLinks
{
#if GG_BLUETOOTH
    /// <summary>A scanner for monitors, or null where the app has no Bluetooth support.</summary>
#if GG_IOS
    public static IMonitorScanner? NewScanner() => new AppleMonitorScanner();
#else
    public static IMonitorScanner? NewScanner() => new MonitorScanner();
#endif

    public static ILaunchMonitorSession NewSquareSession(ulong address, Action<string> log) => new SquareMonitorAdapter(address, log);

#if GG_IOS
    public static IR10Link NewR10Link(ulong address, Action<string> log) => throw new NotSupportedException("The Garmin R10 isn't supported on this device yet.");
#else
    public static IR10Link NewR10Link(ulong address, Action<string> log) => new R10BleLink(address, log);
#endif
#else
    /// <summary>A scanner for monitors, or null where the app has no Bluetooth support.</summary>
    public static IMonitorScanner? NewScanner() => null;

    public static ILaunchMonitorSession NewSquareSession(ulong address, Action<string> log) => throw Unsupported();

    public static IR10Link NewR10Link(ulong address, Action<string> log) => throw Unsupported();

    private static NotSupportedException Unsupported() => new("Bluetooth launch monitors aren't supported on this device yet.");
#endif
}
