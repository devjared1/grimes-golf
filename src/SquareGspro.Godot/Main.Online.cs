using System.Collections.Concurrent;
using System.Text.Json;
using Godot;
using SquareGspro.Core;
using SquareGspro.Core.Course;
using SquareGspro.Core.Storage;
using SquareGspro.Online.Protocol;
using SquareGspro.Online.Session;
using SquareGspro.Online.Transport;
using SquareGspro.Physics;

namespace SquareGspro.Range3D;

/// <summary>
/// Online play (roadmap 1.2): friends at different houses play one round through a relay. The host's PC is the only
/// one that plays shots; every other PC is sent each outcome with the whole match after it, and animates that with
/// the same code as a hot-seat round (<see cref="PlayCourseShot"/> takes the outcome instead of working it out).
/// The sessions (<c>SquareGspro.Online</c>) hold the rules of who may hit when; this file connects them to the game.
/// <para>
/// Offline nothing here runs: <see cref="_online"/> is null and every guard in the other files is a no-op.
/// </para>
/// Development switches (debug builds only): <c>--online</c> shows Play Online on the main menu (it is always shown
/// in debug builds for now), <c>--relay=ws://localhost:5080/ws</c> fills in the relay, <c>--data=folder</c> keeps
/// this run's settings and rounds apart (two copies on one PC), <c>--no-bluetooth</c>, <c>--fake-monitor</c>
/// (HIT SHOT stands in for a monitor; <c>--fake-monitor=session.jsonl</c> plays a saved session's shots), and
/// <c>--auto-play</c> (the fake monitor hits by itself whenever it's this PC's turn).
/// </summary>
public partial class Main
{
    /// <summary>The online round this PC is in (null offline).</summary>
    private OnlineSession? _online;
    private HostSession? OnlineHost => _online as HostSession;
    private OnlineBridge? _bridge;
    /// <summary>Link events, handed over on the game's thread; they wait while a course is loading (it would drop shots).</summary>
    private readonly ConcurrentQueue<Action> _onlineInbox = new();
    /// <summary>This PC's own physics tuning while playing with the host's (put back on leaving).</summary>
    private PhysicsTuning? _ownTuning;
    private bool _wasLocalTurn;

    // Development switches (debug builds only).
    private string? _devRelay;
    private bool _fakeMonitor, _autoPlay, _noBluetooth;
    private Queue<RangeShot>? _fakeShots;
    private double _autoPlayIn = -1;

    private static double OnlineClock() => Time.GetTicksMsec() / 1000.0;
    private bool MonitorLiveOnline => _monitorLive || _fakeMonitor;
    private static string AppVersion => Hud.Version.Length > 0 ? Hud.Version : "dev";

    /// <summary>Reads the development switches for online play (only in debug builds, so the release export ignores them).</summary>
    private void SetupOnlineDevTools(string[] args)
    {
        _hud.MainMenu.SetOnlineAvailable(OS.IsDebugBuild());
        _hud.Online.HostPressed += relay => StartOnline(relay, host: true, null, null);
        _hud.Online.JoinPressed += (relay, code) => StartOnline(relay, host: false, code, null);
        _hud.Online.RejoinPressed += () => StartOnline(_settings.RelayUrl ?? "", host: false, _settings.RejoinCode, null);
        _hud.Online.ResumeHostPressed += () =>
        {
            if (_data?.LoadOnlineRound<OnlineSave>() is { } save) StartOnline(_settings.RelayUrl ?? "", host: true, save.Code, save);
        };
        _hud.Online.StartPressed += () => { if (OnlineHost?.Start(ActivePhysics.Tuning.ToJson()) == false) _hud.Toast("Not everyone's ready yet", UiTheme.Warn); };
        _hud.Online.LeavePressed += () => LeaveOnline("Left the lobby.");
        _hud.Online.BackPressed += () => { _hud.Online.Close(); ShowMainMenu(); };
        _hud.Online.CourseChosen += i =>
        {
            _courseIndex = i;
            OnlineHost?.Configure(CourseLibrary.All[i].Name, _settings.Setup, _settings.Rules);
        };
        _hud.Online.SeatMoved += (from, to) => OnlineHost?.MoveSeat(from, to);
        _hud.MainMenu.OnlinePressed += OpenOnline;

        if (!OS.IsDebugBuild()) return;
        string? Arg(string name) => args.FirstOrDefault(a => a.StartsWith(name + "="))?[(name.Length + 1)..];
        _devRelay = Arg("--relay");
        _noBluetooth = args.Contains("--no-bluetooth");
        _autoPlay = args.Contains("--auto-play");
        _fakeMonitor = _autoPlay || args.Any(a => a == "--fake-monitor" || a.StartsWith("--fake-monitor="));
        if (Arg("--fake-monitor") is { } file) _fakeShots = ReadSessionShots(file);
    }

