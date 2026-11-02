using System.Threading.Channels;
using SquareGspro.Core.GsPro;
using SquareGspro.Core.Square;

namespace SquareGspro.Core;

/// <summary>
/// Runs one Square session: connect, arm the monitor, and turn each shot into a GSPro Open Connect message.
/// Club and handedness follow GSPro's code-201 player updates.
/// </summary>
public sealed class ShotBridge
{
    private readonly ConnectorOptions _o;
    private readonly GsProClient _gs;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _stateLock = new();

    private string _club;
    private string _hand;
    private int _shotNumber;

    // Per-session state (only touched by the session's event loop, except _link/_cmd which GSPro callbacks read).
    private ISquareLink? _link;
    private SquareCommandBuilder? _cmd;
    private CancellationToken _sessionToken;
    private bool _armed;

    public ShotBridge(ConnectorOptions options, GsProClient gsPro, Action<string> log)
    {
        _o = options;
        _gs = gsPro;
        _log = log;
        _club = options.DefaultClub;
        _hand = options.DefaultHanded;
        _gs.ResponseReceived += OnGsProResponse;
    }

    public int ShotCount => _shotNumber;

    public async Task RunSessionAsync(ISquareLink link, CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<SquareEvent>(new UnboundedChannelOptions { SingleReader = true });
        var cmd = new SquareCommandBuilder();
        byte[]? last = null;

        // The Omni sends every notification twice, byte-identical; drop the repeat.
        void OnData(byte[] data)
        {
            if (last != null && data.AsSpan().SequenceEqual(last)) return;
            last = data;
            channel.Writer.TryWrite(SquarePacketParser.Parse(data));
        }
        void OnDisconnected() => channel.Writer.TryWrite(new DisconnectedEvent());

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        link.DataReceived += OnData;
        link.Disconnected += OnDisconnected;
        try
        {
            await link.ConnectAsync(cts.Token);
            lock (_stateLock) { _link = link; _cmd = cmd; _sessionToken = cts.Token; }
            _log("Square connected");

            await WriteAsync(cmd.Query(), cts.Token);
            await WriteAsync(cmd.Heartbeat(), cts.Token);
            await ArmAsync(cts.Token);

            _ = HeartbeatLoopAsync(cmd, channel.Writer, cts.Token);
            await EventLoopAsync(channel, cmd, cts.Token);
        }
        finally
        {
            lock (_stateLock) { _link = null; _cmd = null; _armed = false; }
            link.DataReceived -= OnData;
            link.Disconnected -= OnDisconnected;
            cts.Cancel();
            try { await link.DisconnectAsync(); } catch { /* best effort */ }
            _log("Square session ended");
        }
    }

