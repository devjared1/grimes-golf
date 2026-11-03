using Godot;
using SquareGspro.Core;
using SquareGspro.Core.Course;
using SquareGspro.Core.Storage;

namespace SquareGspro.Range3D;

public sealed record DeviceEntry(ulong Address, string Name, string AddressText, int Rssi, DateTime SeenAt);

/// <summary>One player's line on the scorecard.</summary>
public sealed record ScorecardRow(string Name, IReadOnlyList<int?> Scores, Color Color);

/// <summary>A figure shown in a stat block: caption, value and unit.</summary>
public readonly record struct Stat(string Caption, string Value, string Unit = "");

/// <summary>
/// What the player needs to choose the next course shot. Text is pre-formatted by <see cref="Main"/>; empty notes are hidden.
/// </summary>
/// <param name="ToPin">"153 yds" (feet on and around the green), "Holed" or "Picked up".</param>
/// <param name="Elevation">The rise to the pin, "up 2'" / "down 5'" (empty when level or unknown).</param>
/// <param name="UpSlope">Uphill (+) / downhill (−) along the aim, degrees; <paramref name="SideSlope"/> ball above (+) / below (−) the feet.</param>
/// <param name="Penalty">How much shorter the club carries from this lie and stance than from a clean, level one (0-1).</param>
public sealed record ShotPlan(
    string ToPin, string Elevation, string PlaysLike,
    Lie Lie, string LieName, Color LieColor, double UpSlope, double SideSlope, double Penalty, string LieNote,
    bool OnGreen, string GreenRead, string GreenReadNote,
    string ClubNote, string Aim, bool FlagIn, bool CanHit, bool HoleDone);

/// <summary>
/// All 2D UI. The app opens on the <see cref="MainMenu"/>. In play, a slim top bar (where you are, whose turn it
/// is, monitor status, Menu) is always shown; the Menu button opens the in-game menu (<see cref="MenuOverlay"/>),
/// the launch monitor details and log live in a drawer opened from the status chip, and the settings in a panel
/// over everything. Shots are hit from the screen with the <see cref="ShotControls"/> (power, shape, spin, Ready
/// light). The range view shows full shot data and the session; the course view shows the hole card, the hole
/// overview map (ball to flag), the lie window and a shot bar, a result card after each shot and an on-demand
/// scorecard. A banner across the top says when the monitor is reconnecting; short toasts confirm actions.
/// Split by area: <c>Hud.Drawer.cs</c>, <c>Hud.Range.cs</c>, <c>Hud.Course.cs</c>.
/// </summary>
public partial class Hud : Control
{
    public const int TopBarHeight = 56, RangeBarHeight = 196, RightColumnWidth = 360, ShotPanelHeight = 178;
    public static readonly string[] StatKeys =
        { "Name", "Address", "Signal", "Serial", "Hardware", "Firmware (LM)", "Firmware (MMI)", "Firmware (launcher)", "Uptime", "State", "Ball", "Shots" };

    private static readonly (string Key, string Unit)[] RangeMetrics =
    {
        ("Ball speed", "mph"), ("Club speed", "mph"), ("Smash", ""), ("Launch", ""), ("Direction", ""), ("Apex", "yd"),
        ("Spin", "rpm"), ("Spin axis", ""), ("Descent", ""), ("Offline", "yd"), ("Roll", "yd"), ("Hang time", "s"),
    };

    public event Action? ScanPressed, DisconnectPressed;
    /// <summary>HIT SHOT (or NEXT HOLE once the hole is finished).</summary>
    public event Action? HitPressed;
    public event Action? MenuPressed, NextHolePressed, FlagPressed, HeatMapPressed, GridPressed, MulliganPressed, NewRoundPressed, PlayerChipPressed;
    /// <summary>Settings saved from the settings screen; true = start a new round with them now.</summary>
    public event Action<AppSettings, bool>? SettingsApplied;
    public event Action<ulong>? ConnectPressed;
    public event Action? ClubChanged;
    /// <summary>Aim nudge in degrees (negative = left).</summary>
    public event Action<double>? AimPressed;
    /// <summary>The landing view (look at where the shot set up lands) was switched on or off.</summary>
    public event Action? LandingViewPressed;

