using Windows.Devices.Bluetooth.Advertisement;

namespace GrimesGolf.Bluetooth;

public sealed record SquareAdvertisement(string Name, ulong Address, short Rssi, DateTime SeenAt)
{
    public string AddressText => string.Join(":", BitConverter.GetBytes(Address).Take(6).Reverse().Select(b => b.ToString("X2")));
}

/// <summary>Continuously reports every advertising Square launch monitor (one event per advertisement).</summary>
public sealed class SquareBleScanner : IDisposable
{
    private readonly string _namePrefix;
    private readonly BluetoothLEAdvertisementWatcher _watcher = new() { ScanningMode = BluetoothLEScanningMode.Active };

    public event Action<SquareAdvertisement>? DeviceSeen;
    public event Action<string>? Stopped;

    public SquareBleScanner(string namePrefix = "SquareGolf")
    {
        _namePrefix = namePrefix;
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
        string name = args.Advertisement.LocalName;
        if (string.IsNullOrEmpty(name) || !name.StartsWith(_namePrefix, StringComparison.OrdinalIgnoreCase)) return;
        DeviceSeen?.Invoke(new SquareAdvertisement(name, args.BluetoothAddress, args.RawSignalStrengthInDBm, DateTime.Now));
    }

    public void Dispose()
    {
        Stop();
        _watcher.Received -= OnReceived;
    }
}