    /// <summary>The data folder: <c>--data=folder</c> in debug builds (two copies on one PC), the app's own otherwise.</summary>
    private static string DataFolderFor(string[] args) =>
        OS.IsDebugBuild() && args.FirstOrDefault(a => a.StartsWith("--data="))?[7..] is { Length: > 0 } folder
            ? ProjectSettings.GlobalizePath(folder) : OS.GetUserDataDir();

    /// <summary>The monitor shots of a saved session (<c>sessions/*.jsonl</c>), to drive the fake monitor.</summary>
    private Queue<RangeShot> ReadSessionShots(string path)
    {
        var q = new Queue<RangeShot>();
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (line.Length == 0) continue;
                try
                {
                    if (JsonSerializer.Deserialize<ShotRecord>(line, GolfData.Json) is { Demo: false, Replay: false } r)
                        q.Enqueue(new RangeShot(r.BallMph, r.LaunchDeg, r.DirectionDeg, r.SpinRpm, r.SpinAxisDeg, r.ClubMph, null) { Source = ShotSource.Monitor });
                }
                catch (JsonException) { }
            }
            Log($"Fake monitor: {q.Count} shots from {path}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log($"Fake monitor: couldn't read {path}: {ex.Message}"); }
        return q;
    }

    // ---- the online panel ----------------------------------------------------------------------

    private void OpenOnline()
    {
        _hud.MainMenu.Close();
        string? rejoin = _settings.RejoinCode is { } code && _settings.RejoinUntil > DateTime.Now ? $"Rejoin lobby {code}" : null;
        string? resume = _data?.LoadOnlineRound<OnlineSave>() is { } s && s.Saved > DateTime.Now.AddHours(-12)
            ? $"Carry on your round at {s.Config.Course} ({string.Join(", ", s.Config.Seats.Select(x => x.Name))}), lobby {s.Code}" : null;
        _hud.Online.OpenHome(_settings.RelayUrl ?? _devRelay ?? "", rejoin, resume,
            MonitorLiveOnline ? null : "Connect your launch monitor first: online rounds are played with real shots.");
    }

