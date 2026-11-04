using GrimesGolf.Core;
using GrimesGolf.Core.Course;
using GrimesGolf.Online.Protocol;
using GrimesGolf.Online.Transport;

namespace GrimesGolf.Online.Session;

/// <summary>
/// The host's PC: runs the lobby, plays every shot (its own and its friends'), and tells every PC what happened.
/// <list type="number">
/// <item>The PC whose turn it is sends its monitor's shot (<see cref="ShotRequest"/>); the host checks seat, turn,
/// sequence and numbers, and plays it with the game's own code.</item>
/// <item>Every PC gets the outcome and the match after it (<see cref="ShotResult"/>) and animates it.</item>
/// <item>Each PC says when it has shown it; once all have (or the flight time plus a few seconds has gone by), the
/// next turn opens (<see cref="TurnOpen"/>).</item>
/// </list>
/// A player whose PC drops (or whose monitor does) gets a countdown when their turn comes, then is skipped and their
/// holes picked up until they're back.
/// </summary>
public sealed class HostSession : OnlineSession
{
    private sealed class PcState(string pc, bool host)
    {
        public string Pc { get; } = pc;
        public bool Host { get; } = host;
        public int? Peer { get; set; }
        public bool Connected { get; set; }
        public bool MonitorLive { get; set; }
        public string? Fingerprint { get; set; }
        public bool Shown { get; set; }
        public bool Refused { get; set; }
    }

    private enum Wait { Dropped, Monitor }

    private readonly List<PcState> _pcs = new();
    private readonly List<Seat> _seats = new();
    private string? _course;
    private GameSetup _setup = GameSetup.Default;
    private HouseRules _rules = HouseRules.Strict;
    private ShotResult? _lastResult;
    private double _shownBy;
    private double _loadingBy;
    private (int Player, double At, Wait Why)? _countdown;
    private string? _linkProblem;

    public HostSession(ILobbyLink link, IOnlineGame game, Action<Action> post, Func<double> clock, string token, string appVersion,
                       IReadOnlyList<Golfer> players, bool monitorLive)
        : base(link, game, post, clock, token, appVersion)
    {
        var me = new PcState(Pc, host: true) { Peer = 0, Connected = true, MonitorLive = monitorLive };
        _pcs.Add(me);
        SetHostPlayers(players);
    }

    public override bool IsHost => true;

    /// <summary>Starting needs every PC's launch monitor live (off only for development runs with a fake monitor).</summary>
    public bool RequireMonitors { get; set; } = true;

    /// <summary>Raised with the round as it stands after every turn, for <c>online-round.json</c>.</summary>
    public event Action<OnlineSave>? Checkpoint;

    public IReadOnlyList<Seat> Seats => _seats;

    private PcState Me => _pcs[0];

    public override string? Banner
    {
        get
        {
            if (_linkProblem != null) return _linkProblem;
            if (_countdown is { } c)
            {
                int left = Math.Max(0, (int)Math.Ceiling(c.At - Clock()));
                string why = c.Why == Wait.Monitor ? "'s launch monitor is disconnected" : " has dropped out";
                return $"{Who(Config, c.Player)}{why}: skipping in {left} s (Skip to go now)";
            }
            return null;
        }
    }

    /// <summary>A countdown to skip a player is running (the host can skip now).</summary>
    public bool CanSkip => _countdown != null;

    // ---- lobby -----------------------------------------------------------------------------------

    /// <summary>The course and settings for the round (the host's choice; shown to everyone in the lobby).</summary>
    public void Configure(string course, GameSetup setup, HouseRules rules)
    {
        _course = course;
        _setup = setup;
        _rules = rules;
        BroadcastLobby();
    }

    /// <summary>The players at the host's own PC (they go first in the seat order, unless moved).</summary>
    public void SetHostPlayers(IReadOnlyList<Golfer> players)
    {
        if (Phase != OnlinePhase.Lobby) return;
        _seats.RemoveAll(s => s.Pc == Pc);
        var mine = players.Take(Match.MaxPlayers).Select((g, i) => new Seat(Pc, CleanName(g.Name, i), g.LeftHanded)).ToList();
        _seats.InsertRange(0, Dedupe(mine, _seats));
        while (_seats.Count > Match.MaxPlayers) _seats.RemoveAt(_seats.Count - 1);
        BroadcastLobby();
    }

