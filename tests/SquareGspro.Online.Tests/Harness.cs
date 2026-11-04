using SquareGspro.Core;
using SquareGspro.Core.Course;
using SquareGspro.Online.Protocol;
using SquareGspro.Online.Session;
using SquareGspro.Online.Transport;

namespace SquareGspro.Online.Tests;

/// <summary>
/// A relay in memory: messages wait in one queue until <see cref="Pump"/>, so a test decides exactly what crosses
/// when, and can drop and bring back any PC.
/// </summary>
public sealed class MemoryRelay
{
    private readonly Queue<Action> _queue = new();
    private readonly List<MemoryLink> _links = new();

    public MemoryLink Host() => Add(0);
    public MemoryLink Join() => Add(_links.Count);

    private MemoryLink Add(int peer)
    {
        var l = new MemoryLink(this, peer);
        _links.Add(l);
        Enqueue(() => l.Raise(new LinkEvent.Opened("TESTS", peer, false, true)));
        if (peer != 0) Enqueue(() => HostLink?.Raise(new LinkEvent.PeerJoined(peer)));
        return l;
    }

    private MemoryLink? HostLink => _links.FirstOrDefault(l => l.Peer == 0);

    private void Enqueue(Action a) => _queue.Enqueue(a);

    internal void Route(MemoryLink from, string data, int? to)
    {
        if (!from.Up) return;   // lost on the way
        foreach (var l in _links.Where(l => from.Peer == 0 ? l.Peer != 0 && (to == null || to == l.Peer) : l.Peer == 0))
            Enqueue(() => { if (l.Up) l.Raise(new LinkEvent.Message(from.Peer, data)); });
    }

    /// <summary>Delivers everything waiting (and whatever that sends in turn).</summary>
    public int Pump()
    {
        int n = 0;
        while (_queue.Count > 0 && n < 100_000) { _queue.Dequeue()(); n++; }
        return n;
    }

    /// <summary>The PC's connection drops (anything it sends from now is lost).</summary>
    public void Drop(MemoryLink l)
    {
        l.Up = false;
        Enqueue(() => l.Raise(new LinkEvent.Lost("dropped")));
        if (l.Peer == 0) foreach (var f in _links.Where(x => x.Peer != 0 && x.Up)) Enqueue(() => f.Raise(new LinkEvent.HostLeft()));
        else Enqueue(() => { if (HostLink is { Up: true } h) h.Raise(new LinkEvent.PeerLeft(l.Peer)); });
    }

    public void Reconnect(MemoryLink l)
    {
        l.Up = true;
        Enqueue(() => l.Raise(new LinkEvent.Opened("TESTS", l.Peer, true, HostLink?.Up ?? false)));
        if (l.Peer == 0) foreach (var f in _links.Where(x => x.Peer != 0 && x.Up)) Enqueue(() => f.Raise(new LinkEvent.HostBack()));
        else Enqueue(() => { if (HostLink is { Up: true } h) h.Raise(new LinkEvent.PeerJoined(l.Peer)); });
    }
}

public sealed class MemoryLink(MemoryRelay relay, int peer) : ILobbyLink
{
    public event Action<LinkEvent>? Event;
    public string? Code => "TESTS";
    public int Peer { get; } = peer;
    int? ILobbyLink.Peer => Peer;
    public bool Up { get; set; } = true;
    public List<string> Sent { get; } = new();

    internal void Raise(LinkEvent e) => Event?.Invoke(e);

    public void Send(string data, int? to = null)
    {
        Sent.Add(data);
        relay.Route(this, data, to);
    }

    public void Dispose() => Up = false;
}

/// <summary>
/// The game on one PC, without Godot: a real <see cref="Match"/> on 18 flat holes, with "animation" that lasts until
/// the test calls <see cref="Finish"/>.
/// </summary>
public sealed class FakeGame : IOnlineGame
{
    /// <summary>The three test holes over and over, numbered 1-18.</summary>
    public static readonly IReadOnlyList<Hole> Holes =
        Enumerable.Range(0, 18).Select(i => TestHoles[i % TestHoles.Count] with { Number = i + 1 }).ToList();

