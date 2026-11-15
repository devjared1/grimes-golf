using System.IO;
using GrimesGolf.Core.Garmin;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace GrimesGolf.Bluetooth;

/// <summary>
/// Windows Bluetooth LE transport to a Garmin Approach R10. Unlike the Square, the R10 needs to be paired (bonded):
/// if Windows hasn't paired it yet this tries to, which works while the R10 is in pairing mode; otherwise pair it once
/// in Windows Settings > Bluetooth &amp; devices. The R10 only accepts one connection, so the Garmin Golf app must not
/// be connected to it.
/// </summary>
public sealed class R10BleLink : IR10Link
{
    private readonly ulong _address;
    private readonly Action<string> _log;
    private BluetoothLEDevice? _device;
    private readonly List<GattDeviceService> _services = new();
    private readonly List<(GattCharacteristic Characteristic, R10Characteristic From)> _subscribed = new();
    private GattCharacteristic? _write;
    private GattWriteOption _writeOption = GattWriteOption.WriteWithResponse;

    public event Action<R10Characteristic, byte[]>? Notified;
    public event Action? Disconnected;

    public R10BleLink(ulong address, Action<string> log)
    {
        _address = address;
        _log = log;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        await Task.Delay(250, ct);   // let the radio settle after scanning
        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(_address)
                  ?? throw new IOException("Windows could not open the Bluetooth device.");
        await EnsurePairedAsync(_device, ct);
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;

        var multiLink = await ServiceAsync(R10Uuids.MultiLinkService, required: true, ct);
        // gsp-r10-adapter's channel pair, or channel 0 if this R10 doesn't offer it.
        var notify = await CharacteristicAsync(multiLink!, R10Uuids.Notify) ?? await CharacteristicAsync(multiLink!, R10Uuids.NotifyChannel0);
        _write = await CharacteristicAsync(multiLink!, R10Uuids.Write) ?? await CharacteristicAsync(multiLink!, R10Uuids.WriteChannel0);
        if (notify == null || _write == null) throw new IOException("The R10's MultiLink characteristics weren't found.");
        if (notify.Uuid != R10Uuids.Notify) _log("Using MultiLink channel 0 (2810/2820)");
        if (!_write.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write)) _writeOption = GattWriteOption.WriteWithoutResponse;
        await Task.Delay(250, ct);

