using System.Collections.Concurrent;
using Godot;
using SquareGspro.Bluetooth;
using SquareGspro.Core;
using SquareGspro.Core.Course;
using SquareGspro.Core.Square;
using SquareGspro.Physics;

namespace SquareGspro.Range3D;

/// <summary>
/// Root of the app: builds the world, camera and HUD, and connects the HUD to the Square (scan, connect, stats, shots).
/// Two modes: the driving range, and course play over one of the <see cref="CourseLibrary"/> courses.
/// Bluetooth events arrive on background threads and are marshalled to the main thread through <see cref="Post"/>.
/// Command-line: <c>--autoconnect</c> connects to the first Square found; <c>--course</c> starts in course mode;
/// <c>--test-holes</c> picks the test holes instead of the first course; <c>--hole=N</c> starts on hole N.
/// Development: <c>--demo-shots=N</c> hits N demo shots after start-up, and <c>--screenshot=file.png</c> saves the
/// window to a PNG (after <c>--screenshot-delay=seconds</c>, default 4, or once the demo shots have finished) and quits;
/// <c>--monitor-drawer</c> opens the launch monitor drawer; <c>--green-read</c> turns on the green heat map and grid;
/// <c>--swing-at=t1,t2,…</c> presses Space for the swing meter at those times (seconds after start); <c>--tuner</c> opens the physics tuner (and the screenshot
/// saves it too, as <c>file-tuner.png</c>); <c>--tune=key=value</c> changes one tuning value after the demo shots and
/// replays the last one.
/// </summary>
public partial class Main : Node3D
{
    private readonly ConcurrentQueue<Action> _pending = new();
    private readonly Dictionary<ulong, DeviceEntry> _devices = new();
    private readonly Random _rng = new();

    private RangeWorld _rangeWorld = null!;
    private ShotPlayer _player = null!;
    private CameraRig _camera = null!;
    private Camera3D _mapCamera = null!;
    private Hud _hud = null!;
    private SquareBleScanner? _scanner;
    private MonitorSession? _session;
    private bool _devicesDirty, _autoConnect, _quitting;
    private double _deviceRefresh;
    private int _shotCount;

    // Course mode
    private bool _courseMode;
    private int _courseIndex;
    private Course? _courseDef;
    private HoleRound? _round;
    private CourseWorld? _course;
    private double _aimHeading;
    private bool _flagWhenPutting;
    /// <summary>Green-reading aids on the green being played (kept from hole to hole).</summary>
    private bool _heatMap, _grid;
    /// <summary>The last course shot, applied to the HUD/ball once its animation finishes (so the result isn't spoiled).</summary>
    private ShotOutcome? _outcome;

    // Physics tuner
    private TunerWindow _tuner = null!;
    /// <summary>The last shot played, to replay with new physics: on a course, with the round as it was before it.</summary>
    private sealed record LastShot(RangeShot Shot, string Club, HoleRound? Round, HoleRound.State? Before, double Heading);
    private LastShot? _last;
    /// <summary>Seconds until the shot plan is refreshed for a physics change (countdown; negative = nothing pending).</summary>
    private double _replanIn = -1;

    public override void _Ready()
    {
        GetTree().AutoAcceptQuit = false;

        Scenery.AddSkyAndSun(this);
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
        _camera.Insets = _hud.ViewInsets;
        SetupMinimap();
        SetupTuner();

        _hud.ScanPressed += ToggleScan;
        _hud.ConnectPressed += Connect;
        _hud.DisconnectPressed += () => _session?.Stop();
        _hud.SwingPressed += SwingPress;
        _hud.Meter.Finished += OnSwing;
        _hud.ClearPressed += ClearRange;
        _hud.CameraPressed += ToggleCamera;
        _hud.ClubChanged += OnClubChanged;
        _hud.ModePressed += ToggleMode;
        _hud.RestartHolePressed += () => { if (_round != null) StartHole(_round.HoleIndex); };
        _hud.NextHolePressed += NextHole;
        _hud.CourseSelected += SelectCourse;
        _hud.FlagPressed += ToggleFlag;
        _hud.HeatMapPressed += () => SetGreenOverlay(!_heatMap, _grid);
        _hud.GridPressed += () => SetGreenOverlay(_heatMap, !_grid);
        _hud.AimPressed += d => Aim(d);
        _hud.MinimapClicked += AimAtMap;

        var args = OS.GetCmdlineUserArgs();
        SetupDevTools(args);
        _autoConnect = args.Contains("--autoconnect");
        if (args.Contains("--test-holes")) _courseIndex = Math.Max(0, CourseLibrary.All.ToList().FindIndex(e => e.Name == "Test holes"));
        _hud.SetCourses(CourseLibrary.All.Select(e => e.Name), _courseIndex);
        PrepareMeter();
        if (args.Contains("--course")) ToggleMode();
        if (_round != null && args.FirstOrDefault(a => a.StartsWith("--hole=")) is { } h && int.TryParse(h[7..], out int n) && n >= 2 && n <= _round.Holes.Count)
            StartHole(n - 1);
        try
        {
            _scanner = new SquareBleScanner();
            _scanner.DeviceSeen += a => Post(() => OnDeviceSeen(a));
            _scanner.Stopped += err => Post(() =>
            {
                _hud.SetScanning(false);
                if (err != "Success") Log($"Scan stopped: {err}. Is Bluetooth turned on?");
            });
            StartScan();
        }
        catch (Exception ex) { Log($"Bluetooth unavailable: {ex.Message}"); }
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
        _hud.TunerPressed += _tuner.Toggle;
        // Carry estimates and the putt read depend on the physics: refresh the plan once the sliders settle.
        ActivePhysics.Changed += () => _replanIn = 0.3;
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

        _deviceRefresh += delta;
        if (_deviceRefresh > (_devicesDirty ? 0.5 : 2.0))
        {
            _deviceRefresh = 0;
            _devicesDirty = false;
            _hud.SetDevices(_devices.Values.OrderBy(d => d.Name).ToList());
        }

        if (_outcome != null && !_player.IsPlaying && _player.IdleSeconds > 1.0) FinishShot();
        _player.ShowGhost = _tuner.Visible && _tuner.ShowGhost;
        if (_replanIn >= 0 && (_replanIn -= delta) < 0 && _courseMode && _outcome == null) UpdateAim();
        RunDevTools(delta);
    }

