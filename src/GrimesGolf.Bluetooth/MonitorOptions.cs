namespace GrimesGolf.Bluetooth;

/// <summary>How the Bluetooth link finds a Square launch monitor.</summary>
public sealed class MonitorOptions
{
    /// <summary>BLE advertised-name prefix to scan for. The Omni advertises as "SquareGolf(XXXX)".</summary>
    public string DeviceNamePrefix { get; set; } = "SquareGolf";

    /// <summary>Optional exact advertised name (e.g. "SquareGolf(A1B2)") when several monitors are nearby.</summary>
    public string? DeviceName { get; set; }

    public int ScanTimeoutSeconds { get; set; } = 30;
}
