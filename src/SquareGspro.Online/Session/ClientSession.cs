using SquareGspro.Core;
using SquareGspro.Core.Course;
using SquareGspro.Online.Protocol;
using SquareGspro.Online.Transport;

namespace SquareGspro.Online.Session;

/// <summary>
/// A friend's PC: introduces its players, sends its monitor's shots when it's their turn, and shows what the host's
/// PC says happened. It never plays a shot itself. A shot sent and not yet answered is kept and sent again (same
/// id) after a reconnection, so a real ball isn't lost; the host plays it once.
/// </summary>
public sealed class ClientSession : OnlineSession
{
    private readonly Seat[] _players;
    private bool _monitorLive;
    private string? _fingerprint;
    private ShotRequest? _pending;
    private Sync? _syncWaiting;
    private bool _connected, _hostPresent = true;
    private double? _hostLeftAt;
    private (int Player, double At)? _countdown;
    private string? _ended;

    public ClientSession(ILobbyLink link, IOnlineGame game, Action<Action> post, Func<double> clock, string token, string appVersion,
                         IReadOnlyList<Golfer> players, bool monitorLive)
        : base(link, game, post, clock, token, appVersion)
    {
        _players = players.Take(Match.MaxPlayers).Select((g, i) => new Seat(Pc, CleanName(g.Name, i), g.LeftHanded)).ToArray();
        _monitorLive = monitorLive;
    }

    public override bool IsHost => false;

    /// <summary>The host has been gone long enough to give up on: the game offers the main menu (scores kept).</summary>
    public bool HostGone => _hostLeftAt is { } t && Clock() - t > HostGoneSeconds;

    /// <summary>Why the session ended (refused, lobby closed), null while it's going.</summary>
    public string? EndedBecause => _ended;

    /// <summary>A shot has been sent and the host hasn't answered yet.</summary>
    public bool ShotPending => _pending != null;

    public override string? Banner
    {
        get
        {
            if (_ended != null) return null;
            if (!_connected) return "Connection lost: reconnecting…";
            if (_hostLeftAt is { } t)
            {
                double gone = Clock() - t;
                return gone > HostGoneSeconds ? "The host is still gone. Your scores are kept: leave from the menu, or keep waiting"
                                              : $"The host has disconnected: waiting for them ({Math.Max(0, (int)(HostGoneSeconds - gone))} s)";
            }
            if (_countdown is { } c && c.At > Clock())
                return $"{Who(Config, c.Player)} is having trouble: skipped in {(int)Math.Ceiling(c.At - Clock())} s";
            return null;
        }
    }

    // ---- from the game ---------------------------------------------------------------------------

    public override void MonitorShot(RangeShot shot, string club, double heading, bool putt)
    {
        if (Game.Match is not { } m) return;
        if (_pending != null) { Game.Notice("Still sending your last shot…"); return; }
        if (!IsLocalTurn)
        {
            Game.Notice(Phase == OnlinePhase.Flight ? "Wait for the last shot to finish: that one didn't count"
                       : Phase == OnlinePhase.Open ? $"It's {Who(Config, m.Turn)}'s turn: that shot didn't count"
                       : "No shot to play right now: that one didn't count");
            return;
        }
        _pending = new ShotRequest(Guid.NewGuid(), Seq, m.Turn, shot with { Source = ShotSource.Monitor }, club, heading, putt);
        Send(_pending);
    }

    public override void RoundBuilt(string fingerprint)
    {
        _fingerprint = fingerprint;
        Send(new Status(_monitorLive, fingerprint));
        if (_syncWaiting is { } s) { _syncWaiting = null; ApplySync(s); }
        else if (Phase == OnlinePhase.Loading) Phase = OnlinePhase.Lobby;   // built; the host opens the first turn
    }

    public override void ShotShown()
    {
        if (Phase == OnlinePhase.Flight) Send(new ShotShown(Seq));
    }

    public override void RequestMulligan() => Send(new MulliganRequest(Seq));

    public override void SetMonitor(bool live)
    {
        if (_monitorLive == live) return;
        _monitorLive = live;
        Send(new Status(live, _fingerprint));
    }

    protected override void SendAim(Aim aim) => Send(aim);

    /// <summary>Leaves the round for good (the host skips this PC's players from now on).</summary>
    public void Leave()
    {
        Send(new Bye());
        _ended ??= "Left the round.";
        Phase = OnlinePhase.Over;
    }

    public override void Tick()
    {
        base.Tick();
        if (_countdown is { } c && c.At <= Clock()) _countdown = null;
    }

