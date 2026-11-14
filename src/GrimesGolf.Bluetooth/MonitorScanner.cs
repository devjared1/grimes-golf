using GrimesGolf.Core.Monitors;
using Windows.Devices.Bluetooth.Advertisement;

namespace GrimesGolf.Bluetooth;

public sealed record MonitorAdvertisement(LaunchMonitorType Type, string Name, ulong Address, short Rssi, DateTime SeenAt)
{
    public string AddressText => string.Join(":", BitConverter.GetBytes(Address).Take(6).Reverse().Select(b => b.ToString("X2")));
}

/// <summary>
/// Continuously reports every advertising launch monitor the app supports (one event per advertisement): a Square
/// by its name, a Garmin R10 by its name or by Garmin's manufacturer data. An R10 only advertises while nothing
/// else (a phone) is connected to it.
/// </summary>
public sealed class MonitorScanner : IDisposable
{
    private readonly BluetoothLEAdvertisementWatcher _watcher = new() { ScanningMode = BluetoothLEScanningMode.Active };
    /// <summary>Names heard per address: the R10 can send its name and its manufacturer data in separate packets.</summary>
    private readonly Dictionary<ulong, string> _names = new();
    private readonly Dictionary<ulong, LaunchMonitorType> _types = new();

    public event Action<MonitorAdvertisement>? DeviceSeen;
    public event Action<string>? Stopped;

    public MonitorScanner()
    {
        _watcher.Received += OnReceived;
        _watcher.Stopped += (_, e) => Stopped?.Invoke(e.Error.ToString());
    }

    public bool IsScanning => _watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started;

    /// <summary>False while a stop is still finishing: the watcher can't be started again until it has.</summary>
    public bool CanStart => _watcher.Status is BluetoothLEAdvertisementWatcherStatus.Created
        or BluetoothLEAdvertisementWatcherStatus.Stopped or BluetoothLEAdvertisementWatcherStatus.Aborted;

    public void Start() { if (!IsScanning) _watcher.Start(); }
    public void Stop() { if (IsScanning) _watcher.Stop(); }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        ushort? product = null;
        foreach (var m in args.Advertisement.GetManufacturerDataByCompanyId(MonitorIdentity.GarminCompanyId))
        {
            var bytes = new byte[m.Data.Length];
            using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(m.Data)) reader.ReadBytes(bytes);
            product = MonitorIdentity.GarminProduct(bytes) ?? product;
        }
        string name = args.Advertisement.LocalName;
        ulong address = args.BluetoothAddress;
        lock (_names)
        {
            if (!string.IsNullOrEmpty(name)) _names[address] = name;
            else _names.TryGetValue(address, out name!);
            if (MonitorIdentity.Classify(name, product) is { } found) _types[address] = found;
            if (!_types.TryGetValue(address, out var type)) return;
            DeviceSeen?.Invoke(new MonitorAdvertisement(type, string.IsNullOrEmpty(name) ? MonitorIdentity.GarminR10Name : name,
                address, args.RawSignalStrengthInDBm, DateTime.Now));
        }
    }

    public void Dispose()
    {
        Stop();
        _watcher.Received -= OnReceived;
    }
}
