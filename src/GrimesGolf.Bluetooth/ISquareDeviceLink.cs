using GrimesGolf.Core.Square;

namespace GrimesGolf.Bluetooth;

/// <summary>A Square's Bluetooth link as <see cref="MonitorSession"/> uses it: the commands and events, and reading who it is.</summary>
public interface ISquareDeviceLink : ISquareLink
{
    /// <summary>Reads serial, hardware and firmware version. Call after <see cref="ISquareLink.ConnectAsync"/>.</summary>
    Task<SquareDeviceInfo> ReadDeviceInfoAsync();
}