    /// <summary>Aim nudges in degrees: a press (and each repeat while held), and with Shift / numpad 7 9 / LB RB.</summary>
    public const double AimStep = 0.25, AimStepBig = 2;

    private readonly List<Button> _zoomButtons = new();

    /// <summary>The aim buttons (‹ ›, Shift for big steps) and the landing-view toggle, for an aim row.</summary>
    private void AddAimButtons(HBoxContainer row, float width)
    {
        var left = new Button { Text = "‹", CustomMinimumSize = new Vector2(width, 36), TooltipText = $"Aim left {AimStep}°  [←, Shift = {AimStepBig}°]" };
        left.AddThemeFontSizeOverride("font_size", 22);
        left.Pressed += () => AimPressed?.Invoke(-(Input.IsKeyPressed(Key.Shift) ? AimStepBig : AimStep));
        var right = new Button { Text = "›", CustomMinimumSize = new Vector2(width, 36), TooltipText = $"Aim right {AimStep}°  [→, Shift = {AimStepBig}°]" };
        right.AddThemeFontSizeOverride("font_size", 22);
        right.Pressed += () => AimPressed?.Invoke(Input.IsKeyPressed(Key.Shift) ? AimStepBig : AimStep);
        var zoom = new Button { Text = "Zoom", ToggleMode = true, CustomMinimumSize = new Vector2(0, 36),
                                TooltipText = "Look at where the ball will land with this aim and power; again to come back  [Z, numpad 1, left stick click]" };
        zoom.Pressed += () => LandingViewPressed?.Invoke();
        _zoomButtons.Add(zoom);
        row.AddChild(left);
        row.AddChild(right);
        row.AddChild(zoom);
    }

    /// <summary>Shows the landing view as on or off on the Zoom buttons.</summary>
    public void SetLandingView(bool on)
    {
        foreach (var b in _zoomButtons) b.SetPressedNoSignal(on);
    }
    /// <summary>Range: aim at the next (+1) or previous (−1) flag.</summary>
    public event Action<int>? RangeTargetPressed;
    /// <summary>The course map was clicked (or dragged on) at this point in the minimap viewport's pixels.</summary>
    public event Action<Vector2>? MinimapClicked;

    public ShotControls Shot { get; private set; } = null!;
    public MainMenu MainMenu { get; private set; } = null!;
    public MenuOverlay Overlay { get; private set; } = null!;
    public PlayersPanel Players { get; private set; } = null!;

    private readonly Dictionary<string, Label> _stats = new();
    private readonly Dictionary<string, Label> _rangeValues = new();
    private readonly List<(string Club, double Carry, double Total, double Offline)> _session = new();
    private List<DeviceEntry> _shownDevices = new();
    private bool _connected, _courseMode, _syncingClubs;

    // Shared chrome
    private PanelContainer _topBar = null!;
    private HBoxContainer _topRow = null!;
    private Label _where = null!;
    private Button _monitorChip = null!, _playerChip = null!;
    private PanelContainer _banner = null!, _toast = null!;
    private Label _bannerText = null!, _toastText = null!;
    private Tween? _toastTween;
    private SettingsPanel _settings = null!;
    private PanelContainer _drawer = null!, _minimapFrame = null!;
    private SubViewportContainer _minimapContainer = null!;
    private MapOverlay _mapOverlay = null!;
    /// <summary>The shot controls with the Ready light and HIT SHOT: in the shot bar on a course, floating above the data bar on the range.</summary>
    private HBoxContainer _shotBox = null!;
    private PanelContainer _rangeShotSlot = null!;