    /// <summary>Moves a seat (seat order is teeing order and sets the teams).</summary>
    public void MoveSeat(int from, int to)
    {
        if (Phase != OnlinePhase.Lobby || from < 0 || from >= _seats.Count || to < 0 || to >= _seats.Count || from == to) return;
        var s = _seats[from];
        _seats.RemoveAt(from);
        _seats.Insert(to, s);
        BroadcastLobby();
    }

    public override void SetMonitor(bool live)
    {
        if (Me.MonitorLive == live) return;
        Me.MonitorLive = live;
        BroadcastLobby();
        if (Phase == OnlinePhase.Open) WatchTurn();
    }

    /// <summary>Why the round can't start yet (null: it can).</summary>
    public string? WhyNotStart()
    {
        if (Phase != OnlinePhase.Lobby) return "The round has started.";
        if (_course == null) return "Pick a course.";
        var friends = _pcs.Where(p => !p.Host && !p.Refused).ToList();
        if (friends.Count == 0) return "Waiting for friends to join.";
        if (friends.FirstOrDefault(p => !p.Connected) is { } gone) return $"{Label(gone)} isn't connected.";
        if (RequireMonitors && _pcs.Where(p => !p.Refused).FirstOrDefault(p => !p.MonitorLive) is { } off) return $"{Label(off)}: launch monitor not connected.";
        if (_seats.Count is < 1 or > Match.MaxPlayers) return $"1 to {Match.MaxPlayers} players.";
        if (GameSetup.Problem(_setup.Type, _seats.Count) is { } p) return $"{GameSetup.Name(_setup.Type)} {p}.";
        return null;
    }

    private string Label(PcState pc) => _seats.FirstOrDefault(s => s.Pc == pc.Pc)?.Name ?? (pc.Host ? "Host" : "A friend's PC");

    private LobbyState State()
    {
        var why = WhyNotStart();
        return new LobbyState(Code ?? "", _pcs.Where(p => !p.Refused).Select(p => new PcInfo(p.Pc, Label(p), p.Host, p.Connected, p.MonitorLive)).ToArray(),
            _seats.ToArray(), _course, _setup, _rules, why == null, why, Phase != OnlinePhase.Lobby);
    }

    private void BroadcastLobby()
    {
        Lobby = State();
        Broadcast(new LobbyUpdate(Lobby));
        Game.LobbyChanged(Lobby);
    }

