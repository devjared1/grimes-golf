namespace GrimesGolf.Bluetooth;

/// <summary>The Bluetooth link this platform uses for a Square: CoreBluetooth's.</summary>
internal static class SquareLinks
{
    public static ISquareDeviceLink Create(ulong address, Action<string> log) => new AppleSquareLink(address, log);
}
