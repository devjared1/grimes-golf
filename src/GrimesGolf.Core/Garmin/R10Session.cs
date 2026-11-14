// The connection sequence follows gsp-r10-adapter's direct Bluetooth mode (Copyright (c) 2022 mholow, MIT licence;
// see NOTICE): MultiLink registration, acknowledging every GFDI message, then wake-up, status, tilt and the alert
// subscription. See docs/garmin-r10-protocol.md.
using System.Collections.Concurrent;
using System.Threading.Channels;
using GrimesGolf.Core.Monitors;

namespace GrimesGolf.Core.Garmin;

/// <summary>Where a notification from the R10 came from.</summary>
public enum R10Characteristic { Interface, Measurement, Status, ControlPoint, Battery }

/// <summary>Read from the standard Device Information service. Any of it may be null if the read failed.</summary>
public sealed record R10DeviceInfo(string? Model, string? Serial, string? Firmware);

/// <summary>Transport to an R10: Bluetooth LE on Windows, or the fake R10 in tests and development runs.</summary>
public interface IR10Link
{
    /// <summary>A notification, from any of the subscribed characteristics.</summary>
    event Action<R10Characteristic, byte[]>? Notified;
    event Action? Disconnected;
    /// <summary>Connects (pairing first if need be), finds the services and subscribes to the notifications.</summary>
    Task ConnectAsync(CancellationToken ct);
    Task<R10DeviceInfo> ReadDeviceInfoAsync();
    Task<int?> ReadBatteryAsync();
    /// <summary>Writes one chunk (handle byte first) to the MultiLink write characteristic.</summary>
    Task WriteAsync(byte[] chunk, CancellationToken ct);
    Task DisconnectAsync();
}

/// <summary>
/// One connection to a Garmin Approach R10: registers with its MultiLink service, completes the GFDI handshake,
/// wakes it, subscribes to its alerts, and reports its state and shots. The R10 re-arms itself after every shot.
/// Events are raised on a background thread.
/// </summary>
public sealed class R10Session : ILaunchMonitorSession
{
    /// <summary>How long to wait for the registration, the handshake, and an answer to a request.</summary>
    public TimeSpan RegisterTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan ConfigurationTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>A status request this often checks the link is alive; two unanswered in a row and the session ends.</summary>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(30);

    private readonly Func<IR10Link> _newLink;
    private readonly Action<string> _log, _raw;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<ushort, TaskCompletionSource<IReadOnlyList<R10Event>>> _pending = new();
    private readonly TaskCompletionSource<byte> _registered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _configured = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<uint> _shotIds = new();
    private readonly GfdiReceiver _receiver = new();
    private IR10Link? _link;
    private byte _handle;
    private int _nextRequest;
    private volatile bool _leftHanded, _armed;
    private R10State? _state;
    private DateTime _lastWake = DateTime.MinValue;

    public LaunchMonitorType Type => LaunchMonitorType.GarminR10;
    public MonitorCapabilities Capabilities => MonitorCapabilities.Battery;

    public event Action<MonitorDeviceInfo>? Connected;
    public event Action<MonitorBattery>? Battery;
    public event Action<string>? StateChanged;
    public event Action<MonitorReadiness>? ReadinessChanged;
    public event Action<MonitorShot>? Shot;
    public event Action<string>? Ended;

    /// <param name="raw">
    /// Where the raw traffic goes (a capture file to turn into test fixtures): every GFDI frame both ways, and the
    /// notifications that aren't GFDI. Frames that carry the device's identity are left out.
    /// </param>
    public R10Session(Func<IR10Link> newLink, Action<string> log, Action<string>? raw = null)
    {
        _newLink = newLink;
        _log = log;
        _raw = raw ?? (_ => { });
    }

    public void Stop() => _cts.Cancel();

    /// <summary>The R10 has no club setting; the hand is only used to label its shots.</summary>
    public Task SelectClubAsync(string club, bool leftHanded)
    {
        _leftHanded = leftHanded;
        return Task.CompletedTask;
    }

    private abstract record Input;
    private sealed record Notification(R10Characteristic From, byte[] Data) : Input;
    private sealed record Dropped(string Reason) : Input;

