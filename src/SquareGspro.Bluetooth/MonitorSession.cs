using System.Threading.Channels;
using SquareGspro.Core;
using SquareGspro.Core.Square;

namespace SquareGspro.Bluetooth;

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
        void OnData(byte[] data)
        {
            // Notifications can arrive twice, byte-identical; drop the repeat.
            if (last != null && data.AsSpan().SequenceEqual(last)) return;
            last = data;
            channel.Writer.TryWrite(SquarePacketParser.Parse(data));
        }
        void OnDisconnected() => channel.Writer.TryWrite(new DisconnectedEvent());

        var link = new SquareBleLink(new ConnectorOptions(), _log, _address);
        link.DataReceived += OnData;
        link.Disconnected += OnDisconnected;
        string reason = "Disconnected";
        try
        {
            await link.ConnectAsync(ct);
            _link = link;
            _log("Connected");
            InfoRead?.Invoke(await link.ReadDeviceInfoAsync());

            await WriteAsync(_cmd.Query(), ct);
            await WriteAsync(_cmd.Heartbeat(), ct);
            await SelectClubAsync(club, leftHanded);
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

        await foreach (var ev in reader.ReadAllAsync(ct))
        {
            switch (ev)
            {
                case DisconnectedEvent:
                    return "Link dropped";
                case SensorEvent s:
                    Sensor?.Invoke(s);
                    break;
                case HeartbeatAckEvent h when h.State != lastState:
                    lastState = h.State;
                    StateChanged?.Invoke(h.State);
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
