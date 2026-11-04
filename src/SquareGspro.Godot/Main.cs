using System.Collections.Concurrent;
using Godot;
using SquareGspro.Core;
using SquareGspro.Core.Course;
using SquareGspro.Core.Storage;
using SquareGspro.Online.Session;
using SquareGspro.Physics;

namespace SquareGspro.Range3D;

/// <summary>
/// Root of the app: builds the world, camera and HUD, and connects the HUD to the Square. The rest of the class is
/// split by topic: <c>Main.Monitor.cs</c> (Bluetooth), <c>Main.Shots.cs</c> (shots and the range),
/// <c>Main.Course.cs</c> (rounds), <c>Main.Menus.cs</c> (the main menu and in-game menu),
/// <c>Main.Controls.cs</c> (keyboard, numpad, gamepad, clicker), <c>Main.Settings.cs</c> (the settings screen) and
/// <c>Main.DevTools.cs</c>.
/// <para>
/// Every shot raises <see cref="ShotFinished"/> with its full context; saving data hooks into it.
/// Bluetooth events arrive on background threads and are marshalled to the main thread through <see cref="Post"/>.
/// </para>
/// The app opens on the main menu; everything a guest needs is on screen or in the settings. Command-line switches
/// (all optional): <c>--autoconnect</c> connects to the first Square found even with auto-connect off;
/// <c>--range</c> skips the menu for the driving range; <c>--course</c> skips it for the course (the unfinished round
/// if there is one); <c>--hole=N</c> starts on hole N. Development runs (below) skip the menu too.
/// Development: <c>--demo-shots=N</c> hits N demo shots after start-up,
/// and <c>--screenshot=file.png</c> saves the window to a PNG (after <c>--screenshot-delay=seconds</c>, default 4, or
/// once the demo shots have finished) and quits; <c>--monitor-drawer</c> opens the launch monitor drawer;
/// <c>--settings</c> opens the settings; <c>--menu</c> opens the in-game menu; <c>--setup-page</c> opens New Game's
/// Game Setup page; <c>--game=Scramble</c>, <c>--holes=Back9</c>, <c>--wind=Breezy</c> and <c>--weather=Wet</c> set up
/// the round for this run; <c>--green-read</c> turns on the green
/// heat map and grid; <c>--hit-at=t1,t2,…</c> presses HIT SHOT at those times (seconds after start); <c>--tuner</c>
/// opens the physics tuner (and the screenshot saves it too, as <c>file-tuner.png</c>); <c>--tune=key=value</c>
/// changes one tuning value after the demo shots and replays the last one.
/// </summary>
public partial class Main : Node3D
{
    private readonly ConcurrentQueue<Action> _pending = new();

    private RangeWorld _rangeWorld = null!;
    private ShotPlayer _player = null!;
    private CameraRig _camera = null!;
    private Camera3D _mapCamera = null!;
    private Hud _hud = null!;
    /// <summary>Seconds until the shot plan is refreshed for a physics change (countdown; negative = nothing pending).</summary>
    private double _replanIn = -1;

    // Saved data (null for development runs, so they don't touch the real round and shot history)
    private string _dataFolder = "";
    private AppSettings _settings = AppSettings.Defaults;
    private GolfData? _data;
    /// <summary>Remembered choices (mode, course, camera) are written back; not for development or screenshot runs.</summary>
    private bool _rememberChoices = true;

    /// <summary>Raised for every shot as soon as its result is known, with everything about it.</summary>
    public event Action<ShotFinished>? ShotFinished;

