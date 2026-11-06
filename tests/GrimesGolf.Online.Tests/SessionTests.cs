using GrimesGolf.Core;
using GrimesGolf.Core.Course;
using GrimesGolf.Online.Protocol;
using GrimesGolf.Online.Session;
using Xunit;

namespace GrimesGolf.Online.Tests;

/// <summary>A table of PCs in one online round, over the in-memory relay, with a clock the test moves.</summary>
public sealed class Table
{
    public const string Version = "1.2.0-test";
    public readonly MemoryRelay Relay = new();
    public double Now;
    public readonly HostSession Host;
    public readonly FakeGame HostGame = new();
    public readonly MemoryLink HostLink;
    public readonly List<(ClientSession Session, FakeGame Game, MemoryLink Link)> Friends = new();

    public Table(string[] hostPlayers, params string[][] friendPlayers)
    {
        HostLink = Relay.Host();
        Host = new HostSession(HostLink, HostGame, a => a(), () => Now, Guid.NewGuid().ToString("N"), Version,
                               hostPlayers.Select(n => new Golfer(n)).ToList(), monitorLive: true);
        HostGame.Session = Host;
        foreach (var players in friendPlayers) AddFriend(players);
        Relay.Pump();
    }

    public (ClientSession Session, FakeGame Game, MemoryLink Link) AddFriend(string[] players, string version = Version)
    {
        var link = Relay.Join();
        var game = new FakeGame();
        var s = new ClientSession(link, game, a => a(), () => Now, Guid.NewGuid().ToString("N"), version, players.Select(n => new Golfer(n)).ToList(), monitorLive: true);
        game.Session = s;
        Friends.Add((s, game, link));
        return (s, game, link);
    }

    public IEnumerable<FakeGame> Games => new[] { HostGame }.Concat(Friends.Select(f => f.Game));

    public void Start(GameType type = GameType.Stroke, HouseRules? rules = null, HoleSet holes = HoleSet.Full18)
    {
        Host.Configure("Test 18", new GameSetup(type, holes), rules ?? new HouseRules(Mulligans: 1, PickUpAtDoublePar: true));
        Relay.Pump();
        Assert.Null(Host.WhyNotStart());
        Assert.True(Host.Start("{}"));
        Relay.Pump();
        Assert.Equal(OnlinePhase.Open, Host.Phase);
    }

    /// <summary>The game whose PC has the player who's up.</summary>
    public FakeGame Up
    {
        get
        {
            int turn = HostGame.Match!.Turn;
            string pc = Host.Config!.Seats[turn].Pc;
            return pc == Host.Pc ? HostGame : Friends.First(f => f.Session.Pc == pc).Game;
        }
    }

    /// <summary>Every PC finishes showing the shot in the air.</summary>
    public void FinishAll()
    {
        foreach (var g in Games) g.Finish();
        Relay.Pump();
    }

    /// <summary>One step of the round: a shot, the shot shown, or the next hole.</summary>
    public void Step()
    {
        Relay.Pump();
        if (Games.Any(g => g.Animating)) { FinishAll(); return; }
        if (Host.Phase == OnlinePhase.HoleOver)
        {
            var m = HostGame.Match!;
            m.StartHole(m.HoleIndex + 1);
            Host.HoleStarted();
            Relay.Pump();
            return;
        }
        if (Host.Phase == OnlinePhase.Open) { Up.HitNext(); Relay.Pump(); }
    }

    public void PlayToTheEnd(int maxSteps = 5000)
    {
        for (int i = 0; i < maxSteps && Host.Phase != OnlinePhase.Over; i++) Step();
        Assert.Equal(OnlinePhase.Over, Host.Phase);
    }

    public void AssertAllInStep()
    {
        var host = HostGame.Match!.Capture();
        foreach (var (s, g, _) in Friends.Where(f => f.Session.EndedBecause == null))
            Assert.True(host.SameAs(g.Match!.Capture()), $"{string.Join(",", s.Config!.Seats.Where(x => x.Pc == s.Pc).Select(x => x.Name))} is out of step");
    }
}

