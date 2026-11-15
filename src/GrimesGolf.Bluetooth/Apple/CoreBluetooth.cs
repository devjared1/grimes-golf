using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GrimesGolf.Bluetooth;

/// <summary>
/// The native CoreBluetooth layer (Native/gg_ble.m, built into the app by tools/ios-native.sh), called through
/// P/Invoke to the app itself ("__Internal": the library is linked into it, and the game's project file says so with
/// <c>DirectPInvoke</c>). Its callbacks arrive on a Bluetooth thread, and are passed on as events.
/// One monitor at a time, so there's only one set of callbacks.
/// </summary>
internal static unsafe class CoreBluetooth
{
    public const int ScanStopped = 1, Connected = 2, ConnectFailed = 3, Disconnected = 4, Notification = 5, ReadDone = 6, ReadFailed = 7;

    /// <summary>A monitor was heard: its address (made up from its identifier), name and signal strength.</summary>
    public static event Action<ulong, string, int>? Advert;
    /// <summary>Something happened: the kind (see above), the read token, the address, and the bytes (or text) with it.</summary>
    public static event Action<int, int, ulong, byte[]>? Event;

    [DllImport("__Internal")]
    private static extern void gg_ble_set_callbacks(delegate* unmanaged[Cdecl]<ulong, byte*, int, void> advert,
        delegate* unmanaged[Cdecl]<int, int, ulong, byte*, int, void> onEvent);

    [DllImport("__Internal")] private static extern void gg_ble_start_scan();
    [DllImport("__Internal")] private static extern void gg_ble_stop_scan();

    [DllImport("__Internal")]
    private static extern void gg_ble_connect(ulong address, [MarshalAs(UnmanagedType.LPUTF8Str)] string service,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string command, [MarshalAs(UnmanagedType.LPUTF8Str)] string notify, int timeoutSeconds);

    [DllImport("__Internal")] private static extern void gg_ble_disconnect();
    [DllImport("__Internal")] private static extern int gg_ble_write(byte* data, int length);

    [DllImport("__Internal")]
    private static extern void gg_ble_read([MarshalAs(UnmanagedType.LPUTF8Str)] string characteristic, int token);

    private static bool _ready;

    private static void EnsureReady()
    {
        if (_ready) return;
        lock (typeof(CoreBluetooth))
        {
            if (_ready) return;
            gg_ble_set_callbacks(&OnAdvert, &OnEvent);
            _ready = true;
        }
    }

    public static void StartScan() { EnsureReady(); gg_ble_start_scan(); }
    public static void StopScan() { EnsureReady(); gg_ble_stop_scan(); }

    public static void Connect(ulong address, Guid service, Guid command, Guid notify, int timeoutSeconds)
    {
        EnsureReady();
        gg_ble_connect(address, service.ToString(), command.ToString(), notify.ToString(), timeoutSeconds);
    }

    public static void Disconnect() { EnsureReady(); gg_ble_disconnect(); }

    public static bool Write(byte[] data)
    {
        EnsureReady();
        fixed (byte* p = data) return gg_ble_write(p, data.Length) != 0;
    }

    public static void Read(Guid characteristic, int token)
    {
        EnsureReady();
        gg_ble_read(characteristic.ToString(), token);
    }

    // An exception must never leave a callback: it would take the app down with it.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnAdvert(ulong address, byte* name, int rssi)
    {
        try { Advert?.Invoke(address, Marshal.PtrToStringUTF8((IntPtr)name) ?? "", rssi); }
        catch { /* the listener's problem, not Bluetooth's */ }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnEvent(int kind, int token, ulong address, byte* data, int length)
    {
        try { Event?.Invoke(kind, token, address, data == null || length <= 0 ? Array.Empty<byte>() : new ReadOnlySpan<byte>(data, length).ToArray()); }
        catch { /* as above */ }
    }
}
