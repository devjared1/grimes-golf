namespace GrimesGolf.Bluetooth;

/// <summary>The Bluetooth link this platform uses for a Square: the WinRT one.</summary>
internal static class SquareLinks
{
    public static ISquareDeviceLink Create(ulong address, Action<string> log) => new SquareBleLink(new MonitorOptions(), log, address);
}