    public override void _Ready()
    {
        GetTree().AutoAcceptQuit = false;
        _dataFolder = DataFolderFor(OS.GetCmdlineUserArgs());
        CrashLog.Install(_dataFolder);

        (_world, _sun) = Scenery.AddSkyAndSun(this);
        _weather = new WeatherLook(_world, _sun);
        AddChild(_weather);
        _rangeWorld = new RangeWorld();
        AddChild(_rangeWorld);
        _player = new ShotPlayer();
        AddChild(_player);
        _camera = new CameraRig();
        AddChild(_camera);
        _camera.Init(_player);

        var ui = new CanvasLayer();
        _hud = new Hud();
        ui.AddChild(_hud);
        AddChild(ui);
        SetupMinimap();
        SetupTuner();

        _hud.ScanPressed += ToggleScan;
        _hud.ConnectPressed += Connect;
        _hud.DisconnectPressed += Disconnect;
        _hud.HitPressed += HitPress;
        _hud.RangeTargetPressed += CycleRangeTarget;
        _hud.Shot.PowerChanged += () => { if (_courseMode) UpdateAim(); else UpdateRangeAim(); };
        _hud.ClubChanged += OnClubChanged;
        _hud.NextHolePressed += NextHole;
        _hud.FlagPressed += ToggleFlag;
        _hud.HeatMapPressed += () => SetGreenOverlay(!_heatMap, _grid);
        _hud.GridPressed += () => SetGreenOverlay(_heatMap, !_grid);
        _hud.AimPressed += d => { if (_courseMode) Aim(d); else AimRange(d); };
        _hud.LandingViewPressed += ToggleLandingView;
        _hud.MinimapClicked += AimAtMap;
        _hud.MulliganPressed += Mulligan;
        _hud.NewRoundPressed += () => ConfirmRestartGame();
        _hud.PlayerChipPressed += NextRangePlayer;
        _hud.MenuPressed += OpenGameMenu;
        _hud.Players.Applied += RegroupPlayers;
        _hud.Shot.Describe = DescribePower;
        ShotFinished += SaveShot;
        SetupMainMenu();

        var args = OS.GetCmdlineUserArgs();
        _settings = AppSettings.Load(_dataFolder, Log);
        ApplyDisplaySettings();
        ApplyGraphics();
        SetupSettings();
        _game = _settings.Setup;
        SetupDevTools(args);
        SetupOnlineDevTools(args);
        // Runs that play shots by themselves (or jump holes) don't save, or resume, anything.
        bool devRun = args.Any(a => a.StartsWith("--demo-shots") || a.StartsWith("--hole=") || a.StartsWith("--hit-at") || a.StartsWith("--tune="));
        bool screenshot = args.Any(a => a.StartsWith("--screenshot"));
        _rememberChoices = !devRun && !screenshot;
        if (!devRun)
        {
            _data = new GolfData(_dataFolder);
            _data.Error += Log;
        }
        _camera.Follow = _settings.CameraFollow;
        _camera.FlyoverFinished += () => _hud.SetHint(null);

        // The course on offer: the one with a round left unfinished (closed or crashed), else the last one picked.
        var unfinished = _data?.LoadCurrentRound();
        int savedCourse = unfinished == null ? -1 : CourseLibrary.All.ToList().FindIndex(e => e.Name == unfinished.Course);
        int lastCourse = CourseLibrary.All.ToList().FindIndex(e => e.Name == _settings.Course);
        _courseIndex = savedCourse >= 0 ? savedCourse : Math.Max(0, lastCourse);
        ShowRangePlayer();
        SuggestPower();
        EnterRange();
        // The main menu, unless a switch says where to go (development runs stay on the range unless told otherwise).
        if (args.Contains("--course")) { if (LoadCourse(resume: true)) EnterCourse(); }
        else if (!args.Contains("--range") && (!devRun || _openSetupAtStart)) ShowMainMenu();
        if (_openSetupAtStart) _hud.MainMenu.OpenSetupPage();
        if (_match != null && _courseMode && args.FirstOrDefault(a => a.StartsWith("--hole=")) is { } h && int.TryParse(h[7..], out int n) && n >= 2 && n <= _match.Holes.Count)
            StartHole(n - 1);
        UpdateInsets();
        if (!_noBluetooth) SetupMonitor(forceAutoConnect: args.Contains("--autoconnect"));
        if (_openSettingsAtStart) OpenSettings();
        if (_openMenuAtStart) OpenGameMenu();
    }