    public async Task RunAsync(string club, bool leftHanded)
    {
        _leftHanded = leftHanded;
        var ct = _cts.Token;
        var inbox = Channel.CreateUnbounded<Input>(new UnboundedChannelOptions { SingleReader = true });
        void OnNotified(R10Characteristic from, byte[] data) => inbox.Writer.TryWrite(new Notification(from, data));
        void OnDisconnected() => inbox.Writer.TryWrite(new Dropped("Link dropped"));

        IR10Link? link = null;
        string reason = "Disconnected";
        try
        {
            link = _newLink();
            link.Notified += OnNotified;
            link.Disconnected += OnDisconnected;
            await link.ConnectAsync(ct);
            _link = link;
            _log("Connected");
            var info = await link.ReadDeviceInfoAsync();
            _log($"Device: {info.Model ?? "?"}, firmware {info.Firmware ?? "?"}");
            var loop = Task.Run(() => EventLoopAsync(inbox.Reader, ct), ct);

            // MultiLink: register for GFDI and be given the handle every write starts with.
            _raw($"-> register {Convert.ToHexString(MultiLink.Register())}");
            await WriteRawAsync(MultiLink.Register(), ct);
            var registered = await Within(_registered.Task, RegisterTimeout, ct);
            if (!registered.Done) throw new IOException("The R10 didn't answer the handshake. Is it paired, and not connected to a phone?");
            _handle = registered.Value;
            _log($"Registered (handle {_handle})");
            await WriteRawAsync(new byte[] { _handle, 0x00 }, ct);   // as gsp-r10-adapter does once registered

            // GFDI: it sends its device information and configuration, which the event loop acknowledges.
            if (!(await Within(_configured.Task, ConfigurationTimeout, ct)).Done) _log("The R10 sent no configuration message; carrying on");
            Connected?.Invoke(new MonitorDeviceInfo(R10Shots.Device, info.Model != null, new MonitorStat[]
            {
                new("Model", info.Model ?? "—"), new("Serial", info.Serial ?? "—"), new("Firmware", info.Firmware ?? "—"),
            }));
            if (await link.ReadBatteryAsync() is { } level) Battery?.Invoke(new MonitorBattery(level, ""));

            // Wake it, see what state it's in and how level it is, then ask for its states, shots and errors.
            var wake = await RequestAsync(R10Proto.WakeUpRequest(), "wake-up", ct);
            if (wake?.OfType<R10WakeResult>().FirstOrDefault() is { } w)
                _log(w.Status switch { 0 => "Woken up", 1 => "Already awake", _ => $"Wake-up failed ({w.Status})" });
            var status = await RequestAsync(R10Proto.StatusRequest(), "status", ct);
            var tilt = await RequestAsync(R10Proto.TiltRequest(), "tilt", ct);
            if (tilt?.OfType<R10TiltResult>().FirstOrDefault() is { } t)
                _log($"Tilt: roll {t.Roll ?? 0:0.0}°, pitch {t.Pitch ?? 0:0.0}°");
            var sub = await RequestAsync(R10Proto.SubscribeRequest(), "alert subscription", ct);
            if (wake == null && status == null && sub == null)
                throw new IOException("The R10 isn't answering. Turn it off and on, and try again.");
            if (sub?.OfType<R10SubscribeResult>().FirstOrDefault() is { Success: false }) _log("The R10 refused the shot alerts");
            _armed = true;
            _log($"Armed ({club}{(leftHanded ? ", LH" : "")}) - hit away");
            if (_state == R10State.Standby) WakeAgain();   // it dozed off (or wouldn't wake) while being set up

            _ = KeepAliveAsync(inbox.Writer, ct);
            reason = await loop;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { reason = "Disconnected"; }
        catch (Exception ex) { reason = $"Connection failed: {ex.Message}"; }
        finally
        {
            _link = null;
            _armed = false;
            _cts.Cancel();
            foreach (var p in _pending.Values) p.TrySetCanceled();
            if (link != null)
            {
                link.Notified -= OnNotified;
                link.Disconnected -= OnDisconnected;
                try { await link.DisconnectAsync(); } catch { /* best effort */ }
            }
            Ended?.Invoke(reason);
        }
    }

    // ---- incoming ----------------------------------------------------------------------------

    private async Task<string> EventLoopAsync(ChannelReader<Input> inbox, CancellationToken ct)
    {
        try
        {
            await foreach (var input in inbox.ReadAllAsync(ct))
            {
                if (input is Dropped d) return d.Reason;
                if (input is not Notification n) continue;
                try { await HandleAsync(n.From, n.Data, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _log($"R10 message not understood: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
        return "Disconnected";
    }

    private async Task HandleAsync(R10Characteristic from, byte[] data, CancellationToken ct)
    {
        switch (from)
        {
            case R10Characteristic.Battery:
                if (data.Length > 0) Battery?.Invoke(new MonitorBattery(data[0], ""));
                return;
            case R10Characteristic.Interface:
                break;
            default:
                // Not understood yet: logged for working out later.
                _raw($"<- {from}: {Convert.ToHexString(data)}");
                return;
        }

        if (!_registered.Task.IsCompleted)
        {
            _raw($"<- interface: {Convert.ToHexString(data)}");
            if (MultiLink.TryParseRegisterResponse(data, out byte status, out byte handle))
            {
                if (status == 0) _registered.TrySetResult(handle);
                else _registered.TrySetException(new IOException($"The R10 refused the registration (status {status})."));
            }
            return;
        }

        foreach (var r in _receiver.Accept(data))
        {
            if (r.Frame is not { } frame)
            {
                _raw($"<- bad frame ({r.Error}): {Convert.ToHexString(r.Raw)}");
                _log($"R10 frame dropped: {r.Error}");
                continue;
            }
            LogFrame("<-", frame.Type, r.Raw);
            await HandleFrameAsync(frame, ct);
        }
    }

    private async Task HandleFrameAsync(GfdiFrame frame, CancellationToken ct)
    {
        switch (frame.Type)
        {
            case Gfdi.Ack:
                if (frame.Acknowledges is { Status: not 0 } nak) _log($"The R10 rejected message {nak.Type} (status {nak.Status})");
                return;
            case Gfdi.ProtobufRequest or Gfdi.ProtobufResponse:
                var (id, proto) = Gfdi.ParseProtobuf(frame.Payload);
                await WriteFrameAsync(Gfdi.BuildProtobufAck(frame.Type, id), ct);
                var events = R10Proto.Decode(proto);
                if (frame.Type == Gfdi.ProtobufResponse && _pending.TryRemove(id, out var waiting)) waiting.TrySetResult(events);
                foreach (var e in events) OnEvent(e);
                return;
            case Gfdi.DeviceInformation:
                if (frame.Payload.Length >= 12)
                    _log($"GFDI protocol {BitConverter.ToUInt16(frame.Payload, 0)}, product {BitConverter.ToUInt16(frame.Payload, 2)}, " +
                         $"software {BitConverter.ToUInt16(frame.Payload, 8)}");
                await WriteFrameAsync(Gfdi.BuildAck(frame.Type, 0), ct);
                return;
            case Gfdi.Configuration:
                await WriteFrameAsync(Gfdi.BuildAck(frame.Type, 0), ct);
                _configured.TrySetResult(true);
                return;
            default:
                // FIT capabilities and anything else: acknowledged, not read.
                await WriteFrameAsync(Gfdi.BuildAck(frame.Type, 0), ct);
                return;
        }
    }

    private void OnEvent(R10Event e)
    {
        switch (e)
        {
            case R10StateEvent s:
                if (s.State == _state) return;
                _state = s.State;
                _log($"Monitor state: {s.State}");
                StateChanged?.Invoke(s.State.ToString());
                ReadinessChanged?.Invoke(Readiness(s.State));
                if (s.State == R10State.Standby && _armed) WakeAgain();
                break;
            case R10ShotEvent shot:
                var m = shot.Metrics;
                // It sends a shot again if it doesn't hear the acknowledgement in time.
                if (m.ShotId is { } sid && !_shotIds.Add(sid)) { _log($"Shot {sid} repeated: ignored"); return; }
                _log(Describe(m));
                Shot?.Invoke(R10Shots.ToMonitorShot(m, _leftHanded));
                break;
            case R10ErrorEvent err:
                string text = err.Code switch
                {
                    R10ErrorCode.PlatformTilted => "Monitor tilted — level it",
                    R10ErrorCode.Overheating => "Monitor too hot — let it cool",
                    R10ErrorCode.RadarSaturation => "Radar overloaded — check the space in front of it",
                    _ => "Monitor error",
                };
                _log($"R10 error: {err.Code} ({err.Severity}){(err.Roll is { } r ? $", roll {r:0.0}° pitch {err.Pitch ?? 0:0.0}°" : "")}");
                if (err.Severity != R10Severity.Warning) ReadinessChanged?.Invoke(new MonitorReadiness(false, text, Problem: true));
                break;
            case R10TiltCalibrationEvent cal:
                _log($"Tilt calibration: status {cal.Status}, result {cal.Result}");
                break;
        }
    }

    /// <summary>It nods off after a while without shots: wake it again (not more than every 10 s).</summary>
    private void WakeAgain()
    {
        if (DateTime.UtcNow - _lastWake < TimeSpan.FromSeconds(10)) return;
        _lastWake = DateTime.UtcNow;
        _log("The R10 is asleep: waking it");
        _ = RequestAsync(R10Proto.WakeUpRequest(), "wake-up", _cts.Token);
    }

    /// <summary>The R10's state for the Ready light: only Waiting is ready for a shot.</summary>
    public static MonitorReadiness Readiness(R10State state) => state switch
    {
        R10State.Waiting => new MonitorReadiness(true, "Ready — hit!"),
        R10State.Recording or R10State.Processing => new MonitorReadiness(false, "Measuring…"),
        R10State.InterferenceTest => new MonitorReadiness(false, "Checking for interference…"),
        R10State.Standby => new MonitorReadiness(false, "Monitor asleep"),
        _ => new MonitorReadiness(false, "Monitor error", Problem: true),
    };

    /// <summary>The shot as the R10 sent it (its own units and signs), for checking against the Garmin Golf app.</summary>
    private static string Describe(R10Metrics m)
    {
        var b = m.Ball;
        var c = m.Club;
        return $"R10 shot {m.ShotId?.ToString() ?? "?"} ({(m.ShotType == 1 ? "normal" : "practice")}): ball {b?.BallSpeed:0.00} m/s, " +
               $"launch {b?.LaunchAngle:0.00}°, direction {b?.LaunchDirection:0.00}°, spin {b?.TotalSpin:0} rpm, axis {b?.SpinAxis:0.00}° (+ = left), " +
               $"spin {b?.SpinSource?.ToString() ?? "?"}; club {c?.HeadSpeed:0.00} m/s, face {c?.Face:0.00}°, path {c?.Path:0.00}°, attack {c?.AttackAngle:0.00}°";
    }

    // ---- outgoing ----------------------------------------------------------------------------

    /// <summary>Sends a protobuf request and waits for its answer (null: none in time).</summary>
    private async Task<IReadOnlyList<R10Event>?> RequestAsync(byte[] proto, string what, CancellationToken ct)
    {
        ushort id = (ushort)Interlocked.Increment(ref _nextRequest);
        var answer = new TaskCompletionSource<IReadOnlyList<R10Event>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        try
        {
            await WriteFrameAsync(Gfdi.BuildProtobuf(Gfdi.ProtobufRequest, id, proto), ct);
            var (done, events) = await Within(answer.Task, RequestTimeout, ct);
            if (!done) _log($"No answer to the {what} request");
            return events;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        finally { _pending.TryRemove(id, out _); }
    }

    /// <summary>
    /// A status request now and then: the R10 needs no keep-alive of its own (Bluetooth supervises the link), but a
    /// link that is up and silent shows here.
    /// </summary>
    private async Task KeepAliveAsync(ChannelWriter<Input> inbox, CancellationToken ct)
    {
        int missed = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(KeepAliveInterval, ct);
                missed = await RequestAsync(R10Proto.StatusRequest(), "status", ct) == null ? missed + 1 : 0;
                if (missed >= 2) { inbox.TryWrite(new Dropped("The R10 stopped answering")); return; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log($"Keep-alive failed: {ex.Message}");
            inbox.TryWrite(new Dropped("Link dropped"));
        }
    }

    /// <summary>One whole frame, its chunks together (another frame's can't come between them).</summary>
    private async Task WriteFrameAsync(byte[] frame, CancellationToken ct)
    {
        var link = _link ?? throw new InvalidOperationException("Not connected.");
        LogFrame("->", BitConverter.ToUInt16(frame, 2), frame);
        await _writeLock.WaitAsync(ct);
        try { foreach (var chunk in Gfdi.Chunks(_handle, frame)) await link.WriteAsync(chunk, ct); }
        finally { _writeLock.Release(); }
    }

    private async Task WriteRawAsync(byte[] data, CancellationToken ct)
    {
        var link = _link ?? throw new InvalidOperationException("Not connected.");
        await _writeLock.WaitAsync(ct);
        try { await link.WriteAsync(data, ct); }
        finally { _writeLock.Release(); }
    }

    private void LogFrame(string direction, ushort type, byte[] raw)
    {
        ushort shown = type == Gfdi.Ack && raw.Length >= 6 ? BitConverter.ToUInt16(raw, 4) : type;   // an ack: what it acknowledges
        _raw(Gfdi.CarriesIdentity(shown)
            ? $"{direction} {type} ({raw.Length} bytes, not logged: it carries the device's identity)"
            : $"{direction} {type}: {Convert.ToHexString(raw)}");
    }

    /// <summary>The task's result, if it finishes within <paramref name="limit"/>.</summary>
    private static async Task<(bool Done, T? Value)> Within<T>(Task<T> task, TimeSpan limit, CancellationToken ct)
    {
        var done = await Task.WhenAny(task, Task.Delay(limit, ct));
        ct.ThrowIfCancellationRequested();
        return done == task ? (true, await task) : (false, default);
    }
}