    private static bool TryRelay(string text, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("wss" or "ws")) return false;
        // Plain ws:// only for a relay on this machine or the local network (development).
        if (u.Scheme == "ws" && !(u.IsLoopback || u.Host.StartsWith("192.168.") || u.Host.StartsWith("10.") || OS.IsDebugBuild())) return false;
        uri = u;
        return true;
    }

    /// <summary>Hosts a lobby (or carries on <paramref name="resume"/>), or joins the one with <paramref name="code"/>.</summary>
    private void StartOnline(string relay, bool host, string? code, OnlineSave? resume)
    {
        if (_online != null || _loading) return;
        if (!TryRelay(relay, out var uri)) { _hud.Online.SetNote("Enter the relay's address, like wss://your-relay.fly.dev/ws"); return; }
        if (!host && !(code is { Length: 5 })) { _hud.Online.SetNote("A lobby code is 5 letters and numbers."); return; }
        string token = resume?.Token ?? _settings.OnlineToken ?? Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        SaveSettings(_settings with { RelayUrl = relay.Trim(), OnlineToken = _settings.OnlineToken ?? token });

        var link = new RelayClient(RelayClient.WebSocketTo(uri), host ? RelayRole.Host : RelayRole.Join, code, token);
        _bridge = new OnlineBridge(this);
        void PostOnline(Action a) => _onlineInbox.Enqueue(a);
        if (host)
        {
            var h = new HostSession(link, _bridge, PostOnline, OnlineClock, token, AppVersion, _settings.Golfers, MonitorLiveOnline);
            h.Checkpoint += s => _data?.SaveOnlineRound(s);
            h.Configure(CourseName, _settings.Setup, _settings.Rules);
            _online = h;
        }
        else _online = new ClientSession(link, _bridge, PostOnline, OnlineClock, token, AppVersion, _settings.Golfers, MonitorLiveOnline);
        _hud.Online.OpenLobby(host, "Connecting to the relay…");
        link.Start();
        if (resume != null) OnlineHost!.Resume(resume);
        Log(host ? "Online: hosting a lobby." : $"Online: joining lobby {code}.");
    }

    /// <summary>
    /// Leaves online play: a host still in a round pauses it for everyone (to carry on later); a friend tells the
    /// host and keeps their scorecard. Back to the main menu, with this PC's own tuning.
    /// </summary>
    private void LeaveOnline(string? why = null)
    {
        var session = _online;
        if (session == null) return;
        if (_outcome != null) FinishShot();
        if (session is HostSession h && h.Phase is not (OnlinePhase.Lobby or OnlinePhase.Over)) h.Stop(paused: true);
        if (session is ClientSession c && c.Phase != OnlinePhase.Over)
        {
            if (c.Phase != OnlinePhase.Lobby) KeepOnlineScorecard();
            c.Leave();
            if (c.Phase == OnlinePhase.Over && c.EndedBecause == "Left the round.") SaveSettings(_settings with { RejoinCode = null, RejoinUntil = null });
        }
        _online = null;
        _bridge = null;
        // Let the goodbye go out before the connection closes.
        GetTree().CreateTimer(0.5).Timeout += session.Dispose;
        while (_onlineInbox.TryDequeue(out _)) { }
        RestoreOwnTuning();
        _hud.SetOnlineLine(null, false);
        _hud.Online.Close();
        _match = null;
        if (_courseMode) EnterRange();
        ShowMainMenu();
        if (_hud.Overlay.IsOpen) _hud.Overlay.Close();
        if (why != null) Log($"Online: {why}");
    }

    /// <summary>A friend's copy of the scorecard, filed with their rounds (the host's PC keeps the round itself).</summary>
    private void KeepOnlineScorecard()
    {
        if (_match != null && _courseDef != null)
            _data?.FileRound(SavedRound.From(_match, $"{_courseDef.Name} (online)", _roundStarted, _game));
    }

    private void ApplyHostTuning(string json)
    {
        _ownTuning ??= ActivePhysics.Tuning;
        TunerWindow.FreezeStartup = true;   // the host's numbers mustn't end up in this PC's startup.json
        try { ActivePhysics.Apply(PhysicsTuning.FromJson(json)); }
        catch (Exception ex) { Log($"Online: couldn't use the host's physics tuning ({ex.Message}); playing with this PC's."); }
    }

    private void RestoreOwnTuning()
    {
        if (_ownTuning != null) ActivePhysics.Apply(_ownTuning);
        _ownTuning = null;
        // Unfrozen once the tuner has let go of the save the change asked for.
        GetTree().CreateTimer(1).Timeout += () => { if (_online == null) TunerWindow.FreezeStartup = false; };
    }

    // ---- every frame ---------------------------------------------------------------------------

    private void ProcessOnline(double delta)
    {
        var s = _online;
        if (s == null) return;
        if (!_loading) while (_online == s && _onlineInbox.TryDequeue(out var a)) a();
        if (_online != s) return;
        s.SetMonitor(MonitorLiveOnline);
        s.Tick();

        string? banner = s.Banner;
        if (_hud.Online.InLobby) { if (banner != null) _hud.Online.SetStatus(banner); }
        else if (_courseMode) _hud.SetOnlineLine(banner ?? s.TurnText, banner != null);

        if (!_courseMode) return;
        bool local = s.IsLocalTurn;
        if (local && !_wasLocalTurn) SendClubToMonitor();   // the monitor may have been left on someone else's club
        _wasLocalTurn = local;
        // HIT SHOT doesn't hit online (a real ball does); NEXT HOLE stays for the host.
        if (!(_round is { Holed: true } && s.IsHost))
        {
            _hud.Shot.Hit.Text = _fakeMonitor ? "FAKE MONITOR" : "HIT ON YOUR MONITOR";
            _hud.Shot.Hit.Disabled = !(_fakeMonitor && local && _outcome == null);
        }
        if (_autoPlay && local && _outcome == null && !_player.IsPlaying && !_camera.Flying)
        {
            if (_autoPlayIn < 0) _autoPlayIn = 2;
            else if ((_autoPlayIn -= delta) < 0) { _autoPlayIn = -1; FakeMonitorShot(); }
        }
        else _autoPlayIn = -1;
    }

    /// <summary>The fake monitor's shot: the next from the saved session, or one sized for the shot planned.</summary>
    private void FakeMonitorShot()
    {
        var shot = _fakeShots is { Count: > 0 } q ? q.Dequeue() : CourseDemoShot();
        if (shot != null) ShowShot(shot with { IsDemo = false, Source = ShotSource.Monitor });
    }

    /// <summary>The Ready light online: only the PC whose turn it is gets "Ready".</summary>
    private bool OnlineReady()
    {
        if (_online == null || !_courseMode) return false;
        if (_online.IsLocalTurn) return false;   // the normal light: ball placed, monitor ready…
        _hud.Shot.SetReady(false, _online.TurnText);
        return true;
    }

    // ---- the round, as the sessions tell it ----------------------------------------------------

    /// <summary>Builds the online round's course and match (behind the loading screen), then tells the session.</summary>
    private async void BuildOnlineRound(RoundConfig config, MatchSnapshot? snapshot)
    {
        var session = _online;
        if (session == null) return;
        int index = CourseLibrary.All.ToList().FindIndex(e => e.Name == config.Course);
        if (index < 0) { OnlineFailed($"This PC doesn't have the course {config.Course}. Install the same version of the app as the host."); return; }
        if (_outcome != null) FinishShot();
        _courseIndex = index;
        _game = config.Setup;
        if (!session.IsHost)
        {
            ApplyHostTuning(config.TuningJson);
            SaveSettings(_settings with { RejoinCode = session.Code, RejoinUntil = config.Started.AddHours(12) });
        }
        string? fingerprint = null, problem = null;
        await WithLoadingScreen(index, () =>
        {
            var entry = CourseLibrary.All[index];
            try { _courseDef = entry.Load(); }
            catch (Exception ex) { problem = $"Couldn't load {entry.Name}: {ex.Message}"; return; }
            _course?.QueueFree();
            _course = null;
            _hud.SetCredit(_courseDef.Credit);
            var holes = config.HoleNumbers.Length > 0
                ? config.HoleNumbers.Select(n => _courseDef.Holes.FirstOrDefault(h => h.Number == n)).OfType<Hole>().ToList()
                : config.Setup.Pick(_courseDef.Holes);
            if (holes.Count == 0 || (config.HoleNumbers.Length > 0 && holes.Count != config.HoleNumbers.Length))
            {
                problem = "This PC's course has different holes from the host's. Install the same version of the app.";
                return;
            }
            try
            {
                _match = new Match(holes, _courseDef.Terrain, config.Golfers, config.Rules, config.Setup.Type);
                if (snapshot != null) _match.Restore(snapshot); else _match.StartHole(0);
            }
            catch (ArgumentException ex) { problem = $"Couldn't set up the round: {ex.Message}"; return; }
            _roundStarted = config.Started;
            _lastRecord = null;
            ForgetLastShot();
            _hud.Online.Close();
            _hud.ShowMainMenu(false);
            EnterCourse();
            fingerprint = CourseFingerprint.Of(holes, _courseDef.Terrain);
        });
        if (_online != session) return;
        if (fingerprint == null) { OnlineFailed(problem ?? "Couldn't load the course."); return; }
        Log($"Online: {config.Course}, {string.Join(", ", config.Seats.Select(s => s.Name))}. {config.Setup.Summary(config.Seats.Length)}.");
        session.RoundBuilt(fingerprint);
    }

    /// <summary>Friends: the host's shot. The match goes to the snapshot at impact and the shot is animated as usual.</summary>
    private void ShowOnlineShot(ShotResult r)
    {
        if (!_courseMode || _match == null) return;
        _camera.SkipFlyover();
        EndLandingView();
        _hud.Shot.ResetShape();
        PlayCourseShot(r.Shot, online: new OnlineShot(r.Player, r.Club, r.Heading, r.Putt, r));
    }

    /// <summary>
    /// A new turn (or a resync): if the host's match isn't this one, this one becomes it, and the next shot is set up
    /// again (a new hole, a mulligan, a player skipped).
    /// </summary>
    private void OpenOnlineTurn(TurnOpen t)
    {
        var match = _match;
        if (match == null || !_courseMode) return;
        if (_outcome != null) FinishShot();   // the host has moved on: catch up
        var before = match.Capture();
        if (before.SameAs(t.Snapshot) && t.Reason == TurnReason.Shot) return;
        int hole = match.HoleIndex;
        match.Restore(t.Snapshot);
        if (match.HoleIndex != hole) { StartHole(match.HoleIndex, fresh: false); return; }
        if (t.Reason == TurnReason.Mulligan) { _hud.HideResult(); _hud.ShowScorecard(false); }
        _player.PlaceBall(RestingBall(match.Current));
        PrepareNextShot(snapCamera: true);
    }

    /// <summary>The player up on another PC is aiming: the same aim, club and power here (the monitor isn't told).</summary>
    private void ShowOnlineAim(Aim a)
    {
        if (!_courseMode || _match is not { } m || m.Turn != a.Player || _outcome != null || _online?.IsLocalTurn == true) return;
        _aimHeading = a.Heading;
        _hud.SetClub(a.Club);   // raises ClubChanged; the monitor write is skipped while it isn't this PC's turn
        _hud.Shot.SetPower(a.Power);
        UpdateAim();
    }

    private void OnlineRoundOver(bool paused, string summary)
    {
        var session = _online;
        if (session == null) return;
        if (!paused)
        {
            if (_match != null && _courseDef != null) _data?.FileRound(SavedRound.From(_match, $"{_courseDef.Name} (online)", _roundStarted, _game));
            if (session.IsHost) _data?.ClearOnlineRound();
            else SaveSettings(_settings with { RejoinCode = null, RejoinUntil = null });
        }
        string message = paused
            ? (session.IsHost ? "The round is kept: carry it on from Play Online within 12 hours, and your friends rejoin with the same code."
                              : "The host has paused the round. Rejoin from Play Online when they carry it on (within 12 hours).")
            : $"Final: {summary}. The scorecard is saved with your rounds.";
        _hud.Overlay.Open(paused ? "Round paused" : "Round over", message,
            new MenuItem[] { new("Main Menu", () => LeaveOnline(), Primary: true), new("Scorecard", () => _hud.ShowScorecard(true)) });
    }

    private void OnlineFailed(string reason)
    {
        Log($"Online: {reason}");
        if (_online is ClientSession { Phase: not OnlinePhase.Lobby }) KeepOnlineScorecard();
        _hud.Overlay.Open("Online play stopped", reason, new MenuItem[] { new("Main Menu", () => LeaveOnline(), Primary: true) });
    }

    /// <summary>The in-game menu online: no hole jumping, player changes, restarts, tuner or replay; leave, pause or end instead.</summary>
    private void AddOnlineMenuItems(List<MenuItem> items)
    {
        var s = _online!;
        items.Add(new("Scorecard", () => _hud.ShowScorecard(true), "[S, gamepad Y]"));
        items.Add(new("Settings", OpenSettings, "Text size, graphics, monitor"));
        if (OnlineHost is { } h)
        {
            if (h.CanSkip) items.Add(new("Skip Now", h.SkipNow, "Skip the player who has dropped out, without waiting", Primary: true));
            items.Add(new("Pause the Round", () => ConfirmOnline("Pause the round?", "Everyone stops here. Carry it on later from Play Online; friends rejoin with the same code.", "Pause", () => h.Stop(paused: true))));
            items.Add(new("End the Round", () => ConfirmOnline("End the round for everyone?", "It finishes here, scores as they stand.", "End It", () => h.Stop(paused: false))));
        }
        else
        {
            string tip = s is ClientSession { HostGone: true } ? "The host is gone: your scorecard is kept" : "Your players are skipped from here on; your scorecard is kept";
            items.Add(new("Leave the Round", () => ConfirmOnline("Leave the round?", tip, "Leave", () => LeaveOnline("Left the round."))));
        }
    }

    private void ConfirmOnline(string title, string message, string yes, Action action) =>
        _hud.Overlay.Open(title, message, new MenuItem[] { new(yes, action, Primary: true), new("Cancel", null) });

    /// <summary>The shot details the host plays a request with, or a friend shows a result with.</summary>
    private sealed record OnlineShot(int Player, string Club, double Heading, bool Putt, ShotResult? Result);

    /// <summary>The game as the online sessions see it.</summary>
    private sealed class OnlineBridge(Main main) : IOnlineGame
    {
        public Match? Match => main._match;
        public void BuildRound(RoundConfig config, MatchSnapshot? snapshot) => main.BuildOnlineRound(config, snapshot);
        public void ShowShot(ShotResult shot) => main.ShowOnlineShot(shot);
        public void OpenTurn(TurnOpen turn) => main.OpenOnlineTurn(turn);
        public void ShowAim(Aim aim) => main.ShowOnlineAim(aim);
        public void Notice(string text) { main._hud.Toast(text, UiTheme.Warn); main.Log($"Online: {text}"); }
        public void LobbyChanged(LobbyState lobby) => main._hud.Online.SetLobby(lobby, main._online?.IsHost == true ? main._courseIndex : -1);
        public void RoundOver(bool paused, string summary) => main.OnlineRoundOver(paused, summary);
        public void Ended(string reason) => main.OnlineFailed(reason);

        public OutcomeData? PlayShot(ShotRequest r)
        {
            if (!main._courseMode) return null;
            main._camera.SkipFlyover();
            main.EndLandingView();
            main._hud.Shot.ResetShape();
            var o = main.PlayCourseShot(r.Shot, online: new OnlineShot(r.Player, r.Club, r.Heading, r.Putt, null));
            return o == null ? null : OutcomeData.Of(o);
        }

        public void FinishShotNow() { if (main._outcome != null) main.FinishShot(); }

        public void MatchChanged()
        {
            if (main._outcome != null || main._match == null || !main._courseMode) return;
            main._player.PlaceBall(main.RestingBall(main._match.Current));
            main.PrepareNextShot(snapCamera: true);
        }

        public bool Mulligan() => main.TryMulligan();
    }
}
