using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace GrimesGolf.Online.Transport;

public enum RelayRole { Host, Join }

/// <summary>Timings for <see cref="RelayClient"/> (shorter in the tests).</summary>
public sealed record RelayClientOptions
{
    public static readonly RelayClientOptions Default = new();
    /// <summary>A ping goes to the relay this often; it answers, so a silent connection is a dead one.</summary>
    public TimeSpan PingEvery { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Nothing heard for this long: the connection is given up on and made again.</summary>
    public TimeSpan DeadAfter { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>Waits between reconnection attempts: doubling from the first up to the last.</summary>
    public TimeSpan FirstRetry { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxRetry { get; init; } = TimeSpan.FromSeconds(15);
    public int MaxFrameBytes { get; init; } = 256 * 1024;
}

/// <summary>
/// The connection to the relay, over a WebSocket (<c>wss://</c>). One loop reads, one sends from a bounded queue,
/// and a timer pings; all of it off the game's thread, so loading a course (which blocks the game) doesn't drop
/// the connection. A dropped connection is made again with backoff, as the same peer: the token (a secret kept
/// in the settings) is what the relay knows this PC by, and a host asks for its own code back.
/// </summary>
public sealed class RelayClient : ILobbyLink
{
    private readonly Func<CancellationToken, Task<WebSocket>> _connect;
    private readonly RelayRole _role;
    private readonly string _token;
    private readonly RelayClientOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private Channel<string>? _out;
    private Task? _loop;
    private bool _opened;

    public RelayClient(Func<CancellationToken, Task<WebSocket>> connect, RelayRole role, string? code, string token, RelayClientOptions? options = null)
    {
        _connect = connect;
        _role = role;
        Code = code?.Trim().ToUpperInvariant();
        _token = token;
        _options = options ?? RelayClientOptions.Default;
    }

    /// <summary>Connects with a <see cref="ClientWebSocket"/> to <paramref name="relay"/> (e.g. <c>wss://relay.example/ws</c>).</summary>
    public static Func<CancellationToken, Task<WebSocket>> WebSocketTo(Uri relay) => async ct =>
    {
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        try { await ws.ConnectAsync(relay, ct); }
        catch { ws.Dispose(); throw; }
        return ws;
    };

    public event Action<LinkEvent>? Event;
    public string? Code { get; private set; }
    public int? Peer { get; private set; }
    public bool Connected => _out != null;

    public void Start() => _loop ??= Task.Run(() => RunAsync(_stop.Token));

    public void Send(string data, int? to = null)
    {
        var json = RelayWire.Message("send", ("to", _role == RelayRole.Host ? to : null), ("data", data));
        _out?.Writer.TryWrite(json);
    }

    public void Dispose()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* stopping anyway */ }
    }

    private void Raise(LinkEvent e)
    {
        try { Event?.Invoke(e); }
        catch { /* a handler's problem mustn't kill the connection */ }
    }

    private async Task RunAsync(CancellationToken stop)
    {
        var wait = _options.FirstRetry;
        while (!stop.IsCancellationRequested)
        {
            string? fatal = null, fatalMessage = null, lost = null;
            try
            {
                using var ws = await _connect(stop);
                (fatal, fatalMessage, lost) = await SessionAsync(ws, stop);
                if (_opened) wait = _options.FirstRetry;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception e) { lost = e.Message; }
            finally { _out = null; }

            if (stop.IsCancellationRequested) break;
            if (fatal != null) { Raise(new LinkEvent.Closed(fatal, fatalMessage ?? fatal)); return; }
            Raise(new LinkEvent.Lost(lost ?? "Connection lost"));
            try { await Task.Delay(wait, stop); } catch (OperationCanceledException) { break; }
            wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, _options.MaxRetry.Ticks));
        }
        Raise(new LinkEvent.Closed("left", "Left the lobby."));
    }

    /// <summary>One connection: hello, then messages until it ends. Returns why (a fatal error code, or what was lost).</summary>
    private async Task<(string? Fatal, string? Message, string? Lost)> SessionAsync(WebSocket ws, CancellationToken stop)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var outbox = Channel.CreateBounded<string>(new BoundedChannelOptions(1024) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
        long lastHeard = Environment.TickCount64;
        string hello = _role == RelayRole.Host
            ? RelayWire.Message("create", ("v", RelayWire.Protocol), ("code", Code), ("token", _token))
            : RelayWire.Message("join", ("v", RelayWire.Protocol), ("code", Code), ("token", _token));
        await ws.SendAsync(Encoding.UTF8.GetBytes(hello), WebSocketMessageType.Text, true, cts.Token);

        var sending = Task.Run(async () =>
        {
            try
            {
                await foreach (var m in outbox.Reader.ReadAllAsync(cts.Token))
                    await ws.SendAsync(Encoding.UTF8.GetBytes(m), WebSocketMessageType.Text, true, cts.Token);
            }
            catch (Exception) { cts.Cancel(); }
        });
        var pinging = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(_options.PingEvery, cts.Token);
                    if (Environment.TickCount64 - Interlocked.Read(ref lastHeard) > _options.DeadAfter.TotalMilliseconds) { ws.Abort(); cts.Cancel(); return; }
                    outbox.Writer.TryWrite(RelayWire.Message("ping"));
                }
            }
            catch (OperationCanceledException) { }
        });

        var buffer = new byte[16 * 1024];
        using var frame = new MemoryStream();
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                frame.SetLength(0);
                WebSocketReceiveResult r;
                do
                {
                    // WaitAsync: give up on a receive the socket itself doesn't cancel (a dead connection).
                    r = await ws.ReceiveAsync(buffer, cts.Token).WaitAsync(cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) return (null, null, "The relay closed the connection");
                    frame.Write(buffer, 0, r.Count);
                    if (frame.Length > _options.MaxFrameBytes) return (null, null, "Message too big");
                } while (!r.EndOfMessage);
                Interlocked.Exchange(ref lastHeard, Environment.TickCount64);
                var result = Handle(Encoding.UTF8.GetString(frame.GetBuffer(), 0, (int)frame.Length), outbox);
                if (result is { } fatal) return (fatal.Code, fatal.Message, null);
            }
            return (null, null, "Connection closed");
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested) { return (null, null, "No answer from the relay"); }
        catch (WebSocketException e) { return (null, null, e.Message); }
        finally
        {
            _out = null;
            outbox.Writer.TryComplete();
            cts.Cancel();
            try { await Task.WhenAll(sending, pinging); } catch { /* done */ }
        }
    }

    /// <summary>One message from the relay; a fatal error comes back to end the connection.</summary>
    private (string Code, string Message)? Handle(string text, Channel<string> outbox)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(text).RootElement; }
        catch (JsonException) { return null; }
        string? Str(string n) => root.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        int Int(string n) => root.TryGetProperty(n, out var p) && p.TryGetInt32(out int i) ? i : -1;
        bool Bool(string n) => root.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.True;
        switch (Str("t"))
        {
            case "created":
            case "joined":
                Code = Str("code");
                Peer = Int("peer");
                bool resumed = _opened || Bool("resumed") || Bool("rejoined");
                _opened = true;
                _out = outbox;
                Raise(new LinkEvent.Opened(Code!, Peer.Value, resumed, Str("t") == "created" || Bool("host")));
                break;
            case "peer-joined": Raise(new LinkEvent.PeerJoined(Int("peer"))); break;
            case "peer-left": Raise(new LinkEvent.PeerLeft(Int("peer"))); break;
            case "host-left": Raise(new LinkEvent.HostLeft()); break;
            case "host-back": Raise(new LinkEvent.HostBack()); break;
            case "msg": if (Str("data") is { } d) Raise(new LinkEvent.Message(Int("from"), d)); break;
            case "error":
                string code = Str("code") ?? "error", message = Str("message") ?? code;
                // Once in a lobby, a missing lobby means the relay restarted and the host isn't back yet: keep trying.
                if (_opened && code is "not-found" or "rate" or "busy") return null;
                return (code, message);
        }
        return null;
    }
}

/// <summary>The relay's messages (see <c>GrimesGolf.Relay</c>): JSON objects with a type in <c>t</c>.</summary>
public static class RelayWire
{
    /// <summary>Must match the relay's <c>RelayOptions.Protocol</c>.</summary>
    public const int Protocol = 1;

    public static string Message(string type, params (string Key, object? Value)[] fields)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("t", type);
            foreach (var (k, v) in fields)
            {
                switch (v)
                {
                    case null: break;
                    case string s: w.WriteString(k, s); break;
                    case int i: w.WriteNumber(k, i); break;
                    case bool b: w.WriteBoolean(k, b); break;
                    default: throw new ArgumentException($"Unsupported value for {k}.");
                }
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