    private async Task EventLoopAsync(Channel<SquareEvent> channel, SquareCommandBuilder cmd, CancellationToken ct)
    {
        (int Id, BallMetricsEvent Ball)? pending = null;
        int pendingSeq = 0;
        bool? lastReady = null, lastDetected = null;
        SquareDeviceState? lastState = null;
        int lastBattery = -1;

        async Task FinalizeAsync(ClubMetricsEvent? club)
        {
            var p = pending!.Value;
            pending = null;
            int n = Interlocked.Increment(ref _shotNumber);
            var shot = ShotMapper.Map(_o, n, p.Ball, club);
            bool ok = await _gs.SendAsync(shot, ct);
            _log($"Shot {n}: {shot.BallData.Speed:0.0} mph, VLA {shot.BallData.VLA:0.0}, HLA {shot.BallData.HLA:0.0}, " +
                 $"spin {shot.BallData.TotalSpin:0} @ axis {shot.BallData.SpinAxis:0.0}" +
                 (shot.ShotDataOptions.ContainsClubData ? $", club {shot.ClubData.Speed:0.0} mph" : ", no club data") +
                 (ok ? "" : "  [NOT SENT: GSPro not connected]"));
        }

        await foreach (var ev in channel.Reader.ReadAllAsync(ct))
        {
            switch (ev)
            {
                case DisconnectedEvent:
                    _log("Square link dropped");
                    return;

                case SensorEvent s:
                    if (s.BallReady != lastReady || s.BallDetected != lastDetected)
                    {
                        lastReady = s.BallReady; lastDetected = s.BallDetected;
                        await _gs.SendAsync(ShotMapper.Status(_o, _shotNumber, s.BallReady, s.BallDetected), ct);
                    }
                    break;

                case BallMetricsEvent b:
                    if (pending != null) await FinalizeAsync(null); // previous shot never got club data
                    int id = ++pendingSeq;
                    pending = (id, b);
                    if (!_o.SendClubData)
                    {
                        await FinalizeAsync(null);
                        break;
                    }
                    await WriteAsync(cmd.RequestClubMetrics(), ct);
                    _ = Task.Delay(_o.ClubMetricsTimeoutMs, ct).ContinueWith(
                        _ => channel.Writer.TryWrite(new ClubTimeoutEvent(id)),
                        TaskContinuationOptions.OnlyOnRanToCompletion);
                    break;

                case ClubMetricsEvent c:
                    // The device replays the previous shot's club data on reconnect; only accept it as an answer to our request.
                    if (pending == null) { _log("Ignoring stale club metrics"); break; }
                    await FinalizeAsync(c.HasAnyData ? c : null);
                    break;

                case ClubTimeoutEvent t:
                    if (pending?.Id == t.PendingId) await FinalizeAsync(null);
                    break;

                case HeartbeatAckEvent h:
                    if (h.State != lastState) { lastState = h.State; _log($"Monitor state: {h.State}"); }
                    break;

                case BatteryEvent bat:
                    if (bat.Level != lastBattery) { lastBattery = bat.Level; _log($"Battery {bat.Level}%"); }
                    break;
            }
        }
    }

    private async Task HeartbeatLoopAsync(SquareCommandBuilder cmd, ChannelWriter<SquareEvent> events, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(_o.HeartbeatSeconds), ct);
                await WriteAsync(cmd.Heartbeat(), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log($"Heartbeat failed: {ex.Message}");
            events.TryWrite(new DisconnectedEvent());
        }
    }

    private async Task ArmAsync(CancellationToken ct)
    {
        SquareCommandBuilder? cmd;
        string club, hand;
        lock (_stateLock) { cmd = _cmd; club = _club; hand = _hand; }
        if (cmd == null) return;

        await SendClubSelectAsync(cmd, club, hand, ct);
        await WriteAsync(cmd.DetectBall(on: true, _o.AdvancedSpin), ct);
        _armed = true;
        _log($"Armed ({club}, {hand})");
    }

    private async Task SendClubSelectAsync(SquareCommandBuilder cmd, string club, string hand, CancellationToken ct)
    {
        if (!SquareClubMap.TryGet(club, out var sq))
        {
            _log($"GSPro club '{club}' has no Square mapping; leaving the monitor's club unchanged");
            return;
        }
        await WriteAsync(cmd.ClubSelect(sq, leftHanded: hand.Equals("LH", StringComparison.OrdinalIgnoreCase)), ct);
    }

    private void OnGsProResponse(GsProResponse r)
    {
        if (r.Code != 201 || r.Player == null) return;

        SquareCommandBuilder? cmd;
        CancellationToken ct;
        string club, hand;
        lock (_stateLock)
        {
            if (!string.IsNullOrWhiteSpace(r.Player.Club)) _club = r.Player.Club!;
            if (!string.IsNullOrWhiteSpace(r.Player.Handed)) _hand = r.Player.Handed!;
            club = _club; hand = _hand; cmd = _cmd; ct = _sessionToken;
        }
        _log($"GSPro player: {club}, {hand}");

        if (cmd != null && _armed)
        {
            _ = Task.Run(async () =>
            {
                try { await SendClubSelectAsync(cmd, club, hand, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _log($"Club change failed: {ex.Message}"); }
            });
        }
    }

    private async Task WriteAsync(byte[] data, CancellationToken ct)
    {
        ISquareLink? link;
        lock (_stateLock) { link = _link; }
        if (link == null) return;

        await _writeLock.WaitAsync(ct);
        try { await link.WriteAsync(data, ct); }
        finally { _writeLock.Release(); }
    }
}