    private static List<Seat> Dedupe(IEnumerable<Seat> add, IEnumerable<Seat> existing)
    {
        var taken = new HashSet<string>(existing.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        var list = new List<Seat>();
        foreach (var s in add)
        {
            string name = s.Name;
            for (int n = 2; taken.Contains(name); n++)
            {
                string suffix = " " + n;
                name = (s.Name.Length + suffix.Length > OnlineProtocol.MaxNameLength ? s.Name[..(OnlineProtocol.MaxNameLength - suffix.Length)] : s.Name) + suffix;
            }
            taken.Add(name);
            list.Add(s with { Name = name });
        }
        return list;
    }

    // ---- starting and carrying on ----------------------------------------------------------------

    /// <summary>
    /// Starts the round (if <see cref="WhyNotStart"/> allows): the host's PC builds it first, then everyone is sent
    /// it, and the first turn opens once every PC has built the same course.
    /// </summary>
    public bool Start(string tuningJson)
    {
        if (WhyNotStart() != null) return false;
        Config = new RoundConfig(_course!, "", _setup, _rules, tuningJson, _seats.ToArray(), DateTime.Now, Array.Empty<int>());
        Phase = OnlinePhase.Loading;
        BroadcastLobby();
        Game.BuildRound(Config, null);
        return true;
    }

    /// <summary>Carries on a round saved earlier (<see cref="OnlineSave"/>): friends rejoin with the same code.</summary>
    public void Resume(OnlineSave save)
    {
        Config = save.Config;
        Seq = save.Seq;
        _course = save.Config.Course;
        _setup = save.Config.Setup;
        _rules = save.Config.Rules;
        _seats.Clear();
        _seats.AddRange(save.Config.Seats);
        foreach (var pc in save.Config.Seats.Select(s => s.Pc).Distinct().Where(p => p != Pc)) _pcs.Add(new PcState(pc, host: false));
        Phase = OnlinePhase.Loading;
        _resumeSnapshot = save.Snapshot;
        Game.BuildRound(Config, save.Snapshot);
    }

    private MatchSnapshot? _resumeSnapshot;

    public override void RoundBuilt(string fingerprint)
    {
        if (Config == null || Game.Match is not { } m || Phase != OnlinePhase.Loading) return;
        if (_resumeSnapshot != null)
        {
            _resumeSnapshot = null;
            Me.Fingerprint = fingerprint;
            OpenTurn(TurnReason.Resync);
            return;
        }
        Config = Config with { Fingerprint = fingerprint, HoleNumbers = m.Holes.Select(h => h.Number).ToArray() };
        Me.Fingerprint = fingerprint;
        foreach (var pc in _pcs.Where(p => !p.Host)) pc.Fingerprint = null;
        Seq = 0;
        _loadingBy = Clock() + 90;
        Broadcast(new RoundStart(Config, m.Capture(), Seq));
        CheckLoaded();
    }

    /// <summary>Every connected PC has built the course (or the wait is over): the first turn opens.</summary>
    private void CheckLoaded()
    {
        if (Phase != OnlinePhase.Loading || Me.Fingerprint == null || _resumeSnapshot != null) return;
        bool ready = _pcs.Where(p => !p.Host && p.Connected && !p.Refused).All(p => p.Fingerprint == Me.Fingerprint);
        if (ready || Clock() > _loadingBy) OpenTurn(TurnReason.Start);
    }

    // ---- shots -----------------------------------------------------------------------------------

    public override void MonitorShot(RangeShot shot, string club, double heading, bool putt)
    {
        if (Game.Match is not { } m) return;
        if (!IsLocalTurn) { Game.Notice(NotYours(Me)); return; }
        Accept(new ShotRequest(Guid.NewGuid(), Seq, m.Turn, shot, club, heading, putt), Me);
    }

    private string NotYours(PcState pc) => Phase switch
    {
        OnlinePhase.Flight => "Wait for the last shot to finish: that one didn't count",
        OnlinePhase.Open when Game.Match is { } m => $"It's {Who(Config, m.Turn)}'s turn: that shot didn't count",
        _ => "No shot to play right now: that one didn't count",
    };

    private void Accept(ShotRequest r, PcState pc)
    {
        if (_lastResult is { } last && last.Id == r.Id)
        {
            // Sent again after a blip, and already played: send the result again.
            if (pc.Peer is int peer && !pc.Host) Send(last, peer);
            return;
        }
        if (Game.Match is not { } m || Config == null) return;
        if (Phase != OnlinePhase.Open || r.Seq != Seq || r.Player != m.Turn || !IsSeatOf(r.Player, pc) || m.HoleComplete)
        {
            Tell(pc, NotYours(pc), r.Id);
            return;
        }
        if (!r.Shot.IsFinite || !double.IsFinite(r.Heading) || ClubPresets.Find(r.Club) == null) { Tell(pc, "That shot's numbers didn't make sense: not counted", r.Id); return; }

        _countdown = null;
        var shot = r.Shot with { Source = ShotSource.Monitor };
        var o = Game.PlayShot(r with { Shot = shot });
        if (o == null) { Tell(pc, "The shot couldn't be played (impossible numbers): not counted", r.Id); return; }
        var result = new ShotResult(Seq, r.Id, r.Player, shot, r.Club, r.Heading, r.Putt, o, m.Capture());
        _lastResult = result;
        Phase = OnlinePhase.Flight;
        foreach (var p in _pcs) p.Shown = false;
        _shownBy = Clock() + o.Duration + ShownGraceSeconds;
        Broadcast(result);
    }

    private bool IsSeatOf(int player, PcState pc) => Config is { } c && player < c.Seats.Length && c.Seats[player].Pc == pc.Pc;

    public override void ShotShown()
    {
        if (Phase != OnlinePhase.Flight) return;
        Me.Shown = true;
        CheckShown();
    }

    private void CheckShown()
    {
        if (Phase != OnlinePhase.Flight) return;
        if (_pcs.Where(p => p.Connected && !p.Refused).All(p => p.Shown)) OpenTurn(TurnReason.Shot);
    }

    /// <summary>Host: the next hole has been started (by the countdown, or NEXT HOLE).</summary>
    public void HoleStarted()
    {
        if (Phase is OnlinePhase.HoleOver or OnlinePhase.Open) OpenTurn(TurnReason.NextHole);
    }

    private void OpenTurn(TurnReason reason)
    {
        if (Game.Match is not { } m || Config == null) return;
        Seq++;
        if (m.RoundComplete && reason != TurnReason.Mulligan)
        {
            Broadcast(new TurnOpen(Seq, m.Capture(), reason));
            Phase = OnlinePhase.HoleOver;
            Stop(paused: false);
            return;
        }
        Phase = m.HoleComplete ? OnlinePhase.HoleOver : OnlinePhase.Open;
        Broadcast(new TurnOpen(Seq, m.Capture(), reason));
        WatchTurn();
        if (Code != null) Checkpoint?.Invoke(new OnlineSave(Code, Token, Config, m.Capture(), Seq, DateTime.Now));
    }

    /// <summary>Starts the countdown if the player up has dropped, or their monitor has; stops it if they're fine.</summary>
    private void WatchTurn()
    {
        if (Phase != OnlinePhase.Open || Game.Match is not { } m || Config == null) { _countdown = null; return; }
        var pc = _pcs.FirstOrDefault(p => IsSeatOf(m.Turn, p));
        Wait? why = pc == null || !pc.Connected ? Wait.Dropped : !pc.MonitorLive && RequireMonitors ? Wait.Monitor : null;
        if (why == null) { _countdown = null; return; }
        if (_countdown is { } c && c.Player == m.Turn && c.Why == why) return;
        double secs = why == Wait.Monitor ? MonitorSeconds : DroppedSeconds;
        _countdown = (m.Turn, Clock() + secs, why.Value);
        Broadcast(new AwayNotice(m.Turn, false, secs, $"{Who(Config, m.Turn)} {(why == Wait.Monitor ? "has no launch monitor" : "has dropped out")}: skipped in {secs:0} s"));
    }

    /// <summary>Host: skips the player the countdown is for, now.</summary>
    public void SkipNow()
    {
        if (_countdown is { } c) Skip(c.Player);
    }

    private void Skip(int player)
    {
        _countdown = null;
        if (Game.Match is not { } m || Config == null) return;
        string pc = Config.Seats[player].Pc;
        bool gone = _pcs.FirstOrDefault(p => p.Pc == pc) is not { Connected: true };
        // A PC that's gone takes all its players with it; a dead monitor only the player up.
        for (int i = 0; i < Config.Seats.Length; i++)
            if (i == player || (gone && Config.Seats[i].Pc == pc))
            {
                m.SetAway(i, true);
                Broadcast(new AwayNotice(i, true, null, $"{Who(Config, i)} skipped: picks up until back"));
            }
        Game.MatchChanged();
        OpenTurn(TurnReason.Skip);
    }

    public override void RequestMulligan() => Mulligan(Me, Seq);

    private void Mulligan(PcState pc, int seq)
    {
        if (Game.Match is not { } m || Config == null) return;
        if (Phase is not (OnlinePhase.Open or OnlinePhase.HoleOver)) { Tell(pc, "Wait for the ball to stop"); return; }
        if (seq != Seq || m.LastToPlay is not { } last || !IsSeatOf(last, pc)) { Tell(pc, "Only the player who hit the last shot can take it back, before the next one"); return; }
        if (!Game.Mulligan()) { Tell(pc, "No mulligan to take"); return; }
        Broadcast(new Notice($"Mulligan! {Who(Config, last)} plays it again"));
        OpenTurn(TurnReason.Mulligan);
    }

    protected override void SendAim(Aim aim) => Broadcast(aim);

    // ---- ending ----------------------------------------------------------------------------------

    /// <summary>Ends the round for everyone: finished, or paused to carry on later (<c>online-round.json</c> keeps it).</summary>
    public void Stop(bool paused)
    {
        if (Phase == OnlinePhase.Over) return;
        var m = Game.Match;
        string summary = m?.Summary() ?? "";
        if (paused && m != null && Config != null && Code != null) Checkpoint?.Invoke(new OnlineSave(Code, Token, Config, m.Capture(), Seq, DateTime.Now));
        Phase = OnlinePhase.Over;
        _countdown = null;
        Broadcast(new RoundOver(paused, summary, m?.Capture()));
        Game.RoundOver(paused, summary);
    }

    // ---- timers ----------------------------------------------------------------------------------

    public override void Tick()
    {
        base.Tick();
        double now = Clock();
        if (Phase == OnlinePhase.Loading) CheckLoaded();
        if (Phase == OnlinePhase.Flight && now > _shownBy)
        {
            if (!Me.Shown) Game.FinishShotNow();   // which says it's shown, and so may open the turn
            if (Phase == OnlinePhase.Flight) OpenTurn(TurnReason.Shot);
        }
        if (_countdown is { } c && now >= c.At) Skip(c.Player);
    }

    // ---- the network -----------------------------------------------------------------------------

    protected override void OnLink(LinkEvent e)
    {
        switch (e)
        {
            case LinkEvent.Opened:
                _linkProblem = null;
                BroadcastLobby();
                break;
            case LinkEvent.Lost:
                _linkProblem = "Connection to the relay lost: reconnecting…";
                break;
            case LinkEvent.Closed c:
                if (c.Code == "left") break;
                Phase = OnlinePhase.Over;
                Game.Ended(c.Reason);
                break;
            case LinkEvent.PeerLeft left:
                if (_pcs.FirstOrDefault(p => !p.Host && p.Peer == left.Peer) is { } pc) Dropped(pc);
                break;
            case LinkEvent.Message m:
                if (OnlineMessage.Decode(m.Data) is { } msg) Received(m.From, msg);
                break;
        }
    }

    private void Dropped(PcState pc)
    {
        pc.Connected = false;
        pc.Peer = null;
        if (Phase == OnlinePhase.Lobby)
        {
            _pcs.Remove(pc);
            _seats.RemoveAll(s => s.Pc == pc.Pc);
        }
        BroadcastLobby();
        if (Phase == OnlinePhase.Open) WatchTurn();
        CheckShown();
        CheckLoaded();
    }

    private void Received(int peer, OnlineMessage msg)
    {
        if (msg is Hello hello) { Hello(peer, hello); return; }
        var pc = _pcs.FirstOrDefault(p => !p.Host && p.Peer == peer && p.Connected && !p.Refused);
        if (pc == null) return;   // not introduced yet
        switch (msg)
        {
            case Status s:
                pc.MonitorLive = s.MonitorLive;
                if (s.Fingerprint != null && Config != null)
                {
                    pc.Fingerprint = s.Fingerprint;
                    if (Me.Fingerprint != null && s.Fingerprint != Me.Fingerprint)
                    {
                        Refuse(pc, "Your course data is different from the host's: install the same version of the app.");
                        CheckShown();
                    }
                }
                BroadcastLobby();
                CheckLoaded();
                if (Phase == OnlinePhase.Open) WatchTurn();
                break;
            case ShotRequest r: Accept(r, pc); break;
            case ShotShown shown when shown.Seq == Seq && Phase == OnlinePhase.Flight:
                pc.Shown = true;
                CheckShown();
                break;
            case Aim a when Phase == OnlinePhase.Open && a.Seq == Seq && Game.Match is { } m && a.Player == m.Turn && IsSeatOf(a.Player, pc)
                            && double.IsFinite(a.Heading) && double.IsFinite(a.Power):
                Broadcast(a);
                Game.ShowAim(a);
                break;
            case MulliganRequest mr: Mulligan(pc, mr.Seq); break;
            case Bye:
                Dropped(pc);
                if (Config != null && Game.Match is { } match && Phase is not (OnlinePhase.Lobby or OnlinePhase.Over))
                {
                    // Left for good: no countdown, straight to away.
                    bool skipNow = Phase == OnlinePhase.Open && IsSeatOf(match.Turn, pc);
                    for (int i = 0; i < Config.Seats.Length; i++)
                        if (Config.Seats[i].Pc == pc.Pc && i != match.Turn) match.SetAway(i, true);
                    if (skipNow) Skip(match.Turn); else Game.MatchChanged();
                }
                break;
        }
    }

    private void Hello(int peer, Hello h)
    {
        var known = _pcs.FirstOrDefault(p => p.Pc == h.Pc && !p.Host);
        if (h.Protocol != OnlineProtocol.Version || h.AppVersion != AppVersion)
        {
            Send(new Refused($"Different versions: the host has {AppVersion}, this PC {h.AppVersion}. Install the same build on both."), peer);
            return;
        }
        if (known is { Refused: true }) { Send(new Refused("The host has turned this PC away for this round."), peer); return; }
        if (known == null)
        {
            if (Phase != OnlinePhase.Lobby) { Send(new Refused("The round has already started."), peer); return; }
            if (h.Players.Length == 0) { Send(new Refused("No players at this PC."), peer); return; }
            if (_seats.Count + h.Players.Length > Match.MaxPlayers) { Send(new Refused($"No room: {Match.MaxPlayers} players at most, and {_seats.Count} are in."), peer); return; }
            known = new PcState(h.Pc, host: false);
            _pcs.Add(known);
        }
        foreach (var other in _pcs.Where(p => p != known && p.Peer == peer)) { other.Peer = null; other.Connected = false; }
        known.Peer = peer;
        known.Connected = true;
        known.MonitorLive = h.MonitorLive;
        if (Phase == OnlinePhase.Lobby)
        {
            int at = _seats.FindIndex(s => s.Pc == h.Pc);
            _seats.RemoveAll(s => s.Pc == h.Pc);
            var add = Dedupe(h.Players.Take(Match.MaxPlayers).Select((s, i) => new Seat(h.Pc, CleanName(s.Name, _seats.Count + i), s.LeftHanded)), _seats);
            _seats.InsertRange(at < 0 ? _seats.Count : Math.Min(at, _seats.Count), add);
            BroadcastLobby();
            return;
        }
        // Back during the round: everything as it stands, and their players play again from the next hole.
        BroadcastLobby();
        if (Config == null || Game.Match is not { } m) return;
        known.Fingerprint ??= Me.Fingerprint;
        known.Shown = true;
        bool changed = false;
        for (int i = 0; i < Config.Seats.Length; i++)
        {
            if (Config.Seats[i].Pc != h.Pc) continue;
            bool wasAway = m.IsAway(i);
            if (wasAway) { m.SetAway(i, false); changed = true; }
            Broadcast(new AwayNotice(i, false, null, $"{Who(Config, i)} is back{(wasAway ? ": plays from the next hole" : "")}"));
        }
        Send(new Sync(Config, Seq, m.Capture(), Phase == OnlinePhase.Flight, Phase == OnlinePhase.Flight ? _lastResult : null), peer);
        if (changed)
        {
            Game.MatchChanged();
            // Everyone else in step too (same turn; during a shot the next turn brings it).
            if (Phase is OnlinePhase.Open or OnlinePhase.HoleOver) Broadcast(new TurnOpen(Seq, m.Capture(), TurnReason.Resync));
        }
        if (Phase == OnlinePhase.Open) WatchTurn();
    }

    private void Refuse(PcState pc, string reason)
    {
        if (pc.Peer is int peer) Send(new Refused(reason), peer);
        pc.Refused = true;
        pc.Connected = false;
        Game.Notice($"{Label(pc)} couldn't join: {reason}");
    }

    private void Tell(PcState pc, string text, Guid? shot = null)
    {
        if (pc.Host) Game.Notice(text);
        else if (pc.Peer is int peer) Send(new Notice(text, shot), peer);
    }

    private void Broadcast(OnlineMessage m)
    {
        if (_pcs.Any(p => !p.Host && p.Connected && !p.Refused)) Link.Send(m.Encode());
    }

    private void Send(OnlineMessage m, int peer) => Link.Send(m.Encode(), peer);
}
