using System.Net;
using System.Net.Sockets;
using System.Text;

namespace GrimesGolf.Core.OpenConnect;

/// <summary>
/// Stands in for GSPro's Open Connect server, so a launch monitor's GSPro connector app (the Garmin R10 adapter, the
/// MLM2PRO Bluetooth app and others) can send its shots here. One connector at a time: a new connection replaces the
/// old one, as a connector that lost its link reconnects while the old socket may not have noticed yet.
/// Events are raised on a background thread; the UI marshals them.
/// </summary>
public sealed class OpenConnectServer : IDisposable
{
    private readonly IPAddress _address;
    private readonly int _port;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private Connection? _client;
    private OpenConnectResponse? _player;

    /// <summary>A connector connected (its address).</summary>
    public event Action<string>? Connected;
    /// <summary>The connector went away (why).</summary>
    public event Action<string>? Disconnected;
    /// <summary>The connector named its device (DeviceID), the first time and whenever it changes.</summary>
    public event Action<string>? DeviceNamed;
    /// <summary>The monitor's ready state: ready for the shot, and whether it sees a ball (null = not said).</summary>
    public event Action<bool, bool?>? ReadyChanged;
    public event Action<OpenConnectShot>? ShotReceived;
    public event Action<string>? Log;

    /// <param name="anyAddress">Accept connectors on other computers too (by default only this PC's, like GSPro).</param>
    public OpenConnectServer(int port = OpenConnectProtocol.DefaultPort, bool anyAddress = false)
    {
        _port = port;
        _address = anyAddress ? IPAddress.Any : IPAddress.Loopback;
    }

    /// <summary>The port listened on (useful with port 0 in tests).</summary>
    public int Port => ((IPEndPoint?)_listener?.LocalEndpoint)?.Port ?? _port;

    public bool HasClient { get { lock (_gate) return _client != null; } }

    /// <summary>Starts listening; throws <see cref="SocketException"/> if the port is taken (GSPro itself running, say).</summary>
    public void Start()
    {
        _listener = new TcpListener(_address, _port);
        _listener.Start();
        Log?.Invoke($"Open Connect: listening on {_address}:{Port}");
        _ = AcceptLoop(_listener, _cts.Token);
    }

    /// <summary>
    /// The player up, for the connector to set the monitor's club and hand (code 201). Kept and sent again to each
    /// connector that connects.
    /// </summary>
    public void SendPlayer(bool leftHanded, string clubCode, double distanceYards)
    {
        var info = OpenConnectProtocol.PlayerInfo(leftHanded, clubCode, distanceYards);
        Connection? c;
        lock (_gate) { _player = info; c = _client; }
        c?.Send(info);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch (SocketException) { }
        Connection? c;
        lock (_gate) { c = _client; _client = null; }
        c?.Close();
    }

    private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await listener.AcceptTcpClientAsync(ct); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { return; }
            catch (SocketException ex) { Log?.Invoke($"Open Connect: accept failed: {ex.Message}"); continue; }

            tcp.NoDelay = true;
            var c = new Connection(tcp);
            Connection? old;
            OpenConnectResponse? player;
            lock (_gate) { old = _client; _client = c; player = _player; }
            if (old != null)
            {
                Log?.Invoke("Open Connect: a new connector connection replaces the old one");
                old.Close();
            }
            Connected?.Invoke(c.Name);
            if (player != null) c.Send(player);
            _ = Serve(c, ct);
        }
    }

    private async Task Serve(Connection c, CancellationToken ct)
    {
        var framer = new JsonObjectFramer();
        framer.Overflowed += () => Log?.Invoke("Open Connect: dropped an over-long message");
        var buffer = new byte[8192];
        string reason = "Connector disconnected";
        string? device = null;
        (bool, bool?)? ready = null;
        try
        {
            var stream = c.Tcp.GetStream();
            while (!ct.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(buffer, ct);
                if (n == 0) break;
                foreach (string json in framer.Push(buffer.AsSpan(0, n)))
                {
                    var m = OpenConnectProtocol.Parse(json);
                    if (m == null)
                    {
                        Log?.Invoke($"Open Connect: not a message: {Shorten(json)}");
                        c.Send(OpenConnectProtocol.Failed("Message could not be read"));
                        continue;
                    }
                    if (!IsCurrent(c)) return;   // replaced: its last messages don't count
                    if (!string.IsNullOrWhiteSpace(m.DeviceID) && m.DeviceID != device)
                    {
                        device = m.DeviceID;
                        DeviceNamed?.Invoke(device);
                    }
                    if (m.ShotDataOptions?.LaunchMonitorIsReady is { } isReady)
                    {
                        var now = (isReady, m.ShotDataOptions.LaunchMonitorBallDetected);
                        if (ready != now) ReadyChanged?.Invoke(now.isReady, now.LaunchMonitorBallDetected);
                        ready = now;
                    }
                    // Heartbeats and ready signals get no reply: a connector can read a 200 as "shot taken".
                    if (!m.IsShot) continue;
                    var (shot, error) = OpenConnectProtocol.ToShot(m);
                    if (shot == null)
                    {
                        Log?.Invoke($"Open Connect: shot {m.ShotNumber} not used: {error}");
                        c.Send(OpenConnectProtocol.Failed(error!));
                        continue;
                    }
                    c.Send(OpenConnectProtocol.Received());
                    foreach (var w in shot.Warnings) Log?.Invoke($"Open Connect: {w}");
                    ShotReceived?.Invoke(shot);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            if (ex is not (OperationCanceledException or ObjectDisposedException)) reason = $"Connector connection lost ({ex.Message})";
        }
        bool wasCurrent;
        lock (_gate)
        {
            wasCurrent = _client == c;
            if (wasCurrent) _client = null;
        }
        c.Close();
        if (wasCurrent && !ct.IsCancellationRequested) Disconnected?.Invoke(reason);
    }

    private bool IsCurrent(Connection c) { lock (_gate) return _client == c; }

    private static string Shorten(string s) => s.Length <= 200 ? s : s[..200] + "…";

    private sealed class Connection
    {
        private readonly object _write = new();
        public TcpClient Tcp { get; }
        public string Name { get; }

        public Connection(TcpClient tcp)
        {
            Tcp = tcp;
            Name = tcp.Client.RemoteEndPoint?.ToString() ?? "connector";
        }

        public void Send(OpenConnectResponse r)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(OpenConnectProtocol.Serialize(r));
            try { lock (_write) Tcp.GetStream().Write(bytes); }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException) { }
        }

        public void Close()
        {
            try { Tcp.Close(); } catch (SocketException) { }
        }
    }
}