    // Monitor drawer
    private Label _status = null!, _batteryText = null!;
    private ItemList _devices = null!;
    private Button _scan = null!, _connect = null!, _disconnect = null!;
    private ProgressBar _battery = null!;
    private CheckBox _leftHanded = null!;
    private RichTextLabel _log = null!;
    private string _readyText = "Not connected";
    private int? _batteryLevel;

    // Range view
    private Control _rangeView = null!;
    private OptionButton _rangeClub = null!;
    private Label _carry = null!, _total = null!, _sessionSummary = null!;
    private Tree _history = null!;
    private TreeItem _historyRoot = null!;

    // Course view
    private Control _courseView = null!;
    private Label _holeNumber = null!, _holeName = null!, _holeFacts = null!, _stroke = null!, _score = null!, _scoreNote = null!, _credit = null!;
    private Label _toPin = null!, _elevation = null!, _playsLike = null!, _lieName = null!, _lieNote = null!, _upSlope = null!, _sideSlope = null!, _penalty = null!;
    private Label _greenRead = null!, _greenReadNote = null!, _clubNote = null!, _aim = null!, _resultHeadline = null!, _resultNote = null!;
    private PanelContainer _lieChip = null!, _result = null!, _scorecardPanel = null!, _shotPanel = null!, _liePanel = null!;
    private LieView _lieView = null!;
    private Control _stanceRows = null!, _greenRows = null!;
    private HBoxContainer _resultStats = null!, _courseShotSlot = null!;
    private OptionButton _courseClub = null!;
    private Button _flag = null!, _heatMap = null!, _grid = null!, _mulligan = null!;
    private GridContainer _scorecard = null!;
    private Label _scorecardTitle = null!;

    public SubViewport Minimap { get; private set; } = null!;

    /// <summary>Minimap size in the current mode: portrait beside a hole, wider at the top of the range's right column.</summary>
    public Vector2I MinimapSize => _courseMode ? new Vector2I(260, 360) : new Vector2I(RightColumnWidth - 12, 230);

    public string ClubCode => ClubPresets.All[Math.Max(0, _rangeClub.Selected)].Code;
    public ClubPreset Club => ClubPresets.All[Math.Max(0, _rangeClub.Selected)];
    public bool LeftHanded => _leftHanded.ButtonPressed;
    public bool DrawerOpen => _drawer.Visible;

    /// <summary>Screen area covered by UI in the current mode (left, top, right, bottom), for centring the 3D view.</summary>
    public (float Left, float Top, float Right, float Bottom) ViewInsets =>
        _courseMode ? (0, TopBarHeight, 0, ShotPanelHeight + 24) : (0, TopBarHeight, RightColumnWidth + 24, RangeBarHeight);

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        Theme = UiTheme.Build();

