using System.Collections.Concurrent;
using System.IO;
using System.Text;
using GrimesGolf.Core.Garmin;

namespace GrimesGolf.Bluetooth;

/// <summary>
/// The iPad's Bluetooth transport to a Garmin Approach R10, through CoreBluetooth (see <see cref="CoreBluetooth"/>), doing
/// what <c>R10BleLink</c> does on Windows: find the MultiLink service, subscribe to the notifications (the interface
/// channel last), and write the handshake and the requests. The R10 has to be bonded: iOS pairs it itself, asking the
/// user to confirm, the first time the app uses a characteristic that needs it, so the first connection waits for
/// that. The R10 only accepts one connection, so the Garmin Golf app must not be connected to it.
/// </summary>
public sealed class AppleR10Link : IR10Link
{
    private const int ConnectSeconds = 45, SubscribeSeconds = 90, WriteSeconds = 10, ReadSeconds = 5;

    private readonly ulong _address;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<byte[]>> _reads = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _subscribes = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _writes = new();
    private readonly ConcurrentDictionary<int, R10Characteristic> _notifying = new();
    private readonly HashSet<Guid> _found = new();
    private TaskCompletionSource<string>? _connected;
    private Guid _write;
    private int _token;

    public event Action<R10Characteristic, byte[]>? Notified;
    public event Action? Disconnected;

    public AppleR10Link(ulong address, Action<string> log)
    {
        _address = address;
        _log = log;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        _connected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        CoreBluetooth.Event += OnEvent;
        _log("Connecting to the R10...");
        CoreBluetooth.ConnectAll(_address, ConnectSeconds, "No Garmin R10 found. Is it on, and not connected to the Garmin Golf app (or another phone)?");
        using var cancelled = ct.Register(() => _connected.TrySetCanceled(ct));
        var listing = await _connected.Task;

        // What the R10 has, one line each ("S <service>", "C <characteristic> <properties>"): kept, and logged for a first look at a real one.
        foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ');
            if (parts.Length >= 2 && parts[0] == "C" && Guid.TryParse(parts[1], out var id)) _found.Add(id);
            _log("R10 GATT: " + line);
        }

        // gsp-r10-adapter's channel pair, or channel 0 if this R10 doesn't offer it.
        var notify = _found.Contains(R10Uuids.Notify) ? R10Uuids.Notify : _found.Contains(R10Uuids.NotifyChannel0) ? R10Uuids.NotifyChannel0 : (Guid?)null;
        Guid? write = _found.Contains(R10Uuids.Write) ? R10Uuids.Write : _found.Contains(R10Uuids.WriteChannel0) ? R10Uuids.WriteChannel0 : null;
        if (notify == null || write == null) throw new IOException("The R10's MultiLink characteristics weren't found.");
        if (notify != R10Uuids.Notify) _log("Using MultiLink channel 0 (2810/2820)");
        _write = write.Value;