public class SessionTests
{
    [Theory]
    [InlineData(GameType.Stroke)]
    [InlineData(GameType.BestBall)]
    [InlineData(GameType.MatchPlay)]
    public void TwoPcsPlay18HolesInStep(GameType type)
    {
        var t = new Table(new[] { "Ann" }, new[] { "Bob" });
        t.Start(type);
        t.PlayToTheEnd();
        t.AssertAllInStep();
        Assert.True(t.HostGame.Match!.RoundComplete);
        Assert.Equal(18, t.HostGame.Match.Holes.Count);
        Assert.All(t.Games, g => Assert.NotNull(g.Over));
        Assert.Equal(t.HostGame.Over!.Value.Summary, t.Friends[0].Game.Over!.Value.Summary);
        Assert.Equal(0, t.Friends[0].Game.ShotsPlayed);   // friends never play a shot themselves
    }

    [Theory]
    [InlineData(GameType.Scramble)]
    [InlineData(GameType.BestBall)]
    [InlineData(GameType.MatchPlay)]
    [InlineData(GameType.Stroke)]
    public void ThreePcsWithFourPlayersPlay18HolesInStep(GameType type)
    {
        var t = new Table(new[] { "Ann", "Bob" }, new[] { "Cat" }, new[] { "Dee" });
        t.Start(type);
        Assert.Equal(new[] { "Ann", "Bob", "Cat", "Dee" }, t.Host.Config!.Seats.Select(s => s.Name));
        t.PlayToTheEnd();
        t.AssertAllInStep();
        Assert.True(t.HostGame.Match!.RoundComplete);
    }

    [Fact]
    public void LobbyChecksVersionsNamesAndRoom()
    {
        var t = new Table(new[] { "Ann", "Bob" }, new[] { "ann" });
        var (old, oldGame, _) = t.AddFriend(new[] { "Eve" }, version: "1.1.0");
        var (big, bigGame, _) = t.AddFriend(new[] { "Fay", "Gus", "Hal" });
        t.Relay.Pump();
        Assert.Contains("Different versions", oldGame.EndedReason);
        Assert.Contains("No room", bigGame.EndedReason);
        Assert.Equal(new[] { "Ann", "Bob", "ann 2" }, t.Host.Seats.Select(s => s.Name));
        Assert.Equal(3, t.Friends[0].Game.Lobby!.Seats.Length);

        // The host reorders seats (seat order sets the teams).
        t.Host.MoveSeat(2, 0);
        t.Relay.Pump();
        Assert.Equal("ann 2", t.Friends[0].Session.Lobby!.Seats[0].Name);

        // A friend's monitor going off stops the start.
        t.Host.Configure("Test 18", GameSetup.Default, HouseRules.Strict);
        t.Friends[0].Session.SetMonitor(false);
        t.Relay.Pump();
        Assert.Contains("launch monitor", t.Host.WhyNotStart());
        Assert.False(t.Host.Start("{}"));
        _ = old; _ = big;
    }

    [Fact]
    public void DifferentCourseDataIsTurnedAway()
    {
        var t = new Table(new[] { "Ann" }, new[] { "Bob" }, new[] { "Cat" });
        t.Friends[1].Game.BuildLater = true;
        t.Host.Configure("Test 18", GameSetup.Default, HouseRules.Strict);
        t.Relay.Pump();
        t.Host.Start("{}");
        t.Relay.Pump();
        Assert.Equal(OnlinePhase.Loading, t.Host.Phase);   // waiting for Cat's PC
        t.Friends[1].Session.RoundBuilt("not-the-same");
        t.Relay.Pump();
        Assert.Contains("copy of the course", t.Friends[1].Game.EndedReason);
        Assert.Equal(OnlinePhase.Open, t.Host.Phase);       // the others carry on
    }

