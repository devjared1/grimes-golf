using GrimesGolf.Core.Monitors;

namespace GrimesGolf.Bluetooth;

/// <summary>
/// Reports every advertising Square the iPad hears (one event per advertisement), through CoreBluetooth. Only
/// Squares: the Garmin R10 isn't supported on the iPad yet, so offering it would only lead to a refusal.
/// The first scan makes iOS ask whether the app may use Bluetooth.
/// </summary>
public sealed class AppleMonitorScanner : IMonitorScanner
{
    private volatile bool _scanning;

    public event Action<MonitorAdvertisement>? DeviceSeen;
    public event Action<string>? Stopped;

    public AppleMonitorScanner()
    {
        CoreBluetooth.Advert += OnAdvert;
        CoreBluetooth.Event += OnEvent;
    }

    public bool IsScanning => _scanning;
    public bool CanStart => true;

    public void Start()
    {
        if (_scanning) return;
        _scanning = true;
        CoreBluetooth.StartScan();
    }

    public void Stop()
    {
        if (!_scanning) return;
        _scanning = false;
        CoreBluetooth.StopScan();
        Stopped?.Invoke("Success");
    }

    private void OnAdvert(ulong address, string name, int rssi)
    {
        if (!_scanning || MonitorIdentity.Classify(name) != LaunchMonitorType.Square) return;
        DeviceSeen?.Invoke(new MonitorAdvertisement(LaunchMonitorType.Square, name, address, (short)Math.Clamp(rssi, short.MinValue, short.MaxValue), DateTime.Now));
    }

    private void OnEvent(int kind, int token, ulong address, byte[] data)
    {
        if (kind != CoreBluetooth.ScanStopped || !_scanning) return;
        _scanning = false;
        Stopped?.Invoke(System.Text.Encoding.UTF8.GetString(data));
    }

    public void Dispose()
    {
        Stop();
        CoreBluetooth.Advert -= OnAdvert;
        CoreBluetooth.Event -= OnEvent;
    }
}
