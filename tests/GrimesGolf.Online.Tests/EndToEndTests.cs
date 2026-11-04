using Microsoft.AspNetCore.Mvc.Testing;
using GrimesGolf.Core.Course;
using GrimesGolf.Online.Session;
using GrimesGolf.Online.Transport;
using Xunit;

namespace GrimesGolf.Online.Tests;

/// <summary>The real thing on this machine: the relay on Kestrel, ClientWebSockets, and a host and a friend playing.</summary>
public sealed class EndToEndTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _relay = new();
    private readonly List<IDisposable> _cleanup = new();

    public EndToEndTests()
    {
        _relay.UseKestrel(0);
        _relay.StartServer();
    }

    private Uri WsUri
    {
        get
        {
            var http = _relay.ClientOptions.BaseAddress;
            return new UriBuilder(http) { Scheme = "ws", Path = "/ws" }.Uri;
        }
    }

    /// <summary>Runs posted session work on the test's thread (as the game's main thread would).</summary>
    private sealed class Inbox
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _q = new();
        public void Post(Action a) => _q.Enqueue(a);
        public void Drain() { while (_q.TryDequeue(out var a)) a(); }
    }

    [Fact]
    public async Task HostAndFriendPlayThreeHolesOverRealSockets()
    {
        var hostInbox = new Inbox();
        var friendInbox = new Inbox();
        string hostToken = Guid.NewGuid().ToString("N"), friendToken = Guid.NewGuid().ToString("N");
        var hostLink = new RelayClient(RelayClient.WebSocketTo(WsUri), RelayRole.Host, null, hostToken);
        var hostGame = new FakeGame();
        var host = new HostSession(hostLink, hostGame, hostInbox.Post, () => Environment.TickCount64 / 1000.0, hostToken, "e2e",
                                   new[] { new Golfer("Ann") }, monitorLive: true);
        hostGame.Session = host;
        _cleanup.Add(host);
        hostLink.Start();

        async Task Until(Func<bool> done, string what)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!done())
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException(what);
                hostInbox.Drain();
                friendInbox.Drain();
                host.Tick();
                await Task.Delay(10);
            }
        }

        await Until(() => hostLink.Code != null && host.Lobby?.Code.Length == 5, "the lobby code");
        var friendLink = new RelayClient(RelayClient.WebSocketTo(WsUri), RelayRole.Join, host.Lobby!.Code, friendToken);
        var friendGame = new FakeGame();
        var friend = new ClientSession(friendLink, friendGame, friendInbox.Post, () => Environment.TickCount64 / 1000.0, friendToken, "e2e",
                                       new[] { new Golfer("Bob") }, monitorLive: true);
        friendGame.Session = friend;
        _cleanup.Add(friend);
        friendLink.Start();

        host.Configure("Test 18", new GameSetup(Holes: HoleSet.Front9), new HouseRules(PickUpAtDoublePar: true));
        await Until(() => host.WhyNotStart() == null, "Bob in the lobby");
        Assert.True(host.Start("{}"));
        await Until(() => host.Phase == OnlinePhase.Open && friend.Phase == OnlinePhase.Open, "the first turn");

        int shots = 0;
        while (hostGame.Match!.HoleIndex < 3 && shots < 200)
        {
            if (host.Phase == OnlinePhase.HoleOver)
            {
                var m = hostGame.Match;
                m.StartHole(m.HoleIndex + 1);
                host.HoleStarted();
                await Until(() => friend.Phase == OnlinePhase.Open && friend.Seq == host.Seq, "the next hole");
                continue;
            }
            int seq = host.Seq;
            var up = host.IsLocalTurn ? hostGame : friendGame;
            await Until(() => (up == hostGame ? host.IsLocalTurn : friend.IsLocalTurn), "the turn to reach the PC up");
            up.HitNext();
            shots++;
            await Until(() => hostGame.Animating && friendGame.Animating, "the shot on both PCs");
            hostGame.Finish();
            friendGame.Finish();
            await Until(() => host.Seq > seq && friend.Seq == host.Seq, "the next turn");
        }
        Assert.Equal(3, hostGame.Match.HoleIndex);
        Assert.True(hostGame.Match.Capture().SameAs(friendGame.Match!.Capture()));
        Assert.Equal(0, friendGame.ShotsPlayed);
    }

    public void Dispose()
    {
        foreach (var c in _cleanup) c.Dispose();
        _relay.Dispose();
    }
}
