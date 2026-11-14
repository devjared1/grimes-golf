using GrimesGolf.Core.Monitors;
using GrimesGolf.Core.Square;

namespace GrimesGolf.Bluetooth;

/// <summary>
/// A Square monitor as an <see cref="ILaunchMonitorSession"/>: the same <see cref="MonitorSession"/> as before, its
/// events passed on in the device-neutral form, and its Square-only features (ball sensing, the alignment stick,
/// its clock) through the optional interfaces.
/// </summary>
public sealed class SquareMonitorAdapter : ILaunchMonitorSession, IBallSensingMonitor, IAlignmentMonitor, IMonitorClock
{
    private readonly MonitorSession _session;
    private volatile bool _leftHanded;

    public SquareMonitorAdapter(ulong address, Action<string> log)
    {
        _session = new MonitorSession(address, log);
        _session.InfoRead += info => Connected?.Invoke(SquareMonitor.Info(info));
        _session.Battery += b => Battery?.Invoke(SquareMonitor.Battery(b));
        _session.StateChanged += s => StateChanged?.Invoke(s.ToString());
        _session.Sensor += s => Sensor?.Invoke(s);
        _session.Stick += s => Stick?.Invoke(s);
        _session.Clock += c => Clock?.Invoke(c);
        _session.Shot += (ball, club) => Shot?.Invoke(SquareShots.ToMonitorShot(ball, club, _leftHanded));
        _session.Ended += r => Ended?.Invoke(r);
    }

    public LaunchMonitorType Type => LaunchMonitorType.Square;
    public MonitorCapabilities Capabilities => SquareMonitor.Capabilities;

    public event Action<MonitorDeviceInfo>? Connected;
    public event Action<MonitorBattery>? Battery;
    public event Action<string>? StateChanged;
    /// <summary>The Square says whether it's ready through its ball sensor (<see cref="Sensor"/>).</summary>
    public event Action<MonitorReadiness>? ReadinessChanged { add { } remove { } }
    public event Action<MonitorShot>? Shot;
    public event Action<string>? Ended;
    public event Action<SensorEvent>? Sensor;
    public event Action<StickEvent>? Stick;
    public event Action<ulong>? Clock;

    public Task RunAsync(string club, bool leftHanded)
    {
        _leftHanded = leftHanded;
        return _session.RunAsync(club, leftHanded);
    }

    public void Stop() => _session.Stop();

    public Task SelectClubAsync(string club, bool leftHanded)
    {
        _leftHanded = leftHanded;
        return _session.SelectClubAsync(club, leftHanded);
    }

    public Task BeginAlignmentAsync(bool leftHanded) => _session.BeginAlignmentAsync(leftHanded);

    public Task EndAlignmentAsync(bool confirm, double angleDeg, string club, bool leftHanded)
    {
        _leftHanded = leftHanded;
        return _session.EndAlignmentAsync(confirm, angleDeg, club, leftHanded);
    }
}