    [Fact]
    public void OffTurnShotsDontCount()
    {
        var t = new Table(new[] { "Ann" }, new[] { "Bob" });
        t.Start();
        Assert.Same(t.HostGame, t.Up);
        t.Friends[0].Game.HitNext();          // Bob's monitor, but it's Ann's turn
        t.Relay.Pump();
        Assert.Contains(t.Friends[0].Game.Notices, n => n.Contains("Ann's turn"));
        Assert.Equal(0, t.HostGame.ShotsPlayed);

        // Even sent straight to the host, a shot for the wrong player or turn is turned down.
        var bad = new ShotRequest(Guid.NewGuid(), t.Host.Seq, 0, FakeGame.BadShot, "I7", 0, false);
        t.Friends[0].Link.Send(bad.Encode());
        t.Friends[0].Link.Send((bad with { Id = Guid.NewGuid(), Player = 1, Seq = 99 }).Encode());
        t.Friends[0].Link.Send((bad with { Id = Guid.NewGuid(), Shot = bad.Shot with { BallMph = double.MaxValue } }).Encode());
        t.Relay.Pump();
        Assert.Equal(0, t.HostGame.ShotsPlayed);
    }

    [Fact]
    public void DuplicateShotIsPlayedOnce()
    {
        var t = new Table(new[] { "Ann" }, new[] { "Bob" });
        t.Start();
        t.Step(); t.Step();                   // Ann hits, everyone shows it: Bob's turn
        Assert.Same(t.Friends[0].Game, t.Up);
        t.Friends[0].Game.HitNext();
        var sent = t.Friends[0].Link.Sent.Last();
        t.Friends[0].Link.Send(sent);         // the same shot again (a resend after a blip)
        t.Relay.Pump();
        Assert.Equal(2, t.HostGame.ShotsPlayed);
        Assert.Equal(2, t.Friends[0].Game.Shown.Count(s => s.Player == 1));   // the result twice, the same one
        Assert.Single(t.Friends[0].Game.Shown.Where(s => s.Player == 1).Select(s => s.Id).Distinct());
    }

    [Fact]
    public void ShotLostInADropIsSentAgainAndPlayedOnce()
    {
        var t = new Table(new[] { "Ann" }, new[] { "Bob" });
        t.Start();
        t.Step(); t.Step();                   // Bob's turn
        var bob = t.Friends[0];
        t.Relay.Drop(bob.Link);
        bob.Game.HitNext();                   // sent into the void
        t.Relay.Pump();
        Assert.True(bob.Session.ShotPending);
        Assert.Contains("dropped out", t.Host.Banner);   // the host is counting down for Bob
        t.Relay.Reconnect(bob.Link);
        t.Relay.Pump();
        Assert.Equal(2, t.HostGame.ShotsPlayed);
        Assert.False(bob.Session.ShotPending);
        Assert.Null(t.Host.Banner);
        t.FinishAll();
        t.AssertAllInStep();
    }

    [Fact]
    public void SlowPcDoesntHoldUpTheNextTurn()
    {
        var t = new Table(new[] { "Ann" }, new[] { "Bob" });
        t.Start();
        t.HostGame.HitNext();
        t.Relay.Pump();
        t.HostGame.Finish();                  // the host has shown it; Bob's PC is still animating
        t.Relay.Pump();
        Assert.Equal(OnlinePhase.Flight, t.Host.Phase);
        t.Now += 60;
        t.Host.Tick();
        t.Relay.Pump();
        Assert.Equal(OnlinePhase.Open, t.Host.Phase);
        t.Friends[0].Game.Finish();           // late: ignored
        t.Relay.Pump();
        t.AssertAllInStep();
    }

    [Fact]
    public void DroppedPlayerIsSkippedThenComesBack()
    {
        var t = new Table(new[] { "Ann" }, new[] { "Bob" });
        t.Start();
        t.Step(); t.Step();                   // Bob's turn
        var bob = t.Friends[0];
        t.Relay.Drop(bob.Link);
        t.Relay.Pump();
        t.Now += OnlineSession.DroppedSeconds - 1;
        t.Host.Tick();
        Assert.Equal(1, t.HostGame.Match!.Turn);          // still waiting
        t.Now += 2;
        t.Host.Tick();
        t.Relay.Pump();
        Assert.True(t.HostGame.Match.IsAway(1));
        Assert.True(t.HostGame.Match.Rounds[1].PickedUp);
        Assert.Equal(0, t.HostGame.Match.Turn);           // play goes on

        // Ann finishes the hole and the next; Bob picks up on both.
        int hole = t.HostGame.Match.HoleIndex;
        for (int i = 0; i < 400 && t.HostGame.Match.HoleIndex < hole + 1; i++) t.Step();
        Assert.True(t.HostGame.Match.Rounds[1].PickedUp);

        // Bob's back: in step at once, playing again from the next hole.
        t.Relay.Reconnect(bob.Link);
        t.Relay.Pump();
        t.AssertAllInStep();
        Assert.False(t.HostGame.Match.IsAway(1));
        for (int i = 0; i < 400 && t.HostGame.Match.HoleIndex < hole + 2; i++) t.Step();
        Assert.True(t.HostGame.Match.Rounds[1].Strokes > 0 || !t.HostGame.Match.Rounds[1].PickedUp);
        t.AssertAllInStep();
    }