    // ---- the network -----------------------------------------------------------------------------

    protected override void OnLink(LinkEvent e)
    {
        switch (e)
        {
            case LinkEvent.Opened o:
                _connected = true;
                _hostPresent = o.HostPresent;
                _hostLeftAt = o.HostPresent ? null : _hostLeftAt ?? Clock();
                if (o.HostPresent) SayHello();
                break;
            case LinkEvent.Lost:
                _connected = false;
                break;
            case LinkEvent.Closed c:
                if (c.Code == "left") break;
                End(c.Code == "not-found" ? "No lobby with that code. Check it with the host." : c.Reason);
                break;
            case LinkEvent.HostLeft:
                _hostPresent = false;
                _hostLeftAt ??= Clock();
                break;
            case LinkEvent.HostBack:
                _hostPresent = true;
                _hostLeftAt = null;
                SayHello();
                break;
            case LinkEvent.Message m when m.From == 0:
                if (OnlineMessage.Decode(m.Data) is { } msg) Received(msg);
                break;
        }
    }

    private void SayHello() => Send(new Hello(OnlineProtocol.Version, AppVersion, Pc, _players, _monitorLive));

    private void Received(OnlineMessage msg)
    {
        if (_ended != null) return;
        switch (msg)
        {
            case LobbyUpdate l:
                Lobby = l.State;
                Game.LobbyChanged(l.State);
                break;
            case Refused r:
                End(r.Reason);
                break;
            case RoundStart s:
                Config = s.Config;
                Seq = s.Seq;
                Phase = OnlinePhase.Loading;
                _pending = null;
                Game.BuildRound(s.Config, s.Snapshot);
                break;
            case Sync s:
                bool sameRound = Config != null && Config.Started == s.Config.Started && Config.Course == s.Config.Course && Game.Match != null && Phase != OnlinePhase.Loading;
                Config = s.Config;
                if (sameRound) ApplySync(s);
                else
                {
                    _syncWaiting = s;
                    Phase = OnlinePhase.Loading;
                    Game.BuildRound(s.Config, s.Snapshot);
                }
                break;
            case ShotResult r when Config != null && Phase != OnlinePhase.Loading:
                if (_pending?.Id == r.Id) _pending = null;
                Seq = r.Seq;
                Phase = OnlinePhase.Flight;
                _countdown = null;
                Game.ShowShot(r);
                break;
            case TurnOpen t when Config != null && Phase != OnlinePhase.Loading:
                Seq = t.Seq;
                if (_pending != null && _pending.Seq != Seq) _pending = null;   // that turn's gone
                _countdown = null;
                Game.OpenTurn(t);
                Phase = Game.Match is { HoleComplete: true } ? OnlinePhase.HoleOver : OnlinePhase.Open;
                break;
            case Aim a when a.Seq == Seq && !IsLocalSeat(a.Player):
                Game.ShowAim(a);
                break;
            case AwayNotice n:
                _countdown = n.CountdownSeconds is { } secs ? (n.Player, Clock() + secs) : null;
                Game.Notice(n.Text);
                break;
            case Notice n:
                if (n.Shot != null && n.Shot == _pending?.Id) _pending = null;
                Game.Notice(n.Text);
                break;
            case RoundOver o:
                if (o.Snapshot != null && Game.Match != null) Game.OpenTurn(new TurnOpen(Seq, o.Snapshot, TurnReason.Resync));
                Phase = OnlinePhase.Over;
                _pending = null;
                Game.RoundOver(o.Paused, o.Summary);
                break;
        }
    }

    /// <summary>Back in step with the host: the match as it stands, and the shot sent before the blip again if it's still that turn.</summary>
    private void ApplySync(Sync s)
    {
        Seq = s.Seq;
        Game.OpenTurn(new TurnOpen(s.Seq, s.Snapshot, TurnReason.Resync));
        Phase = s.Animating ? OnlinePhase.Flight : Game.Match is { HoleComplete: true } ? OnlinePhase.HoleOver : OnlinePhase.Open;
        if (_pending is { } p)
        {
            if (s.LastShot?.Id == p.Id || p.Seq != Seq) _pending = null;   // played already, or that turn's gone
            else if (Phase == OnlinePhase.Open) Send(p);
        }
    }

    private void End(string reason)
    {
        if (_ended != null) return;
        _ended = reason;
        Phase = OnlinePhase.Over;
        Game.Ended(reason);
    }

    private void Send(OnlineMessage m)
    {
        if (_ended == null || m is Bye) Link.Send(m.Encode());
    }
}
