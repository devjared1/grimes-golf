using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SquareGspro.Core.GsPro;

/// <summary>TCP client for GSPro Open Connect v1 (default 127.0.0.1:921). Reconnects automatically.</summary>
public sealed class GsProClient
{
    private static readonly JsonSerializerOptions ReadOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly string _host;
    private readonly int _port;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private volatile NetworkStream? _stream;

    public event Action<GsProResponse>? ResponseReceived;
    public event Action<bool>? ConnectionChanged;

    public bool IsConnected => _stream != null;

    public GsProClient(string host, int port, Action<string> log)
    {
        _host = host;
        _port = port;
        _log = log;
    }

    /// <summary>Connect/read loop. Runs until cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        bool loggedWaiting = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var tcp = new TcpClient { NoDelay = true };
                await tcp.ConnectAsync(_host, _port, ct);
                _stream = tcp.GetStream();
                loggedWaiting = false;
                _log($"Connected to GSPro at {_host}:{_port}");
                ConnectionChanged?.Invoke(true);
                await ReadLoopAsync(_stream, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (!loggedWaiting) _log($"Waiting for GSPro Open Connect on {_host}:{_port} ({ex.Message})");
                loggedWaiting = true;
            }
            finally
            {
                if (_stream != null)
                {
                    _stream = null;
                    _log("Disconnected from GSPro");
                    ConnectionChanged?.Invoke(false);
                }
            }

            try { await Task.Delay(2000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReadLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[4096];
        var chars = new char[4096];
        var decoder = Encoding.UTF8.GetDecoder();
        var framer = new JsonObjectFramer();

        while (!ct.IsCancellationRequested)
        {
            int n = await stream.ReadAsync(buf, ct);
            if (n == 0) return; // remote closed
            int c = decoder.GetChars(buf, 0, n, chars, 0);
            foreach (var json in framer.Feed(new string(chars, 0, c)))
            {
                try
                {
                    var resp = JsonSerializer.Deserialize<GsProResponse>(json, ReadOpts);
                    if (resp != null) ResponseReceived?.Invoke(resp);
                }
                catch (JsonException ex) { _log($"Ignoring unparsable GSPro message: {ex.Message}"); }
            }
        }
    }

    /// <summary>Sends a shot / status message. Returns false if GSPro is not connected or the write failed.</summary>
    public async Task<bool> SendAsync(GsProShot shot, CancellationToken ct = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(shot);
        await _sendLock.WaitAsync(ct);
        try
        {
            var s = _stream;
            if (s == null) return false;
            await s.WriteAsync(bytes, ct);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            _log($"GSPro send failed: {ex.Message}");
            return false;
        }
        finally { _sendLock.Release(); }
    }
}
