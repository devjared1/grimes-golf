using GrimesGolf.Core.Monitors;

namespace GrimesGolf.Bluetooth;

/// <summary>
/// Reports every advertising launch monitor the iPad hears (one event per advertisement), through CoreBluetooth: a
/// Square by its name, a Garmin R10 by its name or by Garmin's manufacturer data (as the Windows scanner does). An R10
/// only advertises while nothing else (a phone) is connected to it.
/// The first scan makes iOS ask whether the app may use Bluetooth.
/// </summary>
public sealed class AppleMonitorScanner : IMonitorScanner
{
    private volatile bool _scanning;
    /// <summary>Names and types heard per address: the R10 can send its name and its manufacturer data in separate packets.</summary>
    private readonly Dictionary<ulong, string> _names = new();
    private readonly Dictionary<ulong, LaunchMonitorType> _types = new();

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

    private void OnAdvert(ulong address, string name, int rssi, byte[] manufacturer)
    {
        if (!_scanning) return;
        // Manufacturer data starts with the company identifier (little-endian); the product number follows it.
        ushort? product = null;
        if (manufacturer.Length >= 4 && (manufacturer[0] | manufacturer[1] << 8) == MonitorIdentity.GarminCompanyId)
            product = MonitorIdentity.GarminProduct(manufacturer.AsSpan(2));
        lock (_names)
        {
            if (!string.IsNullOrEmpty(name)) _names[address] = name;
            else _names.TryGetValue(address, out name!);
            if (MonitorIdentity.Classify(name, product) is { } found) _types[address] = found;
            if (!_types.TryGetValue(address, out var type)) return;
            DeviceSeen?.Invoke(new MonitorAdvertisement(type, string.IsNullOrEmpty(name) ? MonitorIdentity.GarminR10Name : name, address,
                (short)Math.Clamp(rssi, short.MinValue, short.MaxValue), DateTime.Now));
        }
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
