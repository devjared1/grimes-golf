using System.Text;
using System.Threading.Channels;
using GrimesGolf.Core.Monitors;

namespace GrimesGolf.Core.Garmin;

/// <summary>
/// A pretend R10 on the far side of <see cref="IR10Link"/>, for tests and <c>--fake-monitor=r10</c>: it plays the
/// device's part of the real protocol (registration, the GFDI handshake, answers to requests, state changes and shots,
/// all COBS-framed and cut into 20-byte notifications), so the whole R10 path runs without the hardware. Its timing
/// and sequence follow the protocol notes, not a capture from a real device.
/// </summary>
public sealed class FakeR10Device : IR10Link, IFakeMonitor
{
    public const byte Handle = 3;

    private Channel<(R10Characteristic From, byte[] Data, int DelayMs)> _outbox = Channel.CreateUnbounded<(R10Characteristic, byte[], int)>();
    private GfdiReceiver _receiver = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _pump;
    private bool _registered;
    private ushort _nextRequest = 0x100;
    private uint _nextShot = 1;

    public event Action<R10Characteristic, byte[]>? Notified;
    public event Action? Disconnected;

    /// <summary>The state it's in. It starts asleep, as an R10 just switched on does.</summary>
    public R10State State { get; private set; } = R10State.Standby;
    public int BatteryLevel { get; set; } = 87;
    /// <summary>How long between the notifications of one shot (0 in tests).</summary>
    public int StepMs { get; set; } = 150;
    /// <summary>Every frame the host sent, decoded (to check the host's side in tests).</summary>
    public List<GfdiFrame> Received { get; } = new();
    /// <summary>Requests the host made, by kind ("wake", "status", ...).</summary>
    public List<string> Requests { get; } = new();
    /// <summary>Stay silent instead of answering the registration (a test of the timeout).</summary>
    public bool IgnoreRegistration { get; set; }

    /// <summary>A fresh connection: it registers again, as a real one does after a drop.</summary>
    public Task ConnectAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            _pump?.Cancel();
            var pump = new CancellationTokenSource();
            _pump = pump;
            _outbox = Channel.CreateUnbounded<(R10Characteristic, byte[], int)>();
            _receiver = new GfdiReceiver();
            _registered = false;
            var outbox = _outbox;
            _ = Task.Run(() => PumpAsync(outbox, pump.Token));
        }
        return Task.Delay(StepMs, ct);
    }

    public Task<R10DeviceInfo> ReadDeviceInfoAsync() => Task.FromResult(new R10DeviceInfo("Approach R10", "FAKE-R10", "4.40"));
    public Task<int?> ReadBatteryAsync() => Task.FromResult<int?>(BatteryLevel);

    public Task DisconnectAsync()
    {
        _pump?.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>The link drops, as if it went out of range or was switched off.</summary>
    public void Drop()
    {
        _pump?.Cancel();
        Disconnected?.Invoke();
    }

    public Task WriteAsync(byte[] chunk, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_registered)
            {
                if (chunk.Length == 13 && chunk[0] == 0 && chunk[1] == 0 && !IgnoreRegistration)
                {
                    _registered = true;
                    Send(R10Characteristic.Interface, MultiLink.RegisterResponse(Handle));
                    SendFrame(Gfdi.BuildFrame(Gfdi.DeviceInformation, DeviceInformation()));
                    var caps = new byte[14];
                    caps[0] = 13;
                    caps[1 + 30 / 8] = 1 << 30 % 8;
                    SendFrame(Gfdi.BuildFrame(Gfdi.Configuration, caps));
                }
                return Task.CompletedTask;
            }
            foreach (var r in _receiver.Accept(chunk))
                if (r.Frame is { } f) OnFrame(f);
        }
        return Task.CompletedTask;
    }

    /// <summary>HIT SHOT in a development run: the shot as an R10 sends it, with the states around it.</summary>
    public void Hit(RangeShot shot)
    {
        lock (_gate)
        {
            SetState(R10State.Recording);
            SetState(R10State.Processing);
            SendRequest(R10Proto.ShotNotification(R10Shots.FromRangeShot(shot, _nextShot++)));
            SetState(R10State.Waiting);
        }
    }

    /// <summary>Sends an alert as the R10 does (a tilted monitor, say).</summary>
    public void SendAlert(byte[] wrapper)
    {
        lock (_gate) SendRequest(wrapper);
    }

    /// <summary>Goes to sleep, as an R10 does after a while without shots.</summary>
    public void Sleep()
    {
        lock (_gate) SetState(R10State.Standby);
    }

    private void OnFrame(GfdiFrame f)
    {
        Received.Add(f);
        if (f.Type != Gfdi.ProtobufRequest) return;
        var (id, proto) = Gfdi.ParseProtobuf(f.Payload);
        SendFrame(Gfdi.BuildAck(Gfdi.ProtobufRequest, 0));
        string kind = R10Proto.RequestKind(proto) ?? "unknown";
        Requests.Add(kind);
        switch (kind)
        {
            case "wake":
                bool asleep = State == R10State.Standby;
                Respond(id, R10Proto.WakeUpResponse(asleep ? 0 : 1));
                if (asleep) { SetState(R10State.InterferenceTest); SetState(R10State.Waiting); }
                break;
            case "status": Respond(id, R10Proto.StatusResponse(State)); break;
            case "tilt": Respond(id, R10Proto.TiltResponse(0.4f, -0.2f)); break;
            case "subscribe": Respond(id, R10Proto.SubscribeResponse(true)); break;
        }
    }

    private void SetState(R10State s)
    {
        State = s;
        SendRequest(R10Proto.StateNotification(s));
    }

    private void Respond(ushort id, byte[] proto) => SendFrame(Gfdi.BuildProtobuf(Gfdi.ProtobufResponse, id, proto));

    /// <summary>The device's own 5043s (alerts) count up from their own request ids.</summary>
    private void SendRequest(byte[] proto) => SendFrame(Gfdi.BuildProtobuf(Gfdi.ProtobufRequest, _nextRequest++, proto));

    private void SendFrame(byte[] frame)
    {
        foreach (var chunk in Gfdi.Chunks(Handle, frame)) Send(R10Characteristic.Interface, chunk);
    }

    private void Send(R10Characteristic from, byte[] data) => _outbox.Writer.TryWrite((from, data, StepMs / 10));

    private async Task PumpAsync(Channel<(R10Characteristic From, byte[] Data, int DelayMs)> outbox, CancellationToken ct)
    {
        try
        {
            await foreach (var (from, data, delay) in outbox.Reader.ReadAllAsync(ct))
            {
                if (delay > 0) await Task.Delay(delay, ct);
                Notified?.Invoke(from, data);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>A 5024 payload: protocol 113, product 3622 (R10), a made-up unit id, software 4.40, names.</summary>
    private static byte[] DeviceInformation()
    {
        var b = new List<byte>();
        b.AddRange(BitConverter.GetBytes((ushort)113));
        b.AddRange(BitConverter.GetBytes(MonitorIdentity.GarminR10Product));
        b.AddRange(BitConverter.GetBytes(0x00C0FFEEu));
        b.AddRange(BitConverter.GetBytes((ushort)440));
        b.AddRange(BitConverter.GetBytes((ushort)512));
        foreach (var s in new[] { "Approach R10", "Approach R10", "Approach R10" })
        {
            var t = Encoding.UTF8.GetBytes(s);
            b.Add((byte)t.Length);
            b.AddRange(t);
        }
        return b.ToArray();
    }
}