    private void SetupMinimap()
    {
        var map = _hud.Minimap;
        map.World3D = GetViewport().World3D;
        _mapCamera = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal,
            KeepAspect = Camera3D.KeepAspectEnum.Height,
            Size = 340,
            Position = new Vector3(0, 300, -160),
            RotationDegrees = new Vector3(-90, 0, 0),
            Far = 800,
            CullMask = 1 | ShotPlayer.MapLayer,
            Current = true,
            // No fog from 300 m up.
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.15f, 0.25f, 0.13f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = Colors.White,
                AmbientLightEnergy = 0.6f,
            },
        };
        map.AddChild(_mapCamera);
    }

    /// <summary>
    /// The physics tuner, in its own OS window (so sub-windows aren't embedded; menus and tooltips then open as
    /// native pop-ups too). Start-up values saved from the tuner are applied before anything is simulated.
    /// </summary>
    private void SetupTuner()
    {
        GetViewport().GuiEmbedSubwindows = false;
        if (TunerWindow.LoadStartup() is { } startup)
        {
            ActivePhysics.Apply(startup);
            Log($"Physics: start-up values loaded ({startup.ChangedCount} changed from the defaults). Open the tuner with T.");
        }
        _tuner = new TunerWindow();
        AddChild(_tuner);
        _tuner.ReplayPressed += Replay;
        // Carry estimates and the putt read depend on the physics: refresh the plan once the sliders settle.
        ActivePhysics.Changed += () => _replanIn = 0.3;
    }

    /// <summary>
    /// Full screen, and text size: the whole 2D UI is drawn bigger (the 3D view is unaffected). The UI also grows
    /// with the window by itself (stretch mode <c>canvas_items</c>, laid out for 1600×900).
    /// </summary>
    private void ApplyDisplaySettings()
    {
        GetTree().Root.ContentScaleFactor = _settings.UiScale;
        var mode = DisplayServer.WindowGetMode();
        bool full = mode is DisplayServer.WindowMode.Fullscreen or DisplayServer.WindowMode.ExclusiveFullscreen;
        if (_settings.FullScreen && !full) DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
        else if (!_settings.FullScreen && full) DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
        UpdateInsets();
    }

    private WorldEnvironment _world = null!;
    private DirectionalLight3D _sun = null!;
    /// <summary>The round's weather on the sky, light and haze (rain when it's wet).</summary>
    private WeatherLook? _weather;

    /// <summary>3D quality (anti-aliasing, shadows, lighting effects): see <see cref="Graphics"/>.</summary>
    private void ApplyGraphics() => Graphics.Apply(_settings.Graphics, GetViewport(), _world, _sun);

    /// <summary>
    /// Tells the camera how much of the screen the UI covers, so the ball stays centred in what's left (in UI units,
    /// which the camera compares with the visible rect, so the UI scale doesn't matter).
    /// </summary>
    private void UpdateInsets() => _camera.Insets = _hud.ViewInsets;

    /// <summary>
    /// On the hole overview: the ball, the aim line out to where the shot set up will carry (it follows the aim and
    /// the power), and the flag with its distance.
    /// </summary>
    private void UpdateMapMarkers(P2 aimPoint, double aimMetres)
    {
        var round = _round;
        if (!_courseMode || round == null || round.Holed || _outcome != null) { _hud.HideMapMarkers(); return; }
        bool green = round.Lie == Lie.Green;
        _hud.SetMapMarkers(_mapCamera.UnprojectPosition(BallAt(round.Ball)), _mapCamera.UnprojectPosition(BallAt(aimPoint)),
            _mapCamera.UnprojectPosition(BallAt(round.Hole.Pin)), PinDistance(aimMetres, green || Putter),
            PinDistance(round.DistanceToPin, green), UiTheme.PlayerColor(_match!.Turn));
    }

    /// <summary>Points the minimap at the range, or fits it to a hole with the tee at the bottom and the pin at the top.</summary>
    private void FrameMinimap(Hole? hole)
    {
        if (hole == null)
        {
            _mapCamera.Size = 340;
            _mapCamera.Position = new Vector3(0, 300, -160);
            _mapCamera.Rotation = new Vector3(-Mathf.Pi / 2, 0, 0);
            return;
        }
        var b = hole.Bounds;
        var corners = new[] { b.Min, new P2(b.Max.X, b.Min.Z), b.Max, new P2(b.Min.X, b.Max.Z) }.Select(c => Scenery.ToGodot(c)).ToArray();
        var center = corners.Aggregate(Vector3.Zero, (a, c) => a + c) / 4;
        var up = Scenery.ToGodot(hole.Pin) - Scenery.ToGodot(hole.Tee);
        up = new Vector3(up.X, 0, up.Z).Normalized();
        var across = new Vector3(-up.Z, 0, up.X);
        float along = corners.Max(c => Mathf.Abs((c - center).Dot(up))) * 2;
        float wide = corners.Max(c => Mathf.Abs((c - center).Dot(across))) * 2;
        var px = _hud.MinimapSize;
        _mapCamera.Size = Mathf.Max(along, wide * px.Y / px.X) * 1.04f;
        _mapCamera.Position = center + new Vector3(0, 400, 0);
        // Yaw so the tee-to-pin direction points up the minimap.
        _mapCamera.Rotation = new Vector3(-Mathf.Pi / 2, Mathf.Atan2(-up.X, -up.Z), 0);
    }

    public override void _Process(double delta)
    {
        while (_pending.TryDequeue(out var a)) a();
        ProcessOnline(delta);
        ProcessMonitor(delta);
        if (_outcome != null && !_player.IsPlaying && _player.IdleSeconds > 1.0) FinishShot();
        if (_rangeResult != null && !_player.IsPlaying) FlushRangeResult();
        CountDownToNextHole(delta);
        _player.ShowGhost = _tuner.Visible && _tuner.ShowGhost;
        if (_replanIn >= 0 && (_replanIn -= delta) < 0 && _courseMode && _outcome == null) UpdateAim();
        UpdateReady();
        RunDevTools(delta);
    }

    /// <summary>
    /// The Ready light: ready once the last shot has finished and, with a launch monitor connected, it has a ball
    /// teed up and seen. Without a monitor, ready as soon as the last shot has finished.
    /// </summary>
    private void UpdateReady()
    {
        bool moving = _outcome != null || _player.IsPlaying;
        if (!moving && OnlineReady()) return;
        if (_camera.Flying) _hud.Shot.SetReady(false, "Flyover");
        else if (_courseMode && _round is { Holed: true } && !moving) _hud.Shot.SetReady(false, "Hole finished");
        else if (moving) _hud.Shot.SetReady(false, "Ball in play");
        else if (_monitorLive && !_ballReady) _hud.Shot.SetReady(false, _ballSeen ? "Ball not ready" : "Place a ball");
        else _hud.Shot.SetReady(true, "Ready");
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest) Quit();
    }

    /// <summary>Closes the app, letting the monitor session let go of the monitor first (but not waiting long for it).</summary>
    private void Quit()
    {
        if (_online is HostSession { Phase: not (OnlinePhase.Lobby or OnlinePhase.Over) } host) host.Stop(paused: true);
        if (_online is ClientSession friend) friend.Leave();
        _scanner?.Dispose();
        if (_session == null) { GetTree().Quit(); return; }
        // Let the session release the monitor before exiting, but don't hang on it.
        _quitting = true;
        _session.Stop();
        GetTree().CreateTimer(3).Timeout += () => GetTree().Quit();
    }

    // ---- shot finished -----------------------------------------------------------------------

    private void RaiseShotFinished(ShotFinished e)
    {
        try { ShotFinished?.Invoke(e); }
        catch (Exception ex) { GD.PushError($"A shot-finished handler failed: {ex}"); }   // never hold up a shot
    }

    /// <summary>Saving data: every shot goes into the session file.</summary>
    private void SaveShot(ShotFinished e)
    {
        // Online, each PC keeps its own players' shots (the host plays everyone's).
        if (_online != null && e.Course != null && !_online.IsLocalName(e.Player.Name)) return;
        var record = ShotRecord.Of(e);
        if (e.Course != null) _lastRecord = record;
        _data?.AppendShot(record);
    }

    // ---- helpers -----------------------------------------------------------------------------

    private void Post(Action a) => _pending.Enqueue(a);

    private void Log(string m)
    {
        string line = $"{DateTime.Now:HH:mm:ss}  {m}";
        GD.Print(line);
        _hud?.Log(line);
    }

    /// <summary>Keeps the settings and writes them to settings.json.</summary>
    private void SaveSettings(AppSettings s)
    {
        _settings = s;
        if (_rememberChoices) s.Save(_dataFolder, Log);
    }
}