        BuildShotControls();
        BuildRangeView();
        BuildCourseView();
        BuildMinimap();
        BuildTopBar();
        BuildDrawer();
        // The scorecard opens over everything else in the course view, drawer included.
        _courseView.RemoveChild(_scorecardPanel);
        AddChild(_scorecardPanel);
        MainMenu = new MainMenu();
        AddChild(MainMenu);
        MainMenu.SetMonitorStatus(ChipText(), _chipColor);
        Overlay = new MenuOverlay();
        AddChild(Overlay);
        Players = new PlayersPanel();
        AddChild(Players);
        BuildBannerAndToast();
        _settings = new SettingsPanel();
        _settings.Applied += (s, newRound) => SettingsApplied?.Invoke(s, newRound);
        AddChild(_settings);
        SetCourseMode(false);
    }

    // ---- shared chrome -----------------------------------------------------------------------

    private void BuildTopBar()
    {
        var bar = _topBar = new PanelContainer();
        bar.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Bar, 0, 0, 10));
        bar.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
        bar.OffsetBottom = TopBarHeight;
        AddChild(bar);

        var h = _topRow = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 10);
        bar.AddChild(h);

        var brand = new HBoxContainer();
        brand.AddThemeConstantOverride("separation", 0);
        brand.AddChild(UiTheme.Text("SQUARE", 20, UiTheme.Accent, bold: true));
        brand.AddChild(UiTheme.Text(" RANGE", 20, UiTheme.Ink, bold: true));
        var version = UiTheme.Text(" " + Version, 12, UiTheme.Muted);
        version.SizeFlagsVertical = SizeFlags.ShrinkEnd;
        brand.AddChild(version);
        h.AddChild(UiTheme.Pad(brand, 10, 0));

        // Where you are: "Driving range" or the course.
        _where = UiTheme.Text("", 17, UiTheme.Muted, bold: true);
        _where.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _where.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _where.ClipText = true;
        h.AddChild(_where);

        // Who is playing (course: whose turn; range: who the shots are saved under, click for the next player).
        _playerChip = new Button { Visible = false, CustomMinimumSize = new Vector2(130, 0), Alignment = HorizontalAlignment.Left };
        _playerChip.AddThemeFontOverride("font", UiTheme.Bold);
        _playerChip.Pressed += () => PlayerChipPressed?.Invoke();
        h.AddChild(_playerChip);

        // Monitor status (state and battery, with a coloured accent for readiness). Opens the monitor drawer.
        _monitorChip = new Button { TooltipText = "Launch monitor  [L]", CustomMinimumSize = new Vector2(250, 0), Alignment = HorizontalAlignment.Left };
        _monitorChip.Pressed += ToggleDrawer;
        h.AddChild(_monitorChip);
        UpdateChip(UiTheme.Muted);

        var menu = new Button { Text = "☰  Menu", TooltipText = "Settings, holes, players, camera, end the game  [M, Esc, gamepad Start]", CustomMinimumSize = new Vector2(110, 0) };
        menu.AddThemeFontOverride("font", UiTheme.Bold);
        menu.Pressed += () => MenuPressed?.Invoke();
        h.AddChild(menu);
        h.AddChild(new Control { CustomMinimumSize = new Vector2(4, 0), MouseFilter = MouseFilterEnum.Ignore });
    }

    /// <summary>What the top bar says about where you are ("Driving range", the course name).</summary>
    public void SetWhere(string text) => _where.Text = text;

    /// <summary>
    /// The player chip in the top bar: who's up (course) or who range shots are saved under (range; clicking picks the
    /// next player). Hidden with only one player.
    /// </summary>
    public void SetActivePlayer(string name, Color color, bool show)
    {
        _playerChip.Visible = show;
        _playerChip.Text = name;
        _playerChip.TooltipText = _courseMode ? $"{name} to play" : "Range shots are saved under this player: click for the next player";
        _playerChip.AddThemeColorOverride("font_color", color);
        _playerChip.AddThemeColorOverride("font_hover_color", color);
        foreach (var (state, bg) in new[] { ("normal", UiTheme.Control), ("hover", UiTheme.ControlHover), ("pressed", UiTheme.ControlPressed) })
        {
            var box = UiTheme.Box(bg, 8, 0, 8, 14);
            box.BorderWidthLeft = 5;
            box.BorderColor = color;
            _playerChip.AddThemeStyleboxOverride(state, box);
        }
    }

    // ---- banner, toasts, settings, menus -----------------------------------------------------

    private void BuildBannerAndToast()
    {
        _banner = new PanelContainer { Visible = false, MouseFilter = MouseFilterEnum.Ignore };
        _banner.AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(0.55f, 0.36f, 0.05f, 0.97f), 12, 1, 14, 28));
        _banner.SetAnchorsPreset(LayoutPreset.CenterTop);
        _banner.GrowHorizontal = GrowDirection.Both;
        _banner.OffsetTop = TopBarHeight + 8;
        _bannerText = UiTheme.Text("", 26, Colors.White, bold: true);
        _bannerText.HorizontalAlignment = HorizontalAlignment.Center;
        _banner.AddChild(_bannerText);
        AddChild(_banner);

        _toast = new PanelContainer { Visible = false, MouseFilter = MouseFilterEnum.Ignore };
        _toast.AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(0.06f, 0.07f, 0.085f, 0.95f), 12, 1, 14, 22));
        _toast.SetAnchorsPreset(LayoutPreset.Center);
        _toast.GrowHorizontal = GrowDirection.Both;
        _toast.GrowVertical = GrowDirection.Both;
        _toastText = UiTheme.Text("", 24, UiTheme.Ink, bold: true);
        _toast.AddChild(_toastText);
        AddChild(_toast);
    }

    /// <summary>A big banner at the top of the view, for something everyone needs to know (null hides it).</summary>
    public void SetBanner(string? text)
    {
        _banner.Visible = text != null;
        _bannerText.Text = text ?? "";
    }

    /// <summary>A short message in the middle of the screen that fades after a couple of seconds.</summary>
    public void Toast(string text, Color accent)
    {
        _toastText.Text = text;
        _toastText.AddThemeColorOverride("font_color", accent);
        _toast.Visible = true;
        _toast.Modulate = Colors.White;
        _toastTween?.Kill();
        _toastTween = CreateTween();
        _toastTween.TweenInterval(2.2);
        _toastTween.TweenProperty(_toast, "modulate:a", 0.0f, 0.5);
        _toastTween.TweenCallback(Callable.From(() => _toast.Visible = false));
    }

    public bool SettingsOpen => _settings.Visible;
    public void OpenSettings(AppSettings current, bool roundInProgress) => _settings.Open(current, roundInProgress);
    public void CloseSettings() => _settings.Close();

    /// <summary>A panel that takes all the input is open: the main menu, the in-game menu or a question, or the players panel.</summary>
    public bool ModalOpen => MainMenu.IsOpen || Overlay.IsOpen || Players.IsOpen;

    /// <summary>The main menu is up: the game's HUD is hidden behind it (the drawer can still open over it).</summary>
    public void ShowMainMenu(bool show)
    {
        _topBar.Visible = !show;
        _minimapFrame.Visible = !show;
        _rangeView.Visible = !show && !_courseMode;
        _courseView.Visible = !show && _courseMode;
        _rangeShotSlot.Visible = !show && !_courseMode;
        if (show)
        {
            _scorecardPanel.Visible = false;
            _result.Visible = false;
            Overlay.Close();
            Players.Close();
        }
        else MainMenu.Close();
    }

    /// <summary>"v0.9.0", from the project's <c>application/config/version</c>.</summary>
    public static string Version => ProjectSettings.GetSetting("application/config/version", "").AsString() is { Length: > 0 } v ? "v" + v : "";

    private void BuildMinimap()
    {
        _minimapFrame = new PanelContainer();
        _minimapFrame.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Card, 12, 1, 6));
        _minimapContainer = new SubViewportContainer { Stretch = true };
        Minimap = new SubViewport { RenderTargetUpdateMode = SubViewport.UpdateMode.Always, Msaa3D = Viewport.Msaa.Msaa2X };
        _minimapContainer.AddChild(Minimap);
        // Click (or drag) on the course map to aim there.
        _minimapContainer.GuiInput += e =>
        {
            bool aim = e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }
                    || e is InputEventMouseMotion m && m.ButtonMask.HasFlag(MouseButtonMask.Left);
            if (!aim || e is not InputEventMouse mouse) return;
            var size = _minimapContainer.Size;
            if (size.X > 0 && size.Y > 0) MinimapClicked?.Invoke(mouse.Position / size * (Vector2)Minimap.Size);
            _minimapContainer.AcceptEvent();
        };
        _minimapFrame.AddChild(_minimapContainer);
        _mapOverlay = new MapOverlay();
        _minimapContainer.AddChild(_mapOverlay);
        _minimapContainer.TooltipText = "Overview: click or drag to aim there";
        _minimapContainer.MouseDefaultCursorShape = CursorShape.Cross;
        AddChild(_minimapFrame);
    }

    /// <summary>
    /// The ball, the aim line to where the shot will land and the flag on the overview map, given as points in the
    /// minimap viewport's pixels (as the map camera projects them); a null ball hides them all.
    /// </summary>
    public void SetMapMarkers(Vector2? ball, Vector2? aim, Vector2? pin, string aimText, string pinText, Color color)
    {
        var scale = _minimapContainer.Size / (Vector2)Minimap.Size;
        _mapOverlay.Show(ball * scale, aim * scale, pin * scale, aimText, pinText, color);
    }

    public void HideMapMarkers() => _mapOverlay.Show(null, null, null, "", "", Colors.White);

    /// <summary>Places and sizes the minimap for the mode: top of the right column on the range, top right on a course.</summary>
    private void LayoutMinimap()
    {
        var size = MinimapSize;
        _minimapContainer.CustomMinimumSize = size;   // the container stretches the viewport to this
        _minimapFrame.SetAnchorsPreset(LayoutPreset.TopRight);
        _minimapFrame.OffsetRight = -16;
        _minimapFrame.OffsetLeft = -16 - size.X - 12;
        _minimapFrame.OffsetTop = TopBarHeight + 12;
        _minimapFrame.OffsetBottom = TopBarHeight + 12 + size.Y + 12;
    }

    private const string HitTip = "Hit the shot with this power, shape and spin  [Space]\nD: a random demo shot instead";

    /// <summary>The shot controls and HIT SHOT, built once and moved between the range and course layouts.</summary>
    private void BuildShotControls()
    {
        _shotBox = new HBoxContainer();
        _shotBox.AddThemeConstantOverride("separation", 16);
        Shot = new ShotControls();
        _shotBox.AddChild(Shot);
        _shotBox.AddChild(Shot.BuildHitColumn(HitTip));
        Shot.HitPressed += () => { if (_holeDone) NextHolePressed?.Invoke(); else HitPressed?.Invoke(); };

        _rangeShotSlot = new PanelContainer();
        _rangeShotSlot.AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(UiTheme.Bar, 0.9f), 12, 1, 12));
        _rangeShotSlot.SetAnchorsPreset(LayoutPreset.BottomLeft);
        _rangeShotSlot.GrowHorizontal = GrowDirection.End;
        _rangeShotSlot.GrowVertical = GrowDirection.Begin;
        _rangeShotSlot.OffsetLeft = _rangeShotSlot.OffsetRight = 16;
        _rangeShotSlot.OffsetTop = _rangeShotSlot.OffsetBottom = -RangeBarHeight - 12;
        AddChild(_rangeShotSlot);
    }

    /// <summary>Puts the shot controls in the course's shot bar or above the range's data bar.</summary>
    private void PlaceShotControls()
    {
        Control target = _courseMode ? _courseShotSlot : _rangeShotSlot;
        if (_shotBox.GetParent() == target) return;
        _shotBox.GetParent()?.RemoveChild(_shotBox);
        target.AddChild(_shotBox);
    }

    public override void _Process(double delta)
    {
        // Large text leaves less room: go compact when the UI is narrower than it was laid out for.
        bool compact = GetViewportRect().Size.X < CompactBelow;
        if (compact != _compact) SetCompact(compact);
    }

    /// <summary>UI width (in UI units) below which the compact layout is used.</summary>
    private const float CompactBelow = 1450;
    private bool _compact;
    private List<(Control Control, float Width)>? _wide;

    /// <summary>
    /// Compact layout, for large text (or a narrow window): the shot bar's and top bar's fixed-width blocks narrowed
    /// (notes wrap more) and tighter spacing.
    /// </summary>
    private void SetCompact(bool compact)
    {
        _compact = compact;
        var plan = _shotPanel.GetChild<HBoxContainer>(0);
        _wide ??= Descendants(plan).Concat(Descendants(_topRow)).Concat(Descendants(_liePanel)).Where(c => c.CustomMinimumSize.X >= 100)
            .Select(c => (c, c.CustomMinimumSize.X)).ToList();
        foreach (var (c, w) in _wide) c.CustomMinimumSize = new Vector2(compact ? w * 0.75f : w, c.CustomMinimumSize.Y);
        plan.AddThemeConstantOverride("separation", compact ? 10 : 18);
        _topRow.AddThemeConstantOverride("separation", compact ? 6 : 10);
    }

    private static IEnumerable<Control> Descendants(Node n) =>
        n.GetChildren().OfType<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));

    private OptionButton ClubPicker()
    {
        var o = new OptionButton { CustomMinimumSize = new Vector2(190, 40), TooltipText = "Club (sent to the monitor)  [numpad 8 / 2, D-pad ↑ / ↓]" };
        foreach (var c in ClubPresets.All) o.AddItem(c.Label);
        o.Selected = ClubPresets.IndexOf("I7");
        o.ItemSelected += i => SelectClubIndex((int)i);
        return o;
    }

    /// <summary>Keeps the range and course club pickers in step and raises <see cref="ClubChanged"/> once.</summary>
    private void SelectClubIndex(int i)
    {
        if (_syncingClubs) return;
        _syncingClubs = true;
        _rangeClub.Selected = i;
        _courseClub.Selected = i;
        _syncingClubs = false;
        ClubChanged?.Invoke();
    }

    // ---- mode & shared state -----------------------------------------------------------------

    public void SetCourseMode(bool course)
    {
        _courseMode = course;
        _rangeView.Visible = !course;
        _courseView.Visible = course;
        _rangeShotSlot.Visible = !course;
        if (!course)
        {
            _scorecardPanel.Visible = false;
            _result.Visible = false;
            _hint.Visible = false;
            _holeDone = false;
            Shot.Visible = true;
            Shot.Hit.Text = "HIT SHOT";
            Shot.Hit.Disabled = false;
        }
        HideMapMarkers();
        PlaceShotControls();
        LayoutMinimap();
    }

    /// <summary>Map attribution on the hole card (hidden when null).</summary>
    public void SetCredit(string? text)
    {
        _credit.Text = text ?? "";
        _credit.Visible = text != null;
    }

    /// <summary>Selects a club without the user touching the picker (raises <see cref="ClubChanged"/>).</summary>
    public void SetClub(string code)
    {
        int i = ClubPresets.IndexOf(code);
        if (i < 0 || i == _rangeClub.Selected) return;
        SelectClubIndex(i);
    }

    /// <summary>
    /// A player's club and hand, set together (a new player up): raises <see cref="ClubChanged"/> once if either
    /// changed, so the monitor gets one command.
    /// </summary>
    public void SetPlayerClub(string code, bool leftHanded)
    {
        int i = ClubPresets.IndexOf(code);
        bool handChanged = _leftHanded.ButtonPressed != leftHanded;
        _leftHanded.SetPressedNoSignal(leftHanded);
        if (i >= 0 && i != _rangeClub.Selected) SelectClubIndex(i);   // raises ClubChanged
        else if (handChanged) ClubChanged?.Invoke();
    }

    /// <summary>Next club down the bag (+1, shorter) or up it (-1, longer), from a numpad or gamepad.</summary>
    public void StepClub(int delta)
    {
        int i = Math.Clamp(_rangeClub.Selected + delta, 0, ClubPresets.All.Count - 1);
        if (i != _rangeClub.Selected) SelectClubIndex(i);
    }
}