        // The measurement service's notifications aren't understood yet: subscribed (as gsp-r10-adapter does) and logged.
        if (await ServiceAsync(R10Uuids.MeasurementService, required: false, ct) is { } measurement)
        {
            await SubscribeAsync(await CharacteristicAsync(measurement, R10Uuids.Measurement), R10Characteristic.Measurement, required: false);
            await SubscribeAsync(await CharacteristicAsync(measurement, R10Uuids.ControlPoint), R10Characteristic.ControlPoint, required: false);
            await SubscribeAsync(await CharacteristicAsync(measurement, R10Uuids.Status), R10Characteristic.Status, required: false);
        }
        if (await ServiceAsync(R10Uuids.BatteryService, required: false, ct) is { } battery)
            await SubscribeAsync(await CharacteristicAsync(battery, R10Uuids.BatteryLevel), R10Characteristic.Battery, required: false);
        await SubscribeAsync(notify, R10Characteristic.Interface, required: true);
    }

    /// <summary>Pairs the R10 if Windows hasn't already. It has no screen or keys, so pairing just needs accepting.</summary>
    private async Task EnsurePairedAsync(BluetoothLEDevice device, CancellationToken ct)
    {
        var pairing = device.DeviceInformation.Pairing;
        if (pairing.IsPaired) return;
        if (!pairing.CanPair)
            throw new IOException("Windows can't pair with the R10 right now. Put it in pairing mode, or pair it in Windows Settings > Bluetooth & devices.");
        _log("Pairing with the R10...");
        var custom = pairing.Custom;
        void OnRequested(DeviceInformationCustomPairing s, DevicePairingRequestedEventArgs a) => a.Accept();
        custom.PairingRequested += OnRequested;
        try
        {
            var result = await custom.PairAsync(DevicePairingKinds.ConfirmOnly | DevicePairingKinds.ConfirmPinMatch).AsTask(ct);
            if (result.Status is not (DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired))
                throw new IOException($"Windows couldn't pair with the R10 ({result.Status}). Put it in pairing mode, or pair it in Windows Settings > Bluetooth & devices.");
            _log("Paired");
        }
        finally { custom.PairingRequested -= OnRequested; }
    }

    public async Task<R10DeviceInfo> ReadDeviceInfoAsync()
    {
        var dis = await ServiceAsync(R10Uuids.DeviceInfoService, required: false, CancellationToken.None);
        if (dis == null) return new R10DeviceInfo(null, null, null);
        return new R10DeviceInfo(await ReadTextAsync(dis, R10Uuids.ModelNumber), await ReadTextAsync(dis, R10Uuids.SerialNumber),
                                 await ReadTextAsync(dis, R10Uuids.FirmwareRevision));
    }

    public async Task<int?> ReadBatteryAsync()
    {
        var service = await ServiceAsync(R10Uuids.BatteryService, required: false, CancellationToken.None);
        var bytes = service == null ? null : await ReadAsync(service, R10Uuids.BatteryLevel);
        return bytes is { Length: > 0 } ? bytes[0] : null;
    }

    public async Task WriteAsync(byte[] chunk, CancellationToken ct)
    {
        var c = _write ?? throw new InvalidOperationException("Not connected.");
        using var writer = new DataWriter();
        writer.WriteBytes(chunk);
        var result = await c.WriteValueWithResultAsync(writer.DetachBuffer(), _writeOption).AsTask(ct);
        if (result.Status != GattCommunicationStatus.Success) throw new IOException($"BLE write failed ({result.Status}).");
    }

    public async Task DisconnectAsync()
    {
        foreach (var (c, _) in _subscribed)
        {
            c.ValueChanged -= OnValueChanged;
            try { await c.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None); }
            catch { /* it may already be gone */ }
        }
        _subscribed.Clear();
        if (_device != null) _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        foreach (var s in _services) s.Dispose();
        _services.Clear();
        _device?.Dispose();
        _device = null;
        _write = null;
    }

    private async Task<GattDeviceService?> ServiceAsync(Guid uuid, bool required, CancellationToken ct)
    {
        // Service discovery right after connecting can come back empty; try a few times.
        for (int attempt = 1; attempt <= 4; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var r = await _device!.GetGattServicesForUuidAsync(uuid, BluetoothCacheMode.Uncached);
            if (r.Status == GattCommunicationStatus.Success && r.Services.Count > 0)
            {
                _services.Add(r.Services[0]);
                return r.Services[0];
            }
            if (!required) return null;
            _log($"GATT discovery attempt {attempt} found nothing ({r.Status}); retrying");
            await Task.Delay(250, ct);
        }
        throw new IOException("The R10's Garmin service wasn't found. Is it paired with this PC?");
    }

    private static async Task<GattCharacteristic?> CharacteristicAsync(GattDeviceService service, Guid uuid)
    {
        var r = await service.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached);
        return r.Status == GattCommunicationStatus.Success && r.Characteristics.Count > 0 ? r.Characteristics[0] : null;
    }

    private async Task SubscribeAsync(GattCharacteristic? c, R10Characteristic from, bool required)
    {
        if (c == null)
        {
            if (required) throw new IOException($"The R10's {from} characteristic wasn't found.");
            return;
        }
        c.ValueChanged += OnValueChanged;
        _subscribed.Add((c, from));
        var status = await c.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (status == GattCommunicationStatus.Success) return;
        if (required) throw new IOException($"Could not subscribe to the R10's notifications ({status}).");
        _log($"Could not subscribe to {from} ({status})");
    }

    private async Task<byte[]?> ReadAsync(GattDeviceService service, Guid uuid)
    {
        try
        {
            if (await CharacteristicAsync(service, uuid) is not { } c) return null;
            var r = await c.ReadValueAsync(BluetoothCacheMode.Uncached);
            if (r.Status != GattCommunicationStatus.Success) { _log($"Read {uuid} failed ({r.Status})"); return null; }
            var reader = DataReader.FromBuffer(r.Value);
            var bytes = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            _log($"Read {uuid} failed: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> ReadTextAsync(GattDeviceService service, Guid uuid) =>
        await ReadAsync(service, uuid) is { } b ? System.Text.Encoding.UTF8.GetString(b).TrimEnd('\0', ' ', '\r', '\n') : null;

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var reader = DataReader.FromBuffer(args.CharacteristicValue);
        var bytes = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(bytes);
        var from = _subscribed.FirstOrDefault(s => s.Characteristic.Uuid == sender.Uuid).From;
        Notified?.Invoke(from, bytes);
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected) Disconnected?.Invoke();
    }
}
