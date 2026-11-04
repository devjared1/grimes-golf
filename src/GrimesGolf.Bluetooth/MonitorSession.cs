using System.Threading.Channels;
using GrimesGolf.Core;
using GrimesGolf.Core.Square;

namespace GrimesGolf.Bluetooth;

/// <summary>
/// One connection to a chosen monitor: reads device info, arms it, and reports status and shots.
/// Events are raised on a background thread; the UI marshals them.
/// </summary>
public sealed class MonitorSession
{
    private readonly ulong _address;
    private readonly Action<string> _log;
    private readonly SquareCommandBuilder _cmd = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private SquareBleLink? _link;
    private volatile bool _detecting = true;

    public event Action<SquareDeviceInfo>? InfoRead;
    public event Action<BatteryEvent>? Battery;
    public event Action<SquareDeviceState>? StateChanged;
    public event Action<SensorEvent>? Sensor;
    public event Action<ulong>? Clock;
    public event Action<BallMetricsEvent, ClubMetricsEvent?>? Shot;
    public event Action<string>? Ended;

    public MonitorSession(ulong address, Action<string> log)
    {
        _address = address;
        _log = log;
    }

    public async Task RunAsync(string club, bool leftHanded)
    {
        var ct = _cts.Token;
        var channel = Channel.CreateUnbounded<SquareEvent>(new UnboundedChannelOptions { SingleReader = true });
        byte[]? last = null;
        string? lastSensor = null, lastClub = null;
        void OnData(byte[] data)
        {
            // Notifications can arrive twice, byte-identical; drop the repeat.
            if (last != null && data.AsSpan().SequenceEqual(last)) return;
            last = data;
            // The sensor bytes aren't fully understood yet: log each change, ignoring the sequence byte.
            if (data.Length >= 3 && data[0] == 0x11 && data[1] == 0x01)
            {
                string sensor = Convert.ToHexString(data, 3, data.Length - 3);
                if (sensor != lastSensor) _log($"Sensor raw: {Convert.ToHexString(data)}{DescribeSensor(data)}");
                lastSensor = sensor;
            }
            // The heartbeat reply reports the club the monitor is really using: sel 4-7 real club, 0-3 swing stick.
            if (data.Length >= 9 && data[0] == 0x11 && data[1] == 0x03)
            {
                string club = $"sel={data[5]} number={data[6]} hand={data[7]} sensor={data[8]}";
                if (club != lastClub) _log($"Monitor club: {club}");
                lastClub = club;
            }
            channel.Writer.TryWrite(SquarePacketParser.Parse(data));
        }
        void OnDisconnected() => channel.Writer.TryWrite(new DisconnectedEvent());

        var link = new SquareBleLink(new MonitorOptions(), _log, _address);
        link.DataReceived += OnData;
        link.Disconnected += OnDisconnected;
        string reason = "Disconnected";
        try
        {
            await link.ConnectAsync(ct);
            _link = link;
            _log("Connected");
            var info = await link.ReadDeviceInfoAsync();
            _log($"Device: hardware {info.HardwareVersion ?? "?"}, firmware {info.FirmwareRaw ?? "?"}");
            InfoRead?.Invoke(info);

            // The order the official app uses on hardware 1.5.
            await WriteAsync(_cmd.Query(), ct);
            await WriteAsync(_cmd.SessionInfo(), ct);
            await WriteAsync(_cmd.Heartbeat(), ct);
            await SelectClubAsync(club, leftHanded);
            await WriteAsync(_cmd.Heartbeat(), ct);
            await WriteAsync(_cmd.DetectBall(on: true, advancedSpin: true), ct);
            _log($"Armed ({club}{(leftHanded ? ", LH" : "")}) - hit away");

            _ = HeartbeatLoopAsync(channel.Writer, ct);
            reason = await EventLoopAsync(channel.Reader, channel.Writer, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { reason = "Disconnected"; }
        catch (Exception ex) { reason = $"Connection failed: {ex.Message}"; }
        finally
        {
            _link = null;
            link.DataReceived -= OnData;
            link.Disconnected -= OnDisconnected;
            _cts.Cancel();
            try { await link.DisconnectAsync(); } catch { /* best effort */ }
            Ended?.Invoke(reason);
        }
    }

    public void Stop() => _cts.Cancel();

    /// <summary>Ready flag, ball flag and position for the log. The position fields are decoded, but their units aren't known.</summary>
    private static string DescribeSensor(byte[] d) => d.Length < 17 ? "" :
        $"  ready={d[3]} ball={d[4]} X={BitConverter.ToInt32(d, 5)} Y={BitConverter.ToInt32(d, 9)} Z={BitConverter.ToInt32(d, 13)}";

    public async Task SelectClubAsync(string club, bool leftHanded)
    {
        if (!SquareClubMap.TryGet(club, out var sq)) { _log($"No Square code for club {club}"); return; }
        await WriteAsync(_cmd.ClubSelect(sq, leftHanded), _cts.Token);
    }

    private async Task<string> EventLoopAsync(ChannelReader<SquareEvent> reader, ChannelWriter<SquareEvent> events, CancellationToken ct)
    {
        BallMetricsEvent? pending = null;
        int pendingSeq = 0;
        SquareDeviceState? lastState = null;
        int notDetecting = 0;

        await foreach (var ev in reader.ReadAllAsync(ct))
        {
            switch (ev)
            {
                case DisconnectedEvent:
                    return "Link dropped";
                case SensorEvent s:
                    Sensor?.Invoke(s);
                    break;
                case HeartbeatAckEvent h:
                    if (h.State != lastState)
                    {
                        lastState = h.State;
                        _log($"Monitor state: {h.State}");
                        StateChanged?.Invoke(h.State);
                    }
                    // Armed, but the monitor has dropped out of detecting for two heartbeats running: arm it again.
                    notDetecting = _detecting && h.State < SquareDeviceState.Detecting ? notDetecting + 1 : 0;
                    if (notDetecting > 1)
                    {
                        notDetecting = 0;
                        await WriteAsync(_cmd.DetectBall(on: true, advancedSpin: true), ct);
                    }
                    break;
                case UnknownEvent u:
                    _log($"Unknown packet: {Convert.ToHexString(u.Data)}");
                    break;
                case BatteryEvent b:
                    Battery?.Invoke(b);
                    break;
                case ClockEvent c:
                    Clock?.Invoke(c.Seconds);
                    break;
                case BallMetricsEvent b:
                    if (pending != null) Shot?.Invoke(pending, null);
                    pending = b;
                    int id = ++pendingSeq;
                    await WriteAsync(_cmd.RequestClubMetrics(), ct);
                    _ = Task.Delay(800, ct).ContinueWith(_ => events.TryWrite(new ClubTimeoutEvent(id)),
                        TaskContinuationOptions.OnlyOnRanToCompletion);
                    _detecting = false;
                    _ = RearmAfterShotAsync(ct);
                    break;
                case ClubMetricsEvent c:
                    // The device replays the previous shot's club data on reconnect; only accept it as an answer.
                    if (pending == null) break;
                    Shot?.Invoke(pending, c.HasAnyData ? c : null);
                    pending = null;
                    break;
                case ClubTimeoutEvent t:
                    if (pending != null && t.PendingId == pendingSeq) { Shot?.Invoke(pending, null); pending = null; }
                    break;
            }
        }
        return "Disconnected";
    }

    /// <summary>A shot ends detection; arm again for the next one.</summary>
    private async Task RearmAfterShotAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(1500, ct);
            await WriteAsync(_cmd.DetectBall(on: true, advancedSpin: true), ct);
            _detecting = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log($"Re-arming failed: {ex.Message}"); }
    }

    private async Task HeartbeatLoopAsync(ChannelWriter<SquareEvent> events, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                await WriteAsync(_cmd.Heartbeat(), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log($"Heartbeat failed: {ex.Message}");
            events.TryWrite(new DisconnectedEvent());
        }
    }

    private async Task WriteAsync(byte[] data, CancellationToken ct)
    {
        var link = _link;
        if (link == null) return;
        await _writeLock.WaitAsync(ct);
        try { await link.WriteAsync(data, ct); }
        finally { _writeLock.Release(); }
    }
}
