using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace SquareGspro.Relay;

/// <summary>
/// One PC's WebSocket: reads its relay messages (create, join, send, ping) and writes what the lobby sends it
/// through a bounded queue, so a slow PC can't hold anything else up (it's dropped instead).
/// </summary>
public sealed class RelayConnection
{
    private readonly WebSocket _ws;
    private readonly LobbyRegistry _registry;
    private readonly RelayOptions _options;
    private readonly string _ip;
    private readonly Channel<string> _out;

    public RelayConnection(WebSocket ws, LobbyRegistry registry, RelayOptions options, string ip)
    {
        _ws = ws;
        _registry = registry;
        _options = options;
        _ip = ip;
        _out = Channel.CreateBounded<string>(new BoundedChannelOptions(options.SendQueue) { SingleReader = true });
    }

    /// <summary>The lobby this connection is in (null until create or join succeeds).</summary>
    public string? Code { get; private set; }
    /// <summary>Its peer number in the lobby: 0 for the host.</summary>
    public int Peer { get; private set; } = -1;
    public bool Bound => Code != null;

    internal void Bind(string code, int peer) { Code = code; Peer = peer; }

    /// <summary>Queues a message; a PC that has let too many pile up is cut off.</summary>
    public void Send(string json)
    {
        if (!_out.Writer.TryWrite(json)) _ws.Abort();
    }

    /// <summary>Sends an error and closes the connection once it's out.</summary>
    public void Fail(string code, string message)
    {
        _out.Writer.TryWrite(LobbyRegistry.Wire("error", ("code", code), ("message", message)));
        _out.Writer.TryComplete();
    }

    public async Task RunAsync(CancellationToken stopping)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var sending = SendLoopAsync(cts.Token);
        var buffer = new byte[16 * 1024];
        using var frame = new MemoryStream();
        try
        {
            using var hello = new CancellationTokenSource(_options.HelloTimeout);
            while (_ws.State == WebSocketState.Open)
            {
                frame.SetLength(0);
                WebSocketReceiveResult r;
                using (var receive = Bound ? CancellationTokenSource.CreateLinkedTokenSource(cts.Token)
                                           : CancellationTokenSource.CreateLinkedTokenSource(cts.Token, hello.Token))
                {
                    do
                    {
                        r = await _ws.ReceiveAsync(buffer, receive.Token);
                        if (r.MessageType == WebSocketMessageType.Close) return;
                        frame.Write(buffer, 0, r.Count);
                        if (frame.Length > _options.MaxFrameBytes)
                        {
                            await _ws.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "Message too big", CancellationToken.None);
                            return;
                        }
                    } while (!r.EndOfMessage);
                }
                if (r.MessageType != WebSocketMessageType.Text) continue;
                Handle(Encoding.UTF8.GetString(frame.GetBuffer(), 0, (int)frame.Length));
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException) { }
        finally
        {
            _registry.Left(this);
            _out.Writer.TryComplete();
            try { await sending.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* closing anyway */ }
            _ws.Abort();
        }
    }

    private void Handle(string text)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException) { Fail("bad-request", "Not JSON."); return; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { Fail("bad-request", "Not a relay message."); return; }
            string? Str(string name) => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            string? type = Str("t");
            if (type is "create" or "join")
            {
                int v = root.TryGetProperty("v", out var pv) && pv.TryGetInt32(out int iv) ? iv : 0;
                if (v != RelayOptions.Protocol) { Fail("version", "This relay speaks a different version: update the app."); return; }
                if (type == "create") _registry.Create(this, Str("code"), Str("token"), _ip);
                else _registry.Join(this, Str("code"), Str("token"), _ip);
            }
            else if (type == "send" && Str("data") is { } data)
            {
                int? to = root.TryGetProperty("to", out var pt) && pt.TryGetInt32(out int it) ? it : null;
                _registry.Forward(this, to, data);
            }
            else if (type == "ping") Send(LobbyRegistry.Wire("pong"));
            else Fail("bad-request", "Unknown relay message.");
        }
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var m in _out.Reader.ReadAllAsync(ct))
                await _ws.SendAsync(Encoding.UTF8.GetBytes(m), WebSocketMessageType.Text, true, ct);
            // Completed: an error was sent (or the PC left). Say goodbye properly.
            if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Bye", ct);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException) { }
    }
}