    private static IReadOnlyList<Hole> TestHoles => SquareGspro.Tests.TestCourse.Holes;

    public OnlineSession Session { get; set; } = null!;
    public Match? Match { get; private set; }
    public bool Animating { get; private set; }
    public int ShotsPlayed { get; private set; }
    public bool BuildLater { get; set; }
    public RoundConfig? Building { get; private set; }
    public List<string> Notices { get; } = new();
    public List<ShotResult> Shown { get; } = new();
    public string? EndedReason { get; private set; }
    public (bool Paused, string Summary)? Over { get; private set; }
    public LobbyState? Lobby { get; private set; }

    public void BuildRound(RoundConfig config, MatchSnapshot? snapshot)
    {
        var holes = config.HoleNumbers.Length > 0 ? config.HoleNumbers.Select(n => Holes.First(h => h.Number == n)).ToList() : config.Setup.Pick(Holes);
        Match = new Match(holes, null, config.Golfers, config.Rules, config.Setup.Type);
        if (snapshot != null) Match.Restore(snapshot); else Match.StartHole(0);
        Building = config;
        if (!BuildLater) Built();
    }

    public void Built()
    {
        Building = null;
        Session.RoundBuilt(CourseFingerprint.Of(Match!.Holes, null));
    }

    public void ShowShot(ShotResult shot)
    {
        Match!.Restore(shot.Snapshot);
        Shown.Add(shot);
        Animating = true;
    }

    /// <summary>The ball has stopped here: on to whoever's next, as FinishShot does, and tell the session.</summary>
    public void Finish()
    {
        if (!Animating) return;
        Animating = false;
        Match!.AdvanceTurn();
        Session.ShotShown();
    }

    public void OpenTurn(TurnOpen turn) => Match?.Restore(turn.Snapshot);
    public void ShowAim(Aim aim) => Aims++;
    public int Aims { get; private set; }
    public void Notice(string text) => Notices.Add(text);
    public void LobbyChanged(LobbyState lobby) => Lobby = lobby;
    public void RoundOver(bool paused, string summary) => Over = (paused, summary);
    public void Ended(string reason) => EndedReason = reason;

    public OutcomeData? PlayShot(ShotRequest r)
    {
        var round = Match!.Current;
        int before = round.Strokes;
        var o = Match.Play(r.Shot, r.Putt, r.Heading, r.Club);
        if (round.Strokes == before && !o.HoleOver) return null;
        ShotsPlayed++;
        Animating = true;
        return OutcomeData.Of(o);
    }

    public void FinishShotNow() => Finish();
    public void MatchChanged() { }
    public bool Mulligan() => Match!.Mulligan();

    // ---- a golfer at the mat ----

    private static readonly RangeShot Drive = new(150, 11, 0, 2700, 0, null, null);
    private static readonly RangeShot Iron = new(112, 18, 0, 7600, 0, null, null);

    /// <summary>Hits the next shot for whoever's up here, as their monitor would send it.</summary>
    public void HitNext()
    {
        var r = Match!.Current;
        bool putt = r.DistanceToPin < 40;
        var shot = putt ? new RangeShot(Math.Sqrt(2 * 0.55 * Math.Max(0.1, r.DistanceToPin - 0.3)) * ShotMapper.MsToMph, 1, 0, 0, 0, null, null)
                 : r.DistanceToPin > 180 ? Drive : Iron;
        Session.MonitorShot(shot, putt ? "PT" : r.DistanceToPin > 180 ? "DR" : "I7", r.DefaultAimHeading(), putt);
    }

    public static RangeShot BadShot => new(70, 12, 0, 4000, 0, null, null);
}