        // The measurement service's notifications aren't understood yet: subscribed (as gsp-r10-adapter does) and logged.
        await SubscribeAsync(R10Uuids.Measurement, R10Characteristic.Measurement, required: false, ct);
        await SubscribeAsync(R10Uuids.ControlPoint, R10Characteristic.ControlPoint, required: false, ct);
        await SubscribeAsync(R10Uuids.Status, R10Characteristic.Status, required: false, ct);
        await SubscribeAsync(R10Uuids.BatteryLevel, R10Characteristic.Battery, required: false, ct);
        await SubscribeAsync(notify.Value, R10Characteristic.Interface, required: true, ct);
    }

    public async Task<R10DeviceInfo> ReadDeviceInfoAsync() =>
        new(await ReadTextAsync(R10Uuids.ModelNumber), await ReadTextAsync(R10Uuids.SerialNumber), await ReadTextAsync(R10Uuids.FirmwareRevision));

    public async Task<int?> ReadBatteryAsync() => await ReadAsync(R10Uuids.BatteryLevel) is { Length: > 0 } bytes ? bytes[0] : null;

    public async Task WriteAsync(byte[] chunk, CancellationToken ct)
    {
        int token = Interlocked.Increment(ref _token);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _writes[token] = done;
        try
        {
            CoreBluetooth.WriteTo(_write, chunk, token);
            using var cancelled = ct.Register(() => done.TrySetCanceled(ct));
            if (await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(WriteSeconds), ct)) != done.Task) throw new IOException("BLE write timed out.");
            await done.Task;
        }
        finally { _writes.TryRemove(token, out _); }
    }

    public Task DisconnectAsync()
    {
        CoreBluetooth.Event -= OnEvent;
        CoreBluetooth.Disconnect();
        Cancel();
        return Task.CompletedTask;
    }

    private async Task SubscribeAsync(Guid characteristic, R10Characteristic from, bool required, CancellationToken ct)
    {
        if (!_found.Contains(characteristic))
        {
            if (required) throw new IOException($"The R10's {from} characteristic wasn't found.");
            return;
        }
        int token = Interlocked.Increment(ref _token);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _subscribes[token] = done;
        _notifying[token] = from;
        try
        {
            CoreBluetooth.Subscribe(characteristic, token);
            using var cancelled = ct.Register(() => done.TrySetCanceled(ct));
            // The first one can take a while: iOS asks the user to pair the R10 and answers when they have.
            if (await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(SubscribeSeconds), ct)) != done.Task)
                throw new IOException($"Timed out subscribing to the R10's {from} notifications. If iOS asked to pair the R10, was it accepted?");
            await done.Task;
        }
        catch (IOException ex) when (!required)
        {
            _notifying.TryRemove(token, out _);
            _log($"Could not subscribe to {from} ({ex.Message})");
        }
        catch (IOException ex)
        {
            throw new IOException($"Could not subscribe to the R10's notifications ({ex.Message}).", ex);
        }
        finally { _subscribes.TryRemove(token, out _); }
    }

    private async Task<byte[]?> ReadAsync(Guid characteristic)
    {
        if (!_found.Contains(characteristic)) return null;
        int token = Interlocked.Increment(ref _token);
        var done = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _reads[token] = done;
        try
        {
            CoreBluetooth.Read(characteristic, token);
            if (await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(ReadSeconds))) != done.Task) { _log($"Read {characteristic} timed out"); return null; }
            return await done.Task;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            _log($"Read {characteristic} failed: {ex.Message}");
            return null;
        }
        finally { _reads.TryRemove(token, out _); }
    }

    private async Task<string?> ReadTextAsync(Guid characteristic) =>
        await ReadAsync(characteristic) is { } b ? Encoding.UTF8.GetString(b).TrimEnd('\0', ' ', '\r', '\n') : null;

    /// <summary>Everything waiting for an answer gives up (the link has gone, or is being closed).</summary>
    private void Cancel()
    {
        _connected?.TrySetCanceled();
        foreach (var read in _reads.Values) read.TrySetCanceled();
        foreach (var subscribe in _subscribes.Values) subscribe.TrySetCanceled();
        foreach (var write in _writes.Values) write.TrySetCanceled();
    }

    private static string Text(byte[] data) => Encoding.UTF8.GetString(data);

    private void OnEvent(int kind, int token, ulong address, byte[] data)
    {
        switch (kind)
        {
            case CoreBluetooth.Connected when address == _address: _connected?.TrySetResult(Text(data)); break;
            case CoreBluetooth.ConnectFailed: _connected?.TrySetException(new IOException(Text(data))); break;
            case CoreBluetooth.Notification when address == _address:
                if (_notifying.TryGetValue(token, out var from)) Notified?.Invoke(from, data);
                break;
            case CoreBluetooth.Subscribed: if (_subscribes.TryGetValue(token, out var subscribed)) subscribed.TrySetResult(); break;
            case CoreBluetooth.SubscribeFailed: if (_subscribes.TryGetValue(token, out var refused)) refused.TrySetException(new IOException(Text(data))); break;
            case CoreBluetooth.WriteDone: if (_writes.TryGetValue(token, out var written)) written.TrySetResult(); break;
            case CoreBluetooth.WriteFailed: if (_writes.TryGetValue(token, out var failed)) failed.TrySetException(new IOException("BLE write failed: " + Text(data))); break;
            case CoreBluetooth.ReadDone: if (_reads.TryGetValue(token, out var ok)) ok.TrySetResult(data); break;
            case CoreBluetooth.ReadFailed: if (_reads.TryGetValue(token, out var bad)) bad.TrySetException(new IOException(Text(data))); break;
            case CoreBluetooth.Log: _log("Bluetooth: " + Text(data)); break;
            case CoreBluetooth.Disconnected when address == _address:
                Cancel();
                Disconnected?.Invoke();
                break;
        }
    }
}