    [Fact]
    public void HostCanSkipSooner()
    {
        var t = new Table(new[] { "Ann" }, new[] { "Bob" });
        t.Start();
        t.Step(); t.Step();
        t.Relay.Drop(t.Friends[0].Link);
        t.Relay.Pump();
        Assert.True(t.Host.CanSkip);
        t.Host.SkipNow();
        Assert.True(t.HostGame.Match!.IsAway(1));
    }

    [Fact]
    public void MulliganOnlyFromTheHittersPc()
    {
        var t = new Table(new[] { "Ann" }, new[] { "Bob" });
        t.Start(rules: new HouseRules(Mulligans: 1));
        t.HostGame.Session.MonitorShot(FakeGame.BadShot, "I7", t.HostGame.Match!.Current.DefaultAimHeading(), false);
        t.Relay.Pump();
        t.FinishAll();
        // Bob can't take back Ann's shot.
        t.Friends[0].Session.RequestMulligan();
        t.Relay.Pump();
        Assert.Contains(t.Friends[0].Game.Notices, n => n.Contains("Only the player"));
        // Ann can.
        int strokes = t.HostGame.Match.Rounds[0].Strokes;
        t.Host.RequestMulligan();
        t.Relay.Pump();
        Assert.Equal(strokes - 1, t.HostGame.Match.Rounds[0].Strokes);
        Assert.Equal(0, t.HostGame.Match.Turn);
        t.AssertAllInStep();
    }

    [Fact]
    public void MulliganCrossingTheNextShotLoses()
    {
        var t = new Table(new[] { "Ann" }, new[] { "Bob" });
        t.Start(rules: new HouseRules(Mulligans: 1));
        // Ann's tee shot, then Bob's: Bob is away from the hole after his, so it's... whoever. Make it Bob's shot last.
        t.Step(); t.Step();                    // Ann hit; Bob up
        var bob = t.Friends[0];
        bob.Game.HitNext(); t.Relay.Pump(); t.FinishAll();
        // Bob asks for a mulligan while the next player's shot is already being played on the host.
        bob.Session.RequestMulligan();
        if (t.Up == t.HostGame) t.HostGame.HitNext();   // the host's shot gets in first (it's local)
        t.Relay.Pump();
        if (t.HostGame.ShotsPlayed == 3)
            Assert.Contains(bob.Game.Notices, n => n.Contains("Wait for the ball") || n.Contains("Only the player"));
        t.FinishAll();
        t.AssertAllInStep();
    }

    [Fact]
    public void HostLeavingAndComingBack()
    {
        var t = new Table(new[] { "Ann" }, new[] { "Bob" });
        t.Start();
        t.Step(); t.Step();
        var bob = t.Friends[0];
        t.Relay.Drop(t.HostLink);
        t.Relay.Pump();
        Assert.Contains("host has disconnected", bob.Session.Banner);
        t.Now += OnlineSession.HostGoneSeconds + 1;
        Assert.True(bob.Session.HostGone);
        t.Relay.Reconnect(t.HostLink);
        t.Relay.Pump();
        Assert.False(bob.Session.HostGone);
        Assert.Null(bob.Session.Banner);
        t.PlayToTheEnd();
        t.AssertAllInStep();
    }

