using GrimesGolf.Core.Square;
using GrimesGolf.Physics;

namespace GrimesGolf.Core.Monitors;

/// <summary>Which launch monitors to look for (Settings), and which kind a found one is.</summary>
public enum LaunchMonitorType { Auto, Square, GarminR10 }

/// <summary>What a monitor can do beyond connecting and sending shots. The UI hides what a monitor lacks.</summary>
[Flags]
public enum MonitorCapabilities
{
    None = 0,
    /// <summary>Reports its battery level.</summary>
    Battery = 1,
    /// <summary>Sees the ball before the shot (<see cref="IBallSensingMonitor"/>).</summary>
    BallSensing = 2,
    /// <summary>Takes a target line from an alignment stick (<see cref="IAlignmentMonitor"/>).</summary>
    AlignmentStick = 4,
    /// <summary>Is told the club and hand, and uses them (<see cref="ILaunchMonitorSession.SelectClubAsync"/>).</summary>
    ClubSelection = 8,
    /// <summary>Reports how long it has been on (<see cref="IMonitorClock"/>).</summary>
    Uptime = 16,
}

/// <summary>A line in the monitor drawer's device details ("Serial", "Firmware", ...).</summary>
public readonly record struct MonitorStat(string Label, string Value);

/// <param name="Description">What it is, for the HUD and the home player ("Square Omni (hardware 3.1)", "Garmin Approach R10").</param>
/// <param name="Identified">The monitor said what it is (false: <paramref name="Description"/> is a guess).</param>
/// <param name="Stats">Device details for the drawer.</param>
public sealed record MonitorDeviceInfo(string Description, bool Identified, IReadOnlyList<MonitorStat> Stats);

/// <param name="Charging">How it's powered, in words ("charging", "on battery"); empty when the monitor doesn't say.</param>
public sealed record MonitorBattery(int Level, string Charging);

/// <summary>Whether the monitor is ready for a shot, in words for the Ready light.</summary>
/// <param name="Problem">It needs attention (an error, tilted, overheating).</param>
public sealed record MonitorReadiness(bool Ready, string Text, bool Problem = false);

/// <summary>A shot as the monitor measured it.</summary>
/// <param name="ReportedSmash">Smash factor as the monitor itself worked it out (null: work it out from the speeds).</param>
/// <param name="Notes">Things worth knowing about the numbers (spin estimated rather than measured, club not tracked).</param>
public sealed record MonitorShot(LaunchMonitorInput Input, double? ReportedSmash = null, IReadOnlyList<string>? Notes = null);

/// <summary>
/// One connection to a launch monitor, whatever make it is: connects, arms it, and reports its status and shots.
/// Events are raised on a background thread; the UI marshals them. Monitor-specific features are optional
/// interfaces (<see cref="IBallSensingMonitor"/>, <see cref="IAlignmentMonitor"/>, <see cref="IMonitorClock"/>),
/// announced in <see cref="Capabilities"/>.
/// </summary>
public interface ILaunchMonitorSession
{
    LaunchMonitorType Type { get; }
    MonitorCapabilities Capabilities { get; }

    /// <summary>Connected and identified (the monitor is then live, though maybe not ready yet).</summary>
    event Action<MonitorDeviceInfo>? Connected;
    event Action<MonitorBattery>? Battery;
    /// <summary>The monitor's own state, in words for the drawer ("Detecting", "Waiting", ...).</summary>
    event Action<string>? StateChanged;
    /// <summary>Ready for a shot or not. Monitors with <see cref="MonitorCapabilities.BallSensing"/> say so with their sensor instead.</summary>
    event Action<MonitorReadiness>? ReadinessChanged;
    event Action<MonitorShot>? Shot;
    /// <summary>The session is over (dropped, failed or stopped), with why.</summary>
    event Action<string>? Ended;

    /// <summary>Connects and runs until the link drops or <see cref="Stop"/>; never throws (failures end it with a reason).</summary>
    Task RunAsync(string club, bool leftHanded);

    void Stop();

    /// <summary>The club and hand on screen. Monitors without <see cref="MonitorCapabilities.ClubSelection"/> just note the hand for their shots.</summary>
    Task SelectClubAsync(string club, bool leftHanded);
}

/// <summary>A monitor that sees the ball before the shot (the Square's ready and position readings).</summary>
public interface IBallSensingMonitor
{
    event Action<SensorEvent>? Sensor;
}

/// <summary>A monitor that takes its target line from an alignment stick.</summary>
public interface IAlignmentMonitor
{
    event Action<StickEvent>? Stick;
    Task BeginAlignmentAsync(bool leftHanded);
    Task EndAlignmentAsync(bool confirm, double angleDeg, string club, bool leftHanded);
}

/// <summary>A monitor that reports its uptime (seconds since it was switched on), which shows when it has restarted.</summary>
public interface IMonitorClock
{
    event Action<ulong>? Clock;
}

/// <summary>A stand-in monitor for development runs: HIT SHOT sends this shot as if it had been hit.</summary>
public interface IFakeMonitor
{
    void Hit(RangeShot shot);
}

/// <summary>Tells the supported monitors apart by what they advertise over Bluetooth.</summary>
public static class MonitorIdentity
{
    /// <summary>The Omni (and the earlier Square) advertise as "SquareGolf(XXXX)".</summary>
    public const string SquareNamePrefix = "SquareGolf";
    /// <summary>The R10's advertised name (unless renamed in the Garmin Golf app).</summary>
    public const string GarminR10Name = "Approach R10";
    /// <summary>Garmin's Bluetooth company identifier, and the R10's product number in its manufacturer data.</summary>
    public const ushort GarminCompanyId = 0x0087, GarminR10Product = 3622;

    /// <summary>
    /// What kind of supported monitor an advertisement is from, or null if none. <paramref name="garminProduct"/> is
    /// the product number from Garmin manufacturer data, when there was any.
    /// </summary>
    public static LaunchMonitorType? Classify(string? name, ushort? garminProduct = null)
    {
        if (!string.IsNullOrEmpty(name) && name.StartsWith(SquareNamePrefix, StringComparison.OrdinalIgnoreCase)) return LaunchMonitorType.Square;
        if (garminProduct == GarminR10Product) return LaunchMonitorType.GarminR10;
        if (!string.IsNullOrEmpty(name) && name.Contains(GarminR10Name, StringComparison.OrdinalIgnoreCase)) return LaunchMonitorType.GarminR10;
        return null;
    }

    /// <summary>
    /// Whether a found monitor is one to offer: of the type chosen in Settings (any, for Auto) and, when an exact
    /// device name is set, with that name.
    /// </summary>
    public static bool Wanted(LaunchMonitorType found, string? name, LaunchMonitorType choice, string? exactName) =>
        (choice == LaunchMonitorType.Auto || choice == found) &&
        (string.IsNullOrWhiteSpace(exactName) || string.Equals(name?.Trim(), exactName.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The Garmin product number in manufacturer data (its first two bytes, little-endian), or null.</summary>
    public static ushort? GarminProduct(ReadOnlySpan<byte> manufacturerData) =>
        manufacturerData.Length >= 2 ? (ushort)(manufacturerData[0] | manufacturerData[1] << 8) : null;

    /// <summary>How a type is named in Settings and the HUD.</summary>
    public static string Label(LaunchMonitorType type) => type switch
    {
        LaunchMonitorType.Square => "Square Golf Omni",
        LaunchMonitorType.GarminR10 => "Garmin Approach R10",
        _ => "Any supported monitor",
    };
}
