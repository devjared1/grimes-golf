namespace GrimesGolf.Core.Monitors;

/// <summary>One advertisement heard from a launch monitor the app supports.</summary>
public sealed record MonitorAdvertisement(LaunchMonitorType Type, string Name, ulong Address, short Rssi, DateTime SeenAt)
{
    public string AddressText => string.Join(":", BitConverter.GetBytes(Address).Take(6).Reverse().Select(b => b.ToString("X2")));
}

/// <summary>
/// Listens for launch monitors advertising over Bluetooth (one event per advertisement). Each platform with
/// Bluetooth support has its own: Windows' is <c>MonitorScanner</c> in GrimesGolf.Bluetooth.
/// </summary>
public interface IMonitorScanner : IDisposable
{
    event Action<MonitorAdvertisement>? DeviceSeen;
    /// <summary>The scan stopped, with why ("Success" when it was asked to).</summary>
    event Action<string>? Stopped;

    bool IsScanning { get; }
    /// <summary>False while a stop is still finishing: the scan can't be started again until it has.</summary>
    bool CanStart { get; }

    void Start();
    void Stop();
}
