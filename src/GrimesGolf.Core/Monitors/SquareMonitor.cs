using GrimesGolf.Core.Course;
using GrimesGolf.Core.Square;

namespace GrimesGolf.Core.Monitors;

/// <summary>How a Square monitor is described to the UI (the same details the drawer has always shown).</summary>
public static class SquareMonitor
{
    public const MonitorCapabilities Capabilities = MonitorCapabilities.Battery | MonitorCapabilities.BallSensing |
        MonitorCapabilities.AlignmentStick | MonitorCapabilities.ClubSelection | MonitorCapabilities.Uptime;

    public static MonitorDeviceInfo Info(SquareDeviceInfo info) => new(info.Description, info.HardwareVersion != null, new MonitorStat[]
    {
        new("Serial", info.Serial ?? "—"),
        new("Hardware", info.HardwareVersion ?? "—"),
        new("Firmware (LM)", info.FirmwareComponents.GetValueOrDefault("lm") ?? info.FirmwareRaw ?? "—"),
        new("Firmware (MMI)", info.FirmwareComponents.GetValueOrDefault("mmi") ?? "—"),
        new("Firmware (launcher)", info.FirmwareComponents.GetValueOrDefault("launcher") ?? "—"),
    });

    public static MonitorBattery Battery(BatteryEvent b) => new(b.Level, ChargingText(b.ChargingState));

    public static string ChargingText(int state) => state switch
    {
        0 => "not charging", 1 => "on battery", 2 => "charging", 3 => "full", 4 => "no battery", _ => "unknown",
    };
}

/// <summary>
/// A pretend Square for <c>--fake-monitor=square</c>: it connects, reports a battery and a ball ready to hit, and
/// HIT SHOT sends the shot through the same conversion a real one's goes through. It doesn't do the alignment stick,
/// and it doesn't exercise the Bluetooth packets (the parser has its own tests).
/// </summary>
public sealed class FakeSquareSession : ILaunchMonitorSession, IBallSensingMonitor, IMonitorClock, IFakeMonitor
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<string> _log;
    private volatile bool _leftHanded;
    private ulong _clock;

    public FakeSquareSession(Action<string> log) => _log = log;

    public LaunchMonitorType Type => LaunchMonitorType.Square;
    public MonitorCapabilities Capabilities => SquareMonitor.Capabilities & ~MonitorCapabilities.AlignmentStick;

    public event Action<MonitorDeviceInfo>? Connected;
    public event Action<MonitorBattery>? Battery;
    public event Action<string>? StateChanged;
    public event Action<MonitorReadiness>? ReadinessChanged { add { } remove { } }
    public event Action<MonitorShot>? Shot;
    public event Action<string>? Ended;
    public event Action<SensorEvent>? Sensor;
    public event Action<ulong>? Clock;

    public async Task RunAsync(string club, bool leftHanded)
    {
        _leftHanded = leftHanded;
        var ct = _cts.Token;
        try
        {
            await Task.Delay(300, ct);
            _log("Connected (fake Square)");
            Connected?.Invoke(SquareMonitor.Info(new SquareDeviceInfo("FAKE-SQUARE", "3.1", "{\"launcher\":\"1.0.1\",\"mmi\":\"1.4.0\",\"lm\":\"1.5.8\"}")));
            Battery?.Invoke(SquareMonitor.Battery(new BatteryEvent(85, 1)));
            StateChanged?.Invoke(SquareDeviceState.Detecting.ToString());
            Sensor?.Invoke(new SensorEvent(BallReady: true, BallDetected: true, new BallPosition(-40, -30, -33)));
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(3000, ct);
                Clock?.Invoke(_clock += 3);
            }
        }
        catch (OperationCanceledException) { }
        Ended?.Invoke("Disconnected");
    }

    public void Stop() => _cts.Cancel();

    public Task SelectClubAsync(string club, bool leftHanded)
    {
        _leftHanded = leftHanded;
        return Task.CompletedTask;
    }

    public void Hit(RangeShot shot)
    {
        // As the Square sends it: spin axis and sidespin negative = curving right.
        double rad = shot.SpinAxisDeg * Math.PI / 180;
        var ball = new BallMetricsEvent(shot.BallMph / Units.MsToMph, shot.LaunchDeg, shot.DirectionDeg, (int)Math.Round(shot.SpinRpm),
            -shot.SpinAxisDeg, (int)Math.Round(shot.SpinRpm * Math.Cos(rad)), (int)Math.Round(-shot.SpinRpm * Math.Sin(rad)));
        var club = shot.ClubMph is { } c ? new ClubMetricsEvent(0x41, 1.5, 0.5, -2, null, null, null, c / Units.MsToMph, null) : null;
        Sensor?.Invoke(new SensorEvent(false, false));
        Shot?.Invoke(SquareShots.ToMonitorShot(ball, club, _leftHanded, "Square Golf (fake)"));
        _ = Task.Delay(1500, _cts.Token).ContinueWith(_ => Sensor?.Invoke(new SensorEvent(true, true, new BallPosition(-40, -30, -33))),
            TaskContinuationOptions.OnlyOnRanToCompletion);
    }
}
