using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using GrimesGolf.Online.Transport;
using GrimesGolf.Relay;
using Xunit;

namespace GrimesGolf.Online.Tests;

/// <summary>The relay in-process, with <see cref="RelayClient"/>s talking to it as the game would.</summary>
public sealed class RelayTests : IDisposable
{
    private static readonly RelayClientOptions Fast = new()
    {
        FirstRetry = TimeSpan.FromMilliseconds(50), MaxRetry = TimeSpan.FromMilliseconds(200),
        PingEvery = TimeSpan.FromMilliseconds(200), DeadAfter = TimeSpan.FromSeconds(3),
    };

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-11-04T12:00:00Z"));
    private readonly List<IDisposable> _cleanup = new();
    private WebApplicationFactory<Program> _relay;
    private int _joinsPerWindow = 1000;

    public RelayTests() => _relay = StartRelay();

    private WebApplicationFactory<Program> StartRelay()
    {
        var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Relay:JoinsPerWindow", _joinsPerWindow.ToString());
            b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(_time));
        });
        _ = f.Server;   // start it
        return f;
    }

    private LobbyRegistry Registry => _relay.Services.GetRequiredService<LobbyRegistry>();

    /// <summary>Every socket made, so a relay "restart" can cut them off (the in-memory server doesn't on disposal).</summary>
    private readonly List<WebSocket> _sockets = new();

    private Func<CancellationToken, Task<WebSocket>> Connect => async ct =>
    {
        var ws = await _relay.Server.CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/ws"), ct);
        lock (_sockets) _sockets.Add(ws);
        return ws;
    };

    private (RelayClient Link, Recorder Events) Client(RelayRole role, string? code, string? token = null)
    {
        var c = new RelayClient(Connect, role, code, token ?? Guid.NewGuid().ToString("N"), Fast);
        var r = new Recorder(c);
        _cleanup.Add(c);
        c.Start();
        return (c, r);
    }

    private async Task<(RelayClient, Recorder, string)> Host()
    {
        var (h, he) = Client(RelayRole.Host, null);
        var opened = await he.WaitFor<LinkEvent.Opened>();
        Assert.Equal(0, opened.Peer);
        Assert.True(LobbyRegistry.IsValidCode(opened.Code));
        return (h, he, opened.Code);
    }

    [Fact]
    public async Task CreateJoinAndForward()
    {
        var (host, he, code) = await Host();
        var (friend, fe) = Client(RelayRole.Join, code.ToLowerInvariant());
        var joined = await fe.WaitFor<LinkEvent.Opened>();
        Assert.Equal(1, joined.Peer);
        Assert.True(joined.HostPresent);
        await he.WaitFor<LinkEvent.PeerJoined>(p => p.Peer == 1);

        friend.Send("{\"hello\":1}");
        Assert.Equal("{\"hello\":1}", (await he.WaitFor<LinkEvent.Message>()).Data);
        host.Send("to all");
        var m = await fe.WaitFor<LinkEvent.Message>();
        Assert.Equal((0, "to all"), (m.From, m.Data));
        host.Send("to one", to: 1);
        Assert.Equal("to one", (await fe.WaitFor<LinkEvent.Message>()).Data);
    }

    [Fact]
    public async Task RejoinWithTheSameTokenIsTheSamePeer()
    {
        var (_, he, code) = await Host();
        string token = Guid.NewGuid().ToString("N");
        var (first, fe) = Client(RelayRole.Join, code, token);
        await fe.WaitFor<LinkEvent.Opened>();
        first.Dispose();
        await he.WaitFor<LinkEvent.PeerLeft>(p => p.Peer == 1);

        var (_, again) = Client(RelayRole.Join, code, token);
        var opened = await again.WaitFor<LinkEvent.Opened>();
        Assert.Equal(1, opened.Peer);
        Assert.True(opened.Resumed);
        await he.WaitFor<LinkEvent.PeerJoined>(p => p.Peer == 1);
    }

    [Fact]
    public async Task UnknownCodeIsFinal()
    {
        var (_, fe) = Client(RelayRole.Join, "ZZZZZ");
        Assert.Equal("not-found", (await fe.WaitFor<LinkEvent.Closed>()).Code);
    }

    [Fact]
    public async Task LobbyHoldsFourPcs()
    {
        var (_, _, code) = await Host();
        for (int i = 1; i <= 3; i++)
        {
            var (_, fe) = Client(RelayRole.Join, code);
            Assert.Equal(i, (await fe.WaitFor<LinkEvent.Opened>()).Peer);
        }
        var (_, fifth) = Client(RelayRole.Join, code);
        Assert.Equal("full", (await fifth.WaitFor<LinkEvent.Closed>()).Code);
    }

    [Fact]
    public async Task HostlessLobbyCloses()
    {
        var (host, he, code) = await Host();
        var (_, fe) = Client(RelayRole.Join, code);
        await fe.WaitFor<LinkEvent.Opened>();
        host.Dispose();
        await fe.WaitFor<LinkEvent.HostLeft>();

        _time.Advance(TimeSpan.FromMinutes(9));
        Registry.Sweep();
        Assert.Equal(1, Registry.LobbyCount);   // still waiting for the host
        _time.Advance(TimeSpan.FromMinutes(2));
        Registry.Sweep();
        Assert.Equal("lobby-closed", (await fe.WaitFor<LinkEvent.Closed>()).Code);
        Assert.Equal(0, Registry.LobbyCount);
    }

    [Fact]
    public async Task HostComesBackToItsLobby()
    {
        string token = Guid.NewGuid().ToString("N");
        var (host, he) = Client(RelayRole.Host, null, token);
        string code = (await he.WaitFor<LinkEvent.Opened>()).Code;
        var (_, fe) = Client(RelayRole.Join, code);
        await fe.WaitFor<LinkEvent.Opened>();
        host.Dispose();
        await fe.WaitFor<LinkEvent.HostLeft>();

        var (back, be) = Client(RelayRole.Host, code, token);
        var opened = await be.WaitFor<LinkEvent.Opened>();
        Assert.True(opened.Resumed);
        await be.WaitFor<LinkEvent.PeerJoined>(p => p.Peer == 1);   // told who's already there
        await fe.WaitFor<LinkEvent.HostBack>();

        // Somebody else can't take it.
        var (_, thief) = Client(RelayRole.Host, code);
        Assert.Equal("code-taken", (await thief.WaitFor<LinkEvent.Closed>()).Code);
    }

    [Fact]
    public async Task OversizedMessageDropsTheConnection()
    {
        var (_, he, code) = await Host();
        var (friend, fe) = Client(RelayRole.Join, code);
        await fe.WaitFor<LinkEvent.Opened>();
        friend.Send(new string('x', 300 * 1024));
        await fe.WaitFor<LinkEvent.Lost>();
        await fe.WaitFor<LinkEvent.Opened>(o => o.Resumed);   // and comes straight back
        Assert.DoesNotContain(he.All, e => e is LinkEvent.Message);
    }

    [Fact]
    public async Task JoinsAreRateLimited()
    {
        _relay.Dispose();
        _joinsPerWindow = 2;
        _relay = StartRelay();
        var (_, _, code) = await Host();
        var (_, ok) = Client(RelayRole.Join, code);
        await ok.WaitFor<LinkEvent.Opened>();
        var (_, limited) = Client(RelayRole.Join, code);
        Assert.Equal("rate", (await limited.WaitFor<LinkEvent.Closed>()).Code);
    }

    [Fact]
    public async Task WrongVersionIsTurnedAway()
    {
        using var ws = await Connect(CancellationToken.None);
        await ws.SendAsync(Encoding.UTF8.GetBytes("{\"t\":\"join\",\"v\":99,\"code\":\"ABCDE\",\"token\":\"0123456789abcdef\"}"), WebSocketMessageType.Text, true, default);
        var buf = new byte[1024];
        var r = await ws.ReceiveAsync(buf, default);
        Assert.Contains("\"version\"", Encoding.UTF8.GetString(buf, 0, r.Count));
    }

    [Fact]
    public async Task RelayRestartIsSurvived()
    {
        var (host, he, code) = await Host();
        var (friend, fe) = Client(RelayRole.Join, code);
        await fe.WaitFor<LinkEvent.Opened>();

        var old = _relay;
        _relay = StartRelay();     // everything in memory is gone...
        lock (_sockets) foreach (var ws in _sockets) ws.Abort();   // ...and so is every connection to it
        old.Dispose();
        await he.WaitFor<LinkEvent.Lost>();
        await fe.WaitFor<LinkEvent.Lost>();

        // The host makes the same code again; the friend keeps trying until it's there.
        var hostBack = await he.WaitFor<LinkEvent.Opened>();
        Assert.Equal(code, hostBack.Code);
        var friendBack = await fe.WaitFor<LinkEvent.Opened>();
        Assert.True(friendBack.Resumed);
        friend.Send("still here");
        Assert.Equal("still here", (await he.WaitFor<LinkEvent.Message>()).Data);
        Assert.DoesNotContain(fe.All, e => e is LinkEvent.Closed);
        _ = host;
    }

    public void Dispose()
    {
        foreach (var c in _cleanup) c.Dispose();
        _relay.Dispose();
    }
}