    // Handled in _Input (before the GUI) so focused buttons/lists don't swallow the arrow keys.
    public override void _Input(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true } k) return;
        bool handled = true;
        switch (k.Keycode)
        {
            case Key.Left or Key.Right when _courseMode && !_hud.Meter.Busy:
                Aim((k.Keycode == Key.Left ? -1 : 1) * (k.ShiftPressed ? 5 : 1));
                break;
            case Key.Space when !k.Echo:
                SwingPress();
                break;
            case Key.Escape when _hud.Meter.Busy:
                _hud.Meter.Cancel();
                break;
            case Key.D when !k.Echo && !_hud.Meter.Busy:
                if (_courseMode && _round is { Holed: true } && _outcome == null) NextHole(); else Demo();
                break;
            case Key.C when !k.Echo: ToggleCamera(); break;
            case Key.M when !k.Echo: ToggleMode(); break;
            case Key.L when !k.Echo: _hud.ToggleDrawer(); break;
            case Key.T when !k.Echo: _tuner.Toggle(); break;
            case Key.F5 when !k.Echo: Replay(); break;
            case Key.S when !k.Echo && _courseMode: _hud.ToggleScorecard(); break;
            case Key.N when !k.Echo && _courseMode: NextHole(); break;
            case Key.F when !k.Echo && _courseMode: ToggleFlag(); break;
            case Key.H when !k.Echo && _courseMode: SetGreenOverlay(!_heatMap, _grid); break;
            case Key.G when !k.Echo && _courseMode: SetGreenOverlay(_heatMap, !_grid); break;
            case Key.R when !k.Echo && _courseMode && _round != null: StartHole(_round.HoleIndex); break;
            default: handled = false; break;
        }
        if (handled) GetViewport().SetInputAsHandled();
    }

    public override void _Notification(int what)
    {
        if (what != NotificationWMCloseRequest) return;
        _scanner?.Dispose();
        if (_session == null) { GetTree().Quit(); return; }
        // Let the session release the monitor before exiting, but don't hang on it.
        _quitting = true;
        _session.Stop();
        GetTree().CreateTimer(3).Timeout += () => GetTree().Quit();
    }

    // ---- scanning ----------------------------------------------------------------------------

    private void StartScan()
    {
        if (_scanner == null) return;
        try
        {
            _scanner.Start();
            _hud.SetScanning(true);
            Log("Scanning for Square launch monitors...");
        }
        catch (Exception ex) { Log($"Can't scan: {ex.Message}"); }
    }

    private void ToggleScan()
    {
        if (_scanner == null) return;
        if (_scanner.IsScanning) { _scanner.Stop(); _hud.SetScanning(false); }
        else StartScan();
    }

    private void OnDeviceSeen(SquareAdvertisement a)
    {
        if (!_devices.ContainsKey(a.Address)) Log($"Found {a.Name} ({a.AddressText}, {a.Rssi} dBm)");
        _devices[a.Address] = new DeviceEntry(a.Address, a.Name, a.AddressText, a.Rssi, a.SeenAt);
        _devicesDirty = true;
        if (_session == null && _hud.SelectedDevice == null) ShowDevice(_devices[a.Address]);
        if (_autoConnect && _session == null)
        {
            _autoConnect = false;
            Connect(a.Address);
        }
    }

    private void ShowDevice(DeviceEntry d)
    {
        _hud.SetStat("Name", d.Name);
        _hud.SetStat("Address", d.AddressText);
        _hud.SetStat("Signal", $"{d.Rssi} dBm");
    }

    // ---- connection --------------------------------------------------------------------------

    private void Connect(ulong address)
    {
        if (_session != null || !_devices.TryGetValue(address, out var d)) return;

        _scanner?.Stop();
        _hud.SetScanning(false);
        _hud.SetConnected(true);
        _hud.ResetStats();
        ShowDevice(d);
        _hud.SetStatus($"Connecting to {d.Name}...");
        _hud.SetReady("Connecting...", new Color(0.80f, 0.60f, 0.10f));

        var s = new MonitorSession(address, m => Post(() => Log(m)));
        _session = s;
        s.InfoRead += info => Post(() =>
        {
            _hud.SetStatus($"Connected to {d.Name}");
            _hud.SetStat("Serial", info.Serial ?? "—");
            _hud.SetStat("Hardware", info.HardwareVersion ?? "—");
            _hud.SetStat("Firmware (LM)", info.FirmwareComponents.GetValueOrDefault("lm") ?? info.FirmwareRaw ?? "—");
            _hud.SetStat("Firmware (MMI)", info.FirmwareComponents.GetValueOrDefault("mmi") ?? "—");
            _hud.SetStat("Firmware (launcher)", info.FirmwareComponents.GetValueOrDefault("launcher") ?? "—");
            _hud.SetReady("Place a ball", new Color(0.3f, 0.3f, 0.3f));
        });
        s.Battery += b => Post(() => _hud.SetBattery(b.Level, ChargingText(b.ChargingState)));
        s.Clock += secs => Post(() => _hud.SetStat("Uptime", TimeSpan.FromSeconds(secs).ToString(secs >= 86400 ? @"d\.hh\:mm\:ss" : @"hh\:mm\:ss")));
        s.StateChanged += st => Post(() => _hud.SetStat("State", st.ToString()));
        s.Sensor += sn => Post(() =>
        {
            _hud.SetStat("Ball", sn.BallReady ? "Ready" : sn.BallDetected ? "Detected" : "None");
            if (sn.BallReady) _hud.SetReady("Ready — hit!", new Color(0.18f, 0.54f, 0.32f));
            else if (sn.BallDetected) _hud.SetReady("Ball detected", new Color(0.80f, 0.60f, 0.10f));
            else _hud.SetReady("Place a ball", new Color(0.3f, 0.3f, 0.3f));
        });
        s.Shot += (ball, club) => Post(() => ShowShot(RangeShot.FromSquare(ball, club)));
        s.Ended += reason => Post(() => OnSessionEnded(s, reason));

        string code = _hud.ClubCode;
        bool lh = _hud.LeftHanded;
        _ = Task.Run(() => s.RunAsync(code, lh));
    }

    private void OnSessionEnded(MonitorSession s, string reason)
    {
        if (_session != s) return;
        _session = null;
        if (_quitting) { GetTree().Quit(); return; }
        Log(reason);
        _hud.SetStatus(reason);
        _hud.SetReady("Not connected", new Color(0.3f, 0.3f, 0.3f));
        _hud.SetStat("State", "—");
        _hud.SetStat("Ball", "—");
        _hud.SetConnected(false);
        StartScan();
    }

    private async void OnClubChanged()
    {
        if (_courseMode) UpdateAim();
        else if (!_hud.Meter.Busy) PrepareMeter();
        var s = _session;
        if (s == null) return;
        string code = _hud.ClubCode;
        bool lh = _hud.LeftHanded;
        try
        {
            await Task.Run(() => s.SelectClubAsync(code, lh));
            Log($"Club set to {code}{(lh ? " (LH)" : "")}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log($"Club change failed: {ex.Message}"); }
    }

    // ---- shots -------------------------------------------------------------------------------

    private bool Putter => _hud.ClubCode == "PT";

    private void Demo()
    {
        if (!_courseMode)
        {
            ShowShot(Putter ? Caddie.DemoShot(_hud.Club, 6, Lie.Green, _rng) : RangeShot.Demo(_hud.Club, _rng));
            return;
        }
        if (_round == null || _round.Holed) return;
        // On the green, putt at the pace the read calls for (slopes included); elsewhere size the shot to the
        // distance it plays (uphill longer, downhill shorter).
        if (Putter && _round.Lie == Lie.Green) { ShowShot(Caddie.DemoPutt(_round.ReadPutt().Speed, _rng)); return; }
        double target = Putter ? _round.DistanceToPin : _round.PlaysLike(_round.DefaultTarget);
        ShowShot(Caddie.DemoShot(_hud.Club, target, _round.Lie, _rng, _round.StanceAt(_round.Ball, _aimHeading, _hud.LeftHanded)));
    }

    // ---- swing meter -------------------------------------------------------------------------

    /// <summary>The putter meter's full scale (m of flat roll) for the swing in progress.</summary>
    private double _puttScale;

    /// <summary>Space / HIT SHOT: next hole once holed out, otherwise the swing meter's next stage.</summary>
    private void SwingPress()
    {
        var meter = _hud.Meter;
        if (meter.Busy) { meter.Press(); return; }
        if (_courseMode)
        {
            if (_round == null) return;
            if (_round.Holed) { if (_outcome == null) NextHole(); return; }
            if (_outcome != null || _player.IsPlaying) return;   // wait for the ball to stop
        }
        PrepareMeter();
        meter.Press();
    }

    /// <summary>Shows where this shot calls for on the meter and what 100% means, and dims the strike point for putts.</summary>
    private void PrepareMeter()
    {
        var meter = _hud.Meter;
        _hud.Strike.Enabled = !Putter;
        if (Putter)
        {
            var lie = _courseMode && _round != null ? _round.Lie : Lie.Green;
            double need = !_courseMode || _round == null ? 6
                        : lie == Lie.Green ? Caddie.PuttRollMetres(_round.ReadPutt().Speed, lie)
                        : _round.DistanceToPin + 0.25;
            _puttScale = Caddie.PuttScaleMetres(need);
            meter.Prepare(_courseMode ? (float)(need / _puttScale) : null, $"100% = {_puttScale * Units.MetresToYards * 3:0} ft roll");
            return;
        }
        var stance = _courseMode && _round != null ? _round.StanceAt(_round.Ball, _aimHeading, _hud.LeftHanded) : null;
        double full = Caddie.CarryFrom(_hud.ClubCode, stance);
        float? target = _courseMode && _round != null && full > 0 ? (float)(_round.PlaysLike(_round.DefaultTarget) / full) : null;
        meter.Prepare(target, $"100% = {full * Units.MetresToYards:0} yd carry");
    }

    /// <summary>The meter's third press: turns power, timing and strike point into a shot.</summary>
    private void OnSwing(float power, float accuracy)
    {
        var swing = new Swing(power, accuracy, _hud.Strike.StrikeX, _hud.Strike.StrikeY);
        var lie = _courseMode && _round != null ? _round.Lie : Lie.Green;
        var shot = Putter ? Caddie.SwingPutt(swing, _puttScale, lie, _hud.LeftHanded) : Caddie.SwingShot(_hud.Club, swing, _hud.LeftHanded);
        var (timing, _) = SwingMeter.TimingWords(accuracy, (float)Caddie.PerfectTiming);
        Log($"Swing: {power * 100:0}% power, {timing.ToLowerInvariant()} ({accuracy:+0.00;-0.00})" +
            (Putter || (swing.StrikeX == 0 && swing.StrikeY == 0) ? "" : $", strike {swing.StrikeX:+0.0;-0.0} across, {swing.StrikeY:+0.0;-0.0} up"));
        ShowShot(shot);
        if (!_courseMode) PrepareMeter();
    }

    /// <summary>Every shot (from the monitor, a demo or a replay) comes through here.</summary>
    private void ShowShot(RangeShot s, bool replay = false)
    {
        if (_hud.Meter.Busy) _hud.Meter.Cancel();   // a shot from the monitor (or a demo) beats a swing in progress
        if (_courseMode) { PlayCourseShot(s, replay); return; }
        var f = Putter ? BallFlight.Putt(s.BallMph, s.DirectionDeg) : s.Fly();
        _player.Play(f);
        CountShot(s, f, replay);
        string label = _shotCount + (replay ? "R" : s.IsDemo ? "*" : "");
        _hud.AddRangeShot(label, _hud.ClubCode, s, f);
        Remember(new LastShot(s, _hud.ClubCode, null, null, 0), label, f);
    }

    /// <summary>No replay across a switch between the range and a course (it would play somewhere else).</summary>
    private void ForgetLastShot()
    {
        _last = null;
        _tuner.SetReplay(null);
    }

    /// <summary>Keeps the shot for Replay and adds its numbers to the tuner's comparison.</summary>
    private void Remember(LastShot last, string label, FlightResult f)
    {
        _last = last;
        var s = last.Shot;
        _tuner.SetReplay($"{last.Club}, {s.BallMph:0.0} mph, {s.LaunchDeg:0.0}° launch, {s.SpinRpm:0} rpm");
        _tuner.AddResult(new TunerResult(label, last.Club, f.LandingIndex == 0, f.CarryYards, f.TotalYards, f.ApexYards, f.DescentDeg,
                                         f.LateralYards, ActivePhysics.Tuning));
    }

    /// <summary>
    /// Plays the last shot again with the current physics: on the range from the tee; on a course from where it was
    /// struck, with the round put back as it was (so the replay replaces it rather than counting another stroke).
    /// </summary>
    private void Replay()
    {
        var last = _last;
        if (last == null) { Log("Nothing to replay yet: hit a shot first."); return; }
        _hud.SetClub(last.Club);
        if (last.Round == null) { ShowShot(last.Shot, replay: true); return; }

        if (!_courseMode || last.Round != _round) { ForgetLastShot(); return; }   // a round that's been replaced
        var round = _round;
        var before = last.Before!;
        _outcome = null;
        if (round.HoleIndex != before.HoleIndex) StartHole(before.HoleIndex);
        round.Restore(before);
        _hud.HideResult();
        _hud.ShowScorecard(false);
        _player.PlaceBall(BallAt(before.Ball));
        _aimHeading = last.Heading;
        UpdateAim();
        ShowShot(last.Shot, replay: true);
    }

    private void CountShot(RangeShot s, FlightResult f, bool replay = false)
    {
        _shotCount++;
        _hud.SetStat("Shots", _shotCount.ToString());
        Log($"{(replay ? "Replay" : s.IsDemo ? "Demo shot" : "Shot")} {_shotCount} ({_hud.ClubCode}): {s.BallMph:0.0} mph, {s.LaunchDeg:0.0}° launch, {s.SpinRpm:0} rpm → carry {f.CarryYards:0} yd, total {f.TotalYards:0} yd");
    }

    private void ClearRange()
    {
        _player.Clear();
        _hud.ClearShots();
        _shotCount = 0;
        _hud.SetStat("Shots", "0");
    }

    private void ToggleCamera()
    {
        _camera.Follow = !_camera.Follow;
        _hud.SetCameraLabel(_camera.Follow);
    }

    // ---- course play -------------------------------------------------------------------------

    private void ToggleMode()
    {
        _courseMode = !_courseMode;
        ForgetLastShot();
        _hud.SetCourseMode(_courseMode);
        _camera.Insets = _hud.ViewInsets;
        _rangeWorld.Visible = !_courseMode;
        _player.GroundTop = 0.115f;   // the range mat (course holes set their own)
        _camera.GroundAt = null;
        if (_courseMode)
        {
            if (_round == null && !LoadCourse()) { ToggleMode(); return; }
            StartHole(_round!.HoleIndex);
            Log("Course mode: ←/→ aim (Shift = 5°) or click the map, Space swing (start, power, strike), D random demo shot, S scorecard, R restart hole, N next hole, F flag in/out when putting, H green heat map, G green grid.");
        }
        else
        {
            _course?.QueueFree();
            _course = null;
            _outcome = null;
            _player.CupAt = null;
            _player.Clear();
            _camera.SetHome(Vector3.Zero, Vector3.Forward, putting: false, snap: true);
            FrameMinimap(null);
            PrepareMeter();
        }
    }

    /// <summary>Loads the selected course and starts a new round on it (false if it couldn't be loaded).</summary>
    private bool LoadCourse()
    {
        var entry = CourseLibrary.All[_courseIndex];
        try
        {
            _courseDef = entry.Load();
        }
        catch (Exception ex)
        {
            Log($"Couldn't load {entry.Name}: {ex.Message}");
            return false;
        }
        _round = new HoleRound(_courseDef.Holes, _courseDef.Terrain);
        _course?.QueueFree();
        _course = null;
        _hud.SetCredit(_courseDef.Credit);
        Log($"{_courseDef.Name}: {_courseDef.Holes.Count} holes, par {_courseDef.Holes.Sum(h => h.Par)}, {_courseDef.Holes.Sum(h => h.LengthYards):0} yd.");
        foreach (var note in _courseDef.Notes) Log("  " + note);
        return true;
    }

    private void SelectCourse(int index)
    {
        if (index == _courseIndex && _round != null) return;
        int previous = _courseIndex;
        _courseIndex = index;
        if (!_courseMode) { _round = null; return; }
        if (!LoadCourse())
        {
            _courseIndex = previous;
            _hud.SetCourses(CourseLibrary.All.Select(e => e.Name), previous);
            return;
        }
        StartHole(0);
    }

    private void StartHole(int index)
    {
        var round = _round!;
        var course = _courseDef!;
        round.StartHole(index);
        _outcome = null;
        // A mapped course is built once and shared by all its holes; test holes are each their own world.
        if (_course != null && course.SharedLayout && _course.Course == course) _course.ShowHole(round.Hole);
        else
        {
            _course?.QueueFree();
            _course = new CourseWorld(course, round.Hole);
            AddChild(_course);
        }
        SetGreenOverlay(_heatMap, _grid);
        // With real ground the ball sits on it; on a flat course it sits on the raised green layer.
        var terrain = course.Terrain;
        _player.GroundTop = terrain != null ? 0f : Scenery.GreenTop;
        _camera.GroundAt = terrain == null ? null : v => (float)terrain.HeightAt(new P2(-v.Z, v.X));
        _player.CupAt = BallAt(round.Hole.Pin);
        _player.Clear(BallAt(round.Hole.Tee));
        FrameMinimap(round.Hole);
        _hud.HideResult();
        _hud.ShowScorecard(false);
        PrepareNextShot(snapCamera: true);
        Log($"{HoleTitle(round.Hole)}");
    }

    private void NextHole()
    {
        var round = _round;
        if (round == null) return;
        if (round.RoundComplete) { _round = new HoleRound(round.Holes, round.Terrain); StartHole(0); return; }
        StartHole((round.HoleIndex + 1) % round.Holes.Count);
    }

    private void PlayCourseShot(RangeShot s, bool replay = false)
    {
        var round = _round;
        if (round == null) return;
        if (_outcome != null) FinishShot();       // a new shot arrived before the last one finished animating
        if (round.Holed) { Log("Hole finished. Press N for the next hole."); return; }

        int strokesBefore = round.Strokes;
        var before = round.Save();
        double heading = _aimHeading;
        var o = round.Play(s, Putter, _aimHeading, _hud.ClubCode, _hud.LeftHanded);
        if (round.Strokes == strokesBefore && !o.Holed)
        {
            // The engine couldn't simulate it (impossible numbers from the monitor): nothing happened.
            Log(o.Message);
            _hud.ShowResult("Shot not counted", Array.Empty<Stat>(), o.Message);
            return;
        }
        _outcome = o;
        _player.Play(o.Flight);
        _course?.SetAim(round.Ball, _aimHeading, 0, visible: false);
        CountShot(s, o.Flight, replay);
        Remember(new LastShot(s, _hud.ClubCode, round, before, heading), $"{round.Hole.Number}.{strokesBefore + 1}{(replay ? "R" : "")}", o.Flight);
        bool putt = o.Flight.LandingIndex == 0;
        _hud.ShowResult(putt ? "Rolling…" : "In flight…",
            putt ? new[] { new Stat("Ball speed", $"{s.BallMph:0.0}", "mph") }
                 : new[] { new Stat("Ball speed", $"{s.BallMph:0.0}", "mph"), new Stat("Launch", $"{s.LaunchDeg:0.0}°"), new Stat("Spin", $"{s.SpinRpm:0}", "rpm") },
            LieEffectNote(o));
        RefreshPlan();   // no HIT SHOT while the ball is moving
    }

    /// <summary>Applies the last shot once the ball has stopped: penalties, holing out, the result card, then the next shot.</summary>
    private void FinishShot()
    {
        var o = _outcome!;
        _outcome = null;
        var round = _round!;
        _player.HideCarryLabel();
        if (o.Penalty > 0) _player.PlaceBall(BallAt(o.NextBall));
        // A holed ball's path already ends in the cup; a tapped-in one is picked up and dropped in.
        else if (o.Holed && o.Flight.Points[^1].Y > round.GroundAt(round.Hole.Pin) - HoleRound.CupDepth / 2)
            _player.PlaceBall(BallAt(round.Hole.Pin) - new Vector3(0, (float)HoleRound.CupDepth, 0));

        string headline = o.Message, note = LieEffectNote(o);
        if (o.Holed && round.RoundComplete)
        {
            int total = round.Scores.Sum(x => x!.Value), par = round.Holes.Sum(h => h.Par);
            note = $"Round complete: {total} ({Hud.ToPar(total - par)}). Press N to play again.";
        }
        else if (o.Holed) note = "Press N for the next hole.";

        var stats = new List<Stat>();
        if (o.Flight.LandingIndex == 0)
        {
            stats.Add(Distance("Rolled", o.Flight.TotalYards * Units.YardsToMetres, short_: true));
            if (!o.Holed && o.Penalty == 0) stats.Add(Distance("To the hole", o.Rest.DistanceTo(round.Hole.Pin), short_: true));
        }
        else
        {
            stats.Add(new Stat("Carry", $"{o.Flight.CarryYards:0}", "yd"));
            stats.Add(new Stat("Total", $"{o.Flight.TotalYards:0}", "yd"));
            stats.Add(new Stat("Offline", RangeShot.Side(o.Flight.LateralYards, "0"), "yd"));
            if (!o.Holed && o.Penalty == 0) stats.Add(Distance("To the pin", o.Rest.DistanceTo(round.Hole.Pin), short_: round.Lie == Lie.Green));
        }
        _hud.ShowResult(headline, stats, note);
        Log(headline + (note.Length > 0 ? "  " + note : ""));
        PrepareNextShot();
        if (o.Holed) _hud.ShowScorecard(true);
    }

    /// <summary>"From the rough: −5% speed, −30% spin" when the lie or stance changed the shot.</summary>
    private static string LieEffectNote(ShotOutcome o) =>
        o.LieEffect is { Changed: true } e ? $"{LieWords(e.Condition)}: {e.Summary}" : "";

    private static string LieWords(LieCondition c)
    {
        string lie = c.Kind switch
        {
            LieKind.Rough => "From the rough", LieKind.DeepRough => "From deep rough", LieKind.Sand => "From the sand",
            LieKind.Fringe => "From the fringe", _ => "",
        };
        string stance = c.IsLevel ? "" : "sloping lie";
        return lie.Length == 0 ? char.ToUpper(stance[0]) + stance[1..] : stance.Length == 0 ? lie : $"{lie}, {stance}";
    }

    /// <summary>A distance in yards, or feet when short (putts, around the green).</summary>
    private static Stat Distance(string caption, double metres, bool short_)
    {
        double yd = metres * Units.MetresToYards;
        return short_ || yd < 20 ? new Stat(caption, $"{yd * 3:0}", "ft") : new Stat(caption, $"{yd:0}", "yd");
    }

    /// <summary>Default aim and club for the ball's new position, camera, aim line, hole card, scorecard and shot plan.</summary>
    private void PrepareNextShot(bool snapCamera = false)
    {
        var round = _round!;
        _aimHeading = round.DefaultAimHeading();
        if (!round.Holed)
            _hud.SetClub(Caddie.Suggest(round.PlaysLike(round.DefaultTarget), round.Lie, round.StanceAt(round.Ball, _aimHeading, _hud.LeftHanded)));
        UpdateAim(snapCamera);

        var hole = round.Hole;
        var played = Enumerable.Range(0, round.Holes.Count).Where(i => round.Scores[i] != null).ToList();
        string roundScore = played.Count == 0 ? "" :
            $"   ·   Round {Hud.ToPar(played.Sum(i => round.Scores[i]!.Value - round.Holes[i].Par))} thru {played.Count}";
        string progress = round.Holed ? $"Holed in {round.Strokes} · {HoleRound.ScoreName(round.Strokes, hole.Par)}" : $"Stroke {round.Strokes + 1}";
        _hud.SetHoleCard(hole.Number, hole.Name, hole.Par, hole.LengthYards, progress + roundScore);
        _hud.SetScorecard(_courseDef!.Name, round.Holes.Select(h => h.Par).ToList(), round.Scores, round.HoleIndex);
    }

    /// <summary>
    /// Everything the player needs for the next shot: distance (and how it plays), lie and what it does to the
    /// strike, stance slope or the green read, the club's expected carry from here, and where the aim is.
    /// </summary>
    private void RefreshPlan()
    {
        var round = _round;
        if (round == null || !_courseMode) return;
        var pin = round.Hole.Pin;
        bool green = round.Lie == Lie.Green, putting = Putter;
        double toPinM = round.DistanceToPin, rise = round.GroundAt(pin) - round.GroundAt(round.Ball);
        var toPin = Distance("", toPinM, short_: green);
        string toPinText = round.Holed ? "Holed" : $"{toPin.Value} {toPin.Unit}";
        string toPinNote = "";
        if (!round.Holed && !green && round.Terrain != null && Math.Abs(rise) >= 1)
            toPinNote = $"Plays {round.PlaysLike(pin) * Units.MetresToYards:0} yd ({(rise > 0 ? "up" : "down")} {Math.Abs(rise) * 3.281:0} ft)";

        var stance = round.StanceAt(round.Ball, _aimHeading, _hud.LeftHanded);
        var preset = ClubPresets.Find(_hud.ClubCode) ?? ClubPresets.All[ClubPresets.IndexOf("I7")];
        var typical = LaunchConditions.FromDisplayUnits(preset.BallMph, preset.LaunchDeg, 0, preset.SpinRpm, 0);
        string lieEffect = putting ? "" : ActivePhysics.LieModel.Apply(typical, LieCondition.Flat(stance.Kind), preset.LoftDeg).Summary;
        string lieNote = round.Holed ? "" : putting || green ? (green ? "Putting surface" : "Putting from here") : lieEffect.Length > 0 ? lieEffect : "Clean lie";

        string readTitle, read, readNote;
        if (green && !round.Holed)
        {
            readTitle = "Green read";
            double straight = HoleRound.Heading(round.Ball, pin);
            double offset = Math.Sin(Math.IEEERemainder(round.ReadPutt().Heading - straight, 2 * Math.PI)) * toPinM * 39.37;
            read = Math.Abs(offset) < 2 ? "Straight" : Math.Abs(offset) < 24 ? $"{Math.Abs(offset):0} in {(offset > 0 ? "right" : "left")}"
                 : $"{Math.Abs(offset) / 12:0.#} ft {(offset > 0 ? "right" : "left")}";
            double inches = rise * 39.37;
            readNote = (Math.Abs(offset) < 2 ? "" : "Aim outside the hole. ") + (Math.Abs(inches) < 1 ? "Level putt" : $"{Math.Abs(inches):0} in {(inches > 0 ? "uphill" : "downhill")}");
        }
        else
        {
            readTitle = "Stance";
            double up = stance.UphillDegrees, side = stance.BallAboveFeetDegrees;
            string Slope(double deg, string pos, string neg) => $"{(deg > 0 ? pos : neg)} {Math.Abs(deg):0.0}°";
            if (round.Holed || stance.IsLevel) { read = "Level"; readNote = ""; }
            else
            {
                bool alongBigger = Math.Abs(up) >= Math.Abs(side);
                read = alongBigger ? Slope(up, "Uphill", "Downhill") : Slope(side, "Above feet", "Below feet");
                string other = alongBigger ? (Math.Abs(side) >= 0.5 ? Slope(side, "ball above feet", "ball below feet") : "")
                                           : (Math.Abs(up) >= 0.5 ? Slope(up, "uphill", "downhill") : "");
                string effect = putting ? "" : ActivePhysics.LieModel.Apply(typical, stance with { Kind = LieKind.Fairway }, preset.LoftDeg).Summary;
                readNote = string.Join(" · ", new[] { other, effect }.Where(x => x.Length > 0));
            }
        }

        string clubNote;
        if (putting) clubNote = green ? "" : "Rolling it from off the green";
        else
        {
            double carry = Caddie.CarryFrom(preset.Code, stance) * Units.MetresToYards, clean = Caddie.Carry(preset.Code) * Units.MetresToYards;
            clubNote = Math.Abs(carry - clean) >= 3 ? $"Carry ≈ {carry:0} yd ({clean:0} from a clean lie)" : $"Carry ≈ {carry:0} yd";
        }

        // Aim relative to the target in play: the landing area off the tee of a longer hole, otherwise the pin.
        var target = round.DefaultTarget;
        string targetName = target == pin ? (green || putting ? "the hole" : "the pin") : "the target";
        double off = Math.IEEERemainder(_aimHeading - HoleRound.Heading(round.Ball, target), 2 * Math.PI);
        double lateral = Math.Sin(off) * round.Ball.DistanceTo(target);
        string aim = round.Holed ? "" : green || putting
            ? (Math.Abs(lateral) < 0.05 ? $"At {targetName}" : $"{Math.Abs(lateral) * 39.37:0} in {(lateral > 0 ? "right" : "left")} of {targetName}")
            : (Math.Abs(lateral) < 0.9 ? $"At {targetName}" : $"{Math.Abs(lateral) * Units.MetresToYards:0} yd {(lateral > 0 ? "right" : "left")} of {targetName}");

        if (!_hud.Meter.Busy && !round.Holed) PrepareMeter();
        _hud.SetShotPlan(new ShotPlan(toPinText, toPinNote, HoleRound.LieName(round.Lie), LieColor(round.Lie), lieNote,
            readTitle, read, readNote, clubNote, aim, green && !round.Holed, _flagWhenPutting, CanHit: !round.Holed && _outcome == null,
            HoleDone: round.Holed && _outcome == null));
    }

    private static Color LieColor(Lie lie) => lie switch
    {
        Lie.Tee or Lie.Fairway => new Color(0.45f, 0.78f, 0.36f),
        Lie.Fringe => new Color(0.58f, 0.84f, 0.44f),
        Lie.Green => new Color(0.66f, 0.92f, 0.50f),
        Lie.Rough => new Color(0.70f, 0.74f, 0.36f),
        Lie.DeepRough => new Color(0.88f, 0.64f, 0.30f),
        Lie.Bunker => new Color(0.94f, 0.85f, 0.62f),
        Lie.Water => new Color(0.42f, 0.66f, 0.96f),
        _ => UiTheme.Danger,
    };

    /// <summary>Where the ball sits at a course point (on the ground; the player adds the surface layer).</summary>
    private Vector3 BallAt(P2 p) => Scenery.ToGodot(p, (float)(_round?.GroundAt(p) ?? 0));

    /// <summary>Flag in or out while putting (it's pulled by default once the ball is on the green).</summary>
    private void ToggleFlag()
    {
        _flagWhenPutting = !_flagWhenPutting;
        UpdateAim();
    }

    /// <summary>Turns the green's heat map and rolling-dot grid on or off.</summary>
    private void SetGreenOverlay(bool heatMap, bool grid)
    {
        _heatMap = heatMap;
        _grid = grid;
        _course?.SetGreenOverlay(heatMap, grid);
        _hud.SetGreenOverlays(heatMap, grid);
    }

    private void Aim(double degrees)
    {
        if (_round == null || _round.Holed || _outcome != null) return;
        _aimHeading += degrees * Math.PI / 180;
        UpdateAim();
    }

    /// <summary>Aims at the course point under <paramref name="mapPixel"/> on the minimap (straight down, so the ray's origin is the point).</summary>
    private void AimAtMap(Vector2 mapPixel)
    {
        var round = _round;
        if (!_courseMode || round == null || round.Holed || _outcome != null) return;
        var at = _mapCamera.ProjectRayOrigin(mapPixel);
        var target = new P2(-at.Z, at.X);
        if (round.Ball.DistanceTo(target) < 0.5) return;
        _aimHeading = HoleRound.Heading(round.Ball, target);
        UpdateAim();
    }

    /// <summary>Redraws the aim line (to the club's carry, or the pin if closer), moves the camera behind it and refreshes the plan.</summary>
    private void UpdateAim(bool snapCamera = false)
    {
        var round = _round;
        if (round == null || _course == null) return;
        bool putting = Putter;
        double toPin = round.DistanceToPin;
        double length = putting ? toPin : Math.Min(Caddie.Carry(_hud.ClubCode), Math.Max(toPin, round.Ball.DistanceTo(round.DefaultTarget)));
        // Putting, the cup is the target: no ring over it.
        _course.SetAim(round.Ball, _aimHeading, Math.Max(length, 1), visible: !round.Holed && _outcome == null, showRing: !putting);

        var dir = new Vector3((float)Math.Sin(_aimHeading), 0, (float)-Math.Cos(_aimHeading));
        _camera.SetHome(BallAt(round.Ball), dir, putting || round.Lie == Lie.Green, snapCamera);
        // Pull the flag once on the green (unless it's wanted in); it goes back in when the hole is finished.
        bool onGreen = round.Lie == Lie.Green && !round.Holed;
        _course.SetFlagIn(!onGreen || _flagWhenPutting, Scenery.ToGodot(round.Hole.Pin) - Scenery.ToGodot(round.Ball));
        RefreshPlan();
    }

    private static string HoleTitle(Hole h) =>
        string.Join(" · ", new[] { $"Hole {h.Number}", h.Name, $"Par {h.Par}", $"{h.LengthYards:0} yd" }.Where(s => s.Length > 0));

    // ---- development tools -------------------------------------------------------------------

    private string? _screenshotPath;
    private int _demoShotsLeft;
    private double _devClock, _screenshotAt = 4, _nextDemoAt = 1.5;

    private void SetupDevTools(string[] args)
    {
        string? Arg(string name) => args.FirstOrDefault(a => a.StartsWith(name + "="))?[(name.Length + 1)..];
        _screenshotPath = Arg("--screenshot");
        if (double.TryParse(Arg("--screenshot-delay"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) _screenshotAt = d;
        if (int.TryParse(Arg("--demo-shots"), out var n)) _demoShotsLeft = n;
        if (args.Contains("--monitor-drawer")) _hud.ToggleDrawer();
        if (args.Contains("--tuner")) _tuner.Toggle();
        if (args.Contains("--green-read")) SetGreenOverlay(true, true);
        // --swing-at=2,2.8,3.4: press Space (the swing meter) at these times (seconds after start).
        if (Arg("--swing-at") is { } at)
            _swingPresses = new Queue<double>(at.Split(',').Select(t => double.Parse(t, System.Globalization.CultureInfo.InvariantCulture)));
        if (Arg("--tuner-tab") is { } tab) _tuner.ShowTab(tab);
        // --tune=key=value: after the demo shots, change one tuning value and replay the last shot.
        if (Arg("--tune") is { } tune && tune.Split('=') is [var key, var value] && PhysicsTuning.Parameters.Any(p => p.Key == key) &&
            double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
            _devTune = (key, v);
    }

    private (string Key, double Value)? _devTune;
    private Queue<double>? _swingPresses;

    /// <summary>Hits the requested demo shots one after another, then takes the screenshot and quits.</summary>
    private void RunDevTools(double delta)
    {
        if (_screenshotPath == null && _demoShotsLeft == 0 && _swingPresses == null) return;
        _devClock += delta;
        if (_swingPresses is { Count: > 0 } presses && _devClock >= presses.Peek())
        {
            presses.Dequeue();
            SwingPress();
        }
        bool busy = _player.IsPlaying || _outcome != null;
        if (_demoShotsLeft > 0 && !busy && _devClock >= _nextDemoAt && _player.IdleSeconds > 0.5)
        {
            _demoShotsLeft--;
            Demo();
            _nextDemoAt = _devClock + 0.5;
            return;
        }
        if (_devTune is { } t && _demoShotsLeft == 0 && !busy && _player.IdleSeconds > 0.5)
        {
            _devTune = null;
            ActivePhysics.Apply(ActivePhysics.Tuning.With(t.Key, t.Value));
            Replay();
            return;
        }
        if (_screenshotPath != null && _demoShotsLeft == 0 && !busy && _devClock >= _screenshotAt && _player.IdleSeconds > 1.2)
        {
            string path = _screenshotPath;
            _screenshotPath = null;
            GetViewport().GetTexture().GetImage().SavePng(path);
            GD.Print($"Saved screenshot to {path}");
            if (_tuner.Visible)
            {
                string tunerPath = Path.ChangeExtension(path, null) + "-tuner.png";
                _tuner.GetTexture().GetImage().SavePng(tunerPath);
                GD.Print($"Saved tuner screenshot to {tunerPath}");
            }
            GetTree().Quit();
        }
    }

    // ---- helpers -----------------------------------------------------------------------------

    private void Post(Action a) => _pending.Enqueue(a);

    private void Log(string m)
    {
        string line = $"{DateTime.Now:HH:mm:ss}  {m}";
        GD.Print(line);
        _hud.Log(line);
    }

    private static string ChargingText(int state) => state switch
    {
        0 => "not charging", 1 => "on battery", 2 => "charging", 3 => "full", 4 => "no battery", _ => "unknown",
    };
}