    [Fact]
    public void HostPausesAndResumesLater()
    {
        var t = new Table(new[] { "Ann" }, new[] { "Bob" });
        OnlineSave? saved = null;
        t.Host.Checkpoint += s => saved = s;
        t.Start();
        for (int i = 0; i < 30; i++) t.Step();
        while (t.Games.Any(g => g.Animating)) t.FinishAll();
        t.Host.Stop(paused: true);
        t.Relay.Pump();
        Assert.True(t.Friends[0].Game.Over!.Value.Paused);
        Assert.NotNull(saved);

        // Later: a new host session from the save, and Bob rejoins.
        var json = System.Text.Json.JsonSerializer.Serialize(saved, OnlineMessage.Json);
        var save = System.Text.Json.JsonSerializer.Deserialize<OnlineSave>(json, OnlineMessage.Json)!;
        var relay = new MemoryRelay();
        var hostGame = new FakeGame();
        var host = new HostSession(relay.Host(), hostGame, a => a(), () => 0, save.Token, Table.Version, Array.Empty<Golfer>(), true);
        hostGame.Session = host;
        host.Resume(save);
        relay.Pump();
        Assert.True(save.Snapshot.SameAs(hostGame.Match!.Capture()));

        var bobGame = new FakeGame();
        var bob = new ClientSession(relay.Join(), bobGame, a => a(), () => 0, Guid.NewGuid().ToString("N"), Table.Version, new[] { new Golfer("Bob") }, true);
        bobGame.Session = bob;
        relay.Pump();
        Assert.NotNull(bobGame.EndedReason);      // a different PC (new token) isn't one of the round's

        // The same PC (its token) is let back in and synced.
        var bob2Game = new FakeGame();
        var bob2 = new ClientSession(relay.Join(), bob2Game, a => a(), () => 0, t.Friends[0].Session.Token, Table.Version, new[] { new Golfer("Bob") }, true);
        bob2Game.Session = bob2;
        relay.Pump();
        Assert.Null(bob2Game.EndedReason);
        Assert.True(hostGame.Match.Capture().SameAs(bob2Game.Match!.Capture()));
        Assert.Equal(OnlinePhase.Open, bob2.Phase);
    }

    [Fact]
    public void AimIsMirroredAndThrottled()
    {
        var t = new Table(new[] { "Ann" }, new[] { "Bob" });
        t.Start();
        for (int i = 0; i < 20; i++) { t.Host.LocalAim(0.01 * i, "DR", 0.8); t.Host.Tick(); }
        t.Relay.Pump();
        Assert.InRange(t.Friends[0].Game.Aims, 1, 2);
        t.Now += 0.2;
        t.Host.Tick();
        t.Relay.Pump();
        Assert.InRange(t.Friends[0].Game.Aims, 2, 3);
        t.Friends[0].Session.LocalAim(1, "DR", 1);   // not Bob's turn: nothing goes
        t.Relay.Pump();
        Assert.Equal(0, t.HostGame.Aims);
    }

    [Fact]
    public void FriendLeavingIsSkippedAtOnce()
    {
        var t = new Table(new[] { "Ann" }, new[] { "Bob" });
        t.Start();
        t.Step(); t.Step();                    // Bob up
        t.Friends[0].Session.Leave();
        t.Relay.Pump();
        Assert.True(t.HostGame.Match!.IsAway(1));
        Assert.Equal(0, t.HostGame.Match.Turn);
    }

    [Fact]
    public void OutcomeSurvivesTheWire()
    {
        var m = new Match(FakeGame.Holes, null, new[] { new Golfer("Ann") });
        var o = m.Play(new RangeShot(150, 11, 0, 2700, 0, null, null), false, m.Current.DefaultAimHeading(), "DR");
        var data = OutcomeData.Of(o);
        var back = ((ShotResult)OnlineMessage.Decode(new ShotResult(1, Guid.NewGuid(), 0, new RangeShot(1, 2, 3, 4, 5, null, null), "DR", 0.5, false, data, m.Capture()).Encode())!).Outcome.ToOutcome();
        Assert.Equal(o.Flight.Points.Count, back.Flight.Points.Count);
        Assert.Equal(o.Message, back.Message);
        Assert.Equal(o.NextBall, back.NextBall);
        Assert.InRange(Math.Abs(o.Flight.Points[^1].X - back.Flight.Points[^1].X), 0, 0.01);
        Assert.True(data.Path.Length < 32 * 1024);
        Assert.Null(OnlineMessage.Decode("{\"type\":\"nonsense\"}"));
        Assert.Null(OnlineMessage.Decode("not json"));
    }
}
