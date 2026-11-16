using System.Collections.Concurrent;
using System.IO;
using System.Text;
using GrimesGolf.Core.Square;

namespace GrimesGolf.Bluetooth;

/// <summary>
/// The iPad's Bluetooth transport for a Square, through CoreBluetooth (see <see cref="CoreBluetooth"/>). Like the
/// Windows one it's a plain GATT client: the Omni doesn't pair or bond.
/// </summary>
public sealed class AppleSquareLink : ISquareDeviceLink
{
    private const int ConnectSeconds = 30;

    private readonly ulong _address;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<byte[]>> _reads = new();
    private TaskCompletionSource? _connected;
    private int _token;

    public event Action<byte[]>? DataReceived;
    public event Action? Disconnected;

    public AppleSquareLink(ulong address, Action<string> log)
    {
        _address = address;
        _log = log;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        _connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CoreBluetooth.Event += OnEvent;
        _log("Connecting to the Square...");
        CoreBluetooth.Connect(_address, SquareUuids.Service, SquareUuids.Cmd, SquareUuids.Evt, ConnectSeconds);
        using var cancelled = ct.Register(() => _connected.TrySetCanceled(ct));
        await _connected.Task;
    }

    public Task WriteAsync(byte[] command, CancellationToken ct) =>
        CoreBluetooth.Write(command) ? Task.CompletedTask : throw new IOException("BLE write failed (not connected).");

    public Task DisconnectAsync()
    {
        CoreBluetooth.Event -= OnEvent;
        CoreBluetooth.Disconnect();
        foreach (var read in _reads.Values) read.TrySetCanceled();
        return Task.CompletedTask;
    }

    public async Task<SquareDeviceInfo> ReadDeviceInfoAsync() => new(
        SquareDeviceInfo.DecodeText(await TryReadAsync(SquareUuids.DeviceId)),
        SquareDeviceInfo.DecodeText(await TryReadAsync(SquareUuids.HardwareVersion)),
        SquareDeviceInfo.DecodeText(await TryReadAsync(SquareUuids.FirmwareVersion)));

    private async Task<byte[]?> TryReadAsync(Guid characteristic)
    {
        int token = Interlocked.Increment(ref _token);
        var done = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _reads[token] = done;
        try
        {
            CoreBluetooth.Read(characteristic, token);
            var finished = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            if (finished != done.Task) { _log($"Read {characteristic} timed out"); return null; }
            return await done.Task;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            _log($"Read {characteristic} failed: {ex.Message}");
            return null;
        }
        finally { _reads.TryRemove(token, out _); }
    }

    private void OnEvent(int kind, int token, ulong address, byte[] data)
    {
        switch (kind)
        {
            case CoreBluetooth.ReadDone: if (_reads.TryGetValue(token, out var ok)) ok.TrySetResult(data); break;
            case CoreBluetooth.ReadFailed: if (_reads.TryGetValue(token, out var bad)) bad.TrySetException(new IOException(Encoding.UTF8.GetString(data))); break;
            case CoreBluetooth.Connected when address == _address: _connected?.TrySetResult(); break;
            case CoreBluetooth.ConnectFailed: _connected?.TrySetException(new IOException(Encoding.UTF8.GetString(data))); break;
            case CoreBluetooth.Notification when address == _address: DataReceived?.Invoke(data); break;
            case CoreBluetooth.Log: _log("Bluetooth: " + Encoding.UTF8.GetString(data)); break;
            case CoreBluetooth.Disconnected when address == _address: Disconnected?.Invoke(); break;
        }
    }
}
