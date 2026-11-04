using System.IO;
using SquareGspro.Core;
using SquareGspro.Core.Square;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace SquareGspro.Bluetooth;

/// <summary>
/// Windows BLE transport using the WinRT Bluetooth LE APIs.
/// The Omni does not pair or bond: do NOT pair it in Windows Bluetooth settings; connect as a plain GATT client.
/// </summary>
public sealed class SquareBleLink : ISquareLink
{
    private readonly ConnectorOptions _o;
    private readonly Action<string> _log;
    private readonly ulong? _address;

    private BluetoothLEDevice? _device;
    private GattDeviceService? _service;
    private GattCharacteristic? _cmd;
    private GattCharacteristic? _evt;

    public event Action<byte[]>? DataReceived;
    public event Action? Disconnected;

    /// <param name="address">Connect to this Bluetooth address directly instead of scanning by name.</param>
    public SquareBleLink(ConnectorOptions options, Action<string> log, ulong? address = null)
    {
        _o = options;
        _log = log;
        _address = address;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        ulong address = _address ?? await ScanAsync(ct);

        await Task.Delay(250, ct); // let the radio settle after scanning
        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
                  ?? throw new IOException("Windows could not open the Bluetooth device.");
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;

        // Service discovery right after connect can return nothing; retry a few times.
        GattDeviceServicesResult? services = null;
        for (int attempt = 1; attempt <= 4; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(250, ct);
            services = await _device.GetGattServicesForUuidAsync(SquareUuids.Service, BluetoothCacheMode.Uncached);
            if (services.Status == GattCommunicationStatus.Success && services.Services.Count > 0) break;
            _log($"GATT discovery attempt {attempt} found nothing ({services.Status}); retrying");
        }
        _service = services is { Status: GattCommunicationStatus.Success } && services.Services.Count > 0
            ? services.Services[0]
            : throw new IOException("Square GATT service not found.");

        _cmd = await GetCharacteristicAsync(SquareUuids.Cmd);
        _evt = await GetCharacteristicAsync(SquareUuids.Evt);

        await Task.Delay(250, ct);
        _evt.ValueChanged += OnValueChanged;
        var status = await _evt.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (status != GattCommunicationStatus.Success)
            throw new IOException($"Could not subscribe to Square notifications ({status}).");
    }

    public async Task WriteAsync(byte[] command, CancellationToken ct)
    {
        var cmd = _cmd ?? throw new InvalidOperationException("Not connected.");
        using var writer = new DataWriter();
        writer.WriteBytes(command);
        var result = await cmd.WriteValueWithResultAsync(writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse);
        if (result.Status != GattCommunicationStatus.Success)
            throw new IOException($"BLE write failed ({result.Status}).");
    }

    public Task DisconnectAsync()
    {
        if (_evt != null) _evt.ValueChanged -= OnValueChanged;
        if (_device != null) _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        _service?.Dispose();
        _device?.Dispose();
        _service = null; _device = null; _cmd = null; _evt = null;
        return Task.CompletedTask;
    }

    /// <summary>Reads serial, hardware and firmware version. Call after <see cref="ConnectAsync"/>.</summary>
    public async Task<SquareDeviceInfo> ReadDeviceInfoAsync()
    {
        if (_service == null) throw new InvalidOperationException("Not connected.");
        return new SquareDeviceInfo(
            SquareDeviceInfo.DecodeText(await TryReadAsync(SquareUuids.DeviceId)),
            SquareDeviceInfo.DecodeText(await TryReadAsync(SquareUuids.HardwareVersion)),
            SquareDeviceInfo.DecodeText(await TryReadAsync(SquareUuids.FirmwareVersion)));
    }

    private async Task<byte[]?> TryReadAsync(Guid uuid)
    {
        try
        {
            var c = await GetCharacteristicAsync(uuid);
            var r = await c.ReadValueAsync(BluetoothCacheMode.Uncached);
            if (r.Status != GattCommunicationStatus.Success) { _log($"Read {uuid} failed ({r.Status})"); return null; }
            var reader = DataReader.FromBuffer(r.Value);
            var bytes = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.COMException)
        {
            _log($"Read {uuid} failed: {ex.Message}");
            return null;
        }
    }

    private async Task<GattCharacteristic> GetCharacteristicAsync(Guid uuid)
    {
        var r = await _service!.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached);
        if (r.Status != GattCommunicationStatus.Success || r.Characteristics.Count == 0)
            throw new IOException($"Characteristic {uuid} not found ({r.Status}).");
        return r.Characteristics[0];
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var reader = DataReader.FromBuffer(args.CharacteristicValue);
        var bytes = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(bytes);
        DataReceived?.Invoke(bytes);
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected) Disconnected?.Invoke();
    }

    private async Task<ulong> ScanAsync(CancellationToken ct)
    {
        var found = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, args) =>
        {
            string name = args.Advertisement.LocalName;
            if (string.IsNullOrEmpty(name)) return;
            bool match = _o.DeviceName != null
                ? name.Equals(_o.DeviceName, StringComparison.OrdinalIgnoreCase)
                : name.StartsWith(_o.DeviceNamePrefix, StringComparison.OrdinalIgnoreCase);
            if (match)
            {
                _log($"Found {name}");
                found.TrySetResult(args.BluetoothAddress);
            }
        };

        _log("Scanning for Square launch monitor...");
        watcher.Start();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_o.ScanTimeoutSeconds));
            using var reg = timeout.Token.Register(() => found.TrySetCanceled());
            try { return await found.Task; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("No Square launch monitor found. Is it powered on and out of range of the phone app?");
            }
        }
        finally { watcher.Stop(); }
    }
}
