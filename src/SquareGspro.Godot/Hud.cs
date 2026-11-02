using Godot;
using SquareGspro.Core;

namespace SquareGspro.Range3D;

public sealed record DeviceEntry(ulong Address, string Name, string AddressText, int Rssi, DateTime SeenAt);

/// <summary>A figure shown in a stat block: caption, value and unit.</summary>
public readonly record struct Stat(string Caption, string Value, string Unit = "");

/// <summary>
/// What the player needs to choose the next course shot. Text is pre-formatted by <see cref="Main"/>; empty notes are hidden.
/// </summary>
public sealed record ShotPlan(
    string ToPin, string ToPinNote,
    string Lie, Color LieColor, string LieNote,
    string ReadTitle, string Read, string ReadNote,
    string ClubNote, string Aim, bool OnGreen, bool FlagIn, bool CanHit, bool HoleDone);

/// <summary>
/// All 2D UI. A top bar (mode, monitor status, camera) is always shown; the launch monitor details and log live in a
/// drawer opened from it. The range view shows full shot data and the session; the course view shows only what's
/// needed to pick a club and shot (distance, lie, stance or green read, club carry, aim), a result card after each
/// shot, the hole card and an on-demand scorecard.
/// </summary>
public partial class Hud : Control
{
    public const int TopBarHeight = 56, RangeBarHeight = 196, RightColumnWidth = 360, ShotPanelHeight = 128;
    public static readonly string[] StatKeys =
        { "Name", "Address", "Signal", "Serial", "Hardware", "Firmware (LM)", "Firmware (MMI)", "Firmware (launcher)", "Uptime", "State", "Ball", "Shots" };

    private static readonly (string Key, string Unit)[] RangeMetrics =
    {
        ("Ball speed", "mph"), ("Club speed", "mph"), ("Smash", ""), ("Launch", ""), ("Direction", ""), ("Apex", "yd"),
        ("Spin", "rpm"), ("Spin axis", ""), ("Descent", ""), ("Offline", "yd"), ("Roll", "yd"), ("Hang time", "s"),
    };

    public event Action? ScanPressed, DisconnectPressed, ClearPressed, CameraPressed;
    /// <summary>HIT SHOT: the same as pressing Space on the swing meter.</summary>
    public event Action? SwingPressed;

    public SwingMeter Meter { get; private set; } = null!;
    public StrikePad Strike { get; private set; } = null!;
    private PanelContainer _swingPanel = null!, _shotPanel = null!;
    public event Action? ModePressed, RestartHolePressed, NextHolePressed, FlagPressed, TunerPressed, HeatMapPressed, GridPressed;
    public event Action<ulong>? ConnectPressed;
    public event Action? ClubChanged;
    public event Action<int>? CourseSelected;
    /// <summary>Aim nudge in degrees (negative = left).</summary>
    public event Action<int>? AimPressed;
    /// <summary>The course map was clicked (or dragged on) at this point in the minimap viewport's pixels.</summary>
    public event Action<Vector2>? MinimapClicked;

    private readonly Dictionary<string, Label> _stats = new();
    private readonly Dictionary<string, Label> _rangeValues = new();
    private readonly List<(string Club, double Carry, double Total, double Offline)> _session = new();
    private List<DeviceEntry> _shownDevices = new();
    private bool _connected, _courseMode, _syncingClubs;

    // Shared chrome
    private Button _rangeTab = null!, _courseTab = null!, _monitorChip = null!, _camera = null!, _drawerButton = null!;
    private OptionButton _coursePicker = null!;
    private PanelContainer _drawer = null!, _minimapFrame = null!;
    private SubViewportContainer _minimapContainer = null!;

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
    private Label _holeNumber = null!, _holeName = null!, _holeFacts = null!, _holeProgress = null!, _credit = null!;
    private Label _toPin = null!, _toPinNote = null!, _lie = null!, _lieNote = null!, _readTitle = null!, _read = null!, _readNote = null!;
    private Label _clubNote = null!, _aim = null!, _resultHeadline = null!, _resultNote = null!;
    private PanelContainer _lieChip = null!, _result = null!, _scorecardPanel = null!;
    private HBoxContainer _resultStats = null!;
    private OptionButton _courseClub = null!;
    private Button _hit = null!, _flag = null!, _heatMap = null!, _grid = null!;
    private GridContainer _scorecard = null!;
    private Label _scorecardTitle = null!;

    public SubViewport Minimap { get; private set; } = null!;

    /// <summary>Minimap size in the current mode: portrait beside a hole, wider at the top of the range's right column.</summary>
    public Vector2I MinimapSize => _courseMode ? new Vector2I(236, 330) : new Vector2I(RightColumnWidth - 12, 230);

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

        BuildRangeView();
        BuildCourseView();
        BuildSwingPanel();
        BuildMinimap();
        BuildTopBar();
        BuildDrawer();
        SetCourseMode(false);
    }

    // ---- shared chrome -----------------------------------------------------------------------

    private void BuildTopBar()
    {
        var bar = new PanelContainer();
        bar.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Bar, 0, 0, 10));
        bar.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
        bar.OffsetBottom = TopBarHeight;
        AddChild(bar);

        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 10);
        bar.AddChild(h);

        var brand = new HBoxContainer();
        brand.AddThemeConstantOverride("separation", 0);
        brand.AddChild(UiTheme.Text("SQUARE", 20, UiTheme.Accent, bold: true));
        brand.AddChild(UiTheme.Text(" RANGE", 20, UiTheme.Ink, bold: true));
        h.AddChild(UiTheme.Pad(brand, 10, 0));

        var tabs = new HBoxContainer();
        tabs.AddThemeConstantOverride("separation", 4);
        var group = new ButtonGroup();
        _rangeTab = new Button { Text = "Range", ToggleMode = true, ButtonGroup = group, ThemeTypeVariation = "TabButton", TooltipText = "Driving range  [M]" };
        _courseTab = new Button { Text = "Course", ToggleMode = true, ButtonGroup = group, ThemeTypeVariation = "TabButton", TooltipText = "Play a course  [M]" };
        _rangeTab.Pressed += () => { if (_courseMode) ModePressed?.Invoke(); };
        _courseTab.Pressed += () => { if (!_courseMode) ModePressed?.Invoke(); };
        tabs.AddChild(_rangeTab);
        tabs.AddChild(_courseTab);
        h.AddChild(tabs);

        _coursePicker = new OptionButton { TooltipText = "Course to play", CustomMinimumSize = new Vector2(220, 0) };
        _coursePicker.ItemSelected += i => CourseSelected?.Invoke((int)i);
        h.AddChild(_coursePicker);

        h.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore });

        // Monitor status (state and battery, with a coloured accent for readiness). Opens the monitor drawer.
        _monitorChip = new Button { TooltipText = "Launch monitor  [L]", CustomMinimumSize = new Vector2(250, 0), Alignment = HorizontalAlignment.Left };
        _monitorChip.Pressed += ToggleDrawer;
        h.AddChild(_monitorChip);
        UpdateChip(UiTheme.Muted);

        _camera = new Button { TooltipText = "Camera: follow the ball or stay behind it  [C]" };
        _camera.Pressed += () => CameraPressed?.Invoke();
        SetCameraLabel(true);
        h.AddChild(_camera);
        _drawerButton = new Button { Text = "Monitor", TooltipText = "Launch monitor, device details and log  [L]" };
        _drawerButton.Pressed += ToggleDrawer;
        h.AddChild(_drawerButton);
        var tuner = new Button { Text = "Physics", TooltipText = "Physics tuner: adjust the physics between shots, in its own window  [T]" };
        tuner.Pressed += () => TunerPressed?.Invoke();
        h.AddChild(tuner);
        h.AddChild(new Control { CustomMinimumSize = new Vector2(4, 0), MouseFilter = MouseFilterEnum.Ignore });
    }

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
            if (!aim || !_courseMode || e is not InputEventMouse mouse) return;
            var size = _minimapContainer.Size;
            if (size.X > 0 && size.Y > 0) MinimapClicked?.Invoke(mouse.Position / size * (Vector2)Minimap.Size);
            _minimapContainer.AcceptEvent();
        };
        _minimapFrame.AddChild(_minimapContainer);
        AddChild(_minimapFrame);
    }

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

    private const string SwingTip = "Swing: Space (or click) to start the backswing, again to set the power, again on the line to strike  [Space]\n"
                                    + "D: a random demo shot instead";

    /// <summary>
    /// The swing meter and strike point, floating above the bottom panel to one side, clear of the ball in the middle of
    /// the view: above HIT SHOT on a course, bottom left of the view on the range.
    /// </summary>
    private void BuildSwingPanel()
    {
        _swingPanel = new PanelContainer();
        _swingPanel.AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(UiTheme.Bar, 0.85f), 12, 1, 10));
        _swingPanel.GrowVertical = GrowDirection.Begin;
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 12);
        Strike = new StrikePad();
        Meter = new SwingMeter { SizeFlagsVertical = SizeFlags.ShrinkCenter };
        h.AddChild(Strike);
        h.AddChild(Meter);
        _swingPanel.AddChild(h);
        AddChild(_swingPanel);
    }

    /// <summary>Keeps the swing panel just above the bottom panel of the current mode, to the side.</summary>
    private void LayoutSwingPanel()
    {
        if (_courseMode)
        {
            // Right edge lined up with the shot panel's (it's centred, so this follows the window width).
            _swingPanel.SetAnchorsPreset(LayoutPreset.CenterBottom);
            _swingPanel.GrowHorizontal = GrowDirection.Begin;
            float half = _shotPanel.Size.X / 2;
            _swingPanel.OffsetLeft = _swingPanel.OffsetRight = half;
        }
        else
        {
            _swingPanel.SetAnchorsPreset(LayoutPreset.BottomLeft);
            _swingPanel.GrowHorizontal = GrowDirection.End;
            _swingPanel.OffsetLeft = _swingPanel.OffsetRight = 16;
        }
        float bottom = _courseMode ? 16 + _shotPanel.Size.Y + 12 : RangeBarHeight + 12;
        _swingPanel.OffsetTop = _swingPanel.OffsetBottom = -bottom;
    }

    public override void _Process(double delta)
    {
        // The shot panel's size follows its contents; keep the swing panel lined up with it.
        if (_courseMode && _shotPanel.Size != _laidOutFor) { _laidOutFor = _shotPanel.Size; LayoutSwingPanel(); }
    }

    private Vector2 _laidOutFor;

    private void BuildDrawer()
    {
        _drawer = new PanelContainer { Visible = false };
        _drawer.AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(UiTheme.Bar, 1), 0, 1, 18));   // opaque: it covers the shot data
        _drawer.SetAnchorsAndOffsetsPreset(LayoutPreset.LeftWide);
        _drawer.OffsetTop = TopBarHeight;
        _drawer.OffsetRight = 380;
        AddChild(_drawer);

        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 8);
        _drawer.AddChild(v);

        var head = new HBoxContainer();
        var title = UiTheme.Text("Launch monitor", 18, UiTheme.Ink, bold: true);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        head.AddChild(title);
        var close = new Button { Text = "✕", ThemeTypeVariation = "FlatButton", TooltipText = "Close  [L]" };
        close.Pressed += ToggleDrawer;
        head.AddChild(close);
        v.AddChild(head);
        _status = UiTheme.Text("Not connected", 13, UiTheme.Muted);
        v.AddChild(_status);

        var devHead = new HBoxContainer();
        var devCaption = UiTheme.Caption("Devices nearby");
        devCaption.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        devHead.AddChild(devCaption);
        _scan = new Button { Text = "Scan", ThemeTypeVariation = "FlatButton" };
        _scan.Pressed += () => ScanPressed?.Invoke();
        devHead.AddChild(_scan);
        v.AddChild(devHead);

        // No type-to-search, so keyboard shortcuts still work while the list has focus.
        _devices = new ItemList { CustomMinimumSize = new Vector2(0, 100), AllowSearch = false };
        _devices.ItemSelected += _ => UpdateButtons();
        _devices.ItemActivated += _ => { if (!_connect.Disabled) _connect.EmitSignal(BaseButton.SignalName.Pressed); };
        v.AddChild(_devices);
        var hint = UiTheme.Text("Close the Square phone app first, and don't pair the monitor in Windows settings.", 12, UiTheme.Muted);
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        v.AddChild(hint);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 8);
        _connect = new Button { Text = "Connect", Disabled = true, SizeFlagsHorizontal = SizeFlags.ExpandFill, ThemeTypeVariation = "PrimaryButton" };
        _connect.Pressed += () => { if (SelectedDevice is { } d) ConnectPressed?.Invoke(d.Address); };
        _disconnect = new Button { Text = "Disconnect", Disabled = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _disconnect.Pressed += () => DisconnectPressed?.Invoke();
        buttons.AddChild(_connect);
        buttons.AddChild(_disconnect);
        v.AddChild(buttons);

        v.AddChild(UiTheme.Caption("Device"));
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 16);
        grid.AddThemeConstantOverride("v_separation", 3);
        foreach (var key in StatKeys)
        {
            grid.AddChild(UiTheme.Text(key, 13, UiTheme.Muted));
            var val = UiTheme.Text("—", 13, UiTheme.Ink);
            val.ClipText = true;
            val.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            grid.AddChild(val);
            _stats[key] = val;
        }
        v.AddChild(grid);

        v.AddChild(UiTheme.Caption("Battery"));
        _battery = new ProgressBar { MaxValue = 100, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 20) };
        _batteryText = UiTheme.Text("—", 12, UiTheme.Ink);
        _batteryText.HorizontalAlignment = HorizontalAlignment.Center;
        _batteryText.VerticalAlignment = VerticalAlignment.Center;
        _batteryText.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _battery.AddChild(_batteryText);
        v.AddChild(_battery);

        _leftHanded = new CheckBox { Text = "Left-handed golfer" };
        _leftHanded.Toggled += _ => ClubChanged?.Invoke();
        v.AddChild(_leftHanded);

        v.AddChild(UiTheme.Caption("Log"));
        _log = new RichTextLabel { ScrollFollowing = true, SizeFlagsVertical = SizeFlags.ExpandFill, SelectionEnabled = true };
        _log.AddThemeFontSizeOverride("normal_font_size", 11);
        _log.AddThemeColorOverride("default_color", UiTheme.Muted);
        v.AddChild(_log);
    }

    public void ToggleDrawer() => _drawer.Visible = !_drawer.Visible;

    private OptionButton ClubPicker()
    {
        var o = new OptionButton { CustomMinimumSize = new Vector2(190, 40), TooltipText = "Club (sent to the monitor)" };
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

    // ---- range view --------------------------------------------------------------------------

    private void BuildRangeView()
    {
        _rangeView = new Control { MouseFilter = MouseFilterEnum.Ignore };
        _rangeView.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_rangeView);

        var bar = new PanelContainer();
        bar.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Bar, 0, 0, 16));
        bar.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
        bar.OffsetTop = -RangeBarHeight;
        _rangeView.AddChild(bar);

        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 16);
        bar.AddChild(h);

        // Club and actions.
        var controls = new VBoxContainer { CustomMinimumSize = new Vector2(210, 0) };
        controls.AddThemeConstantOverride("separation", 8);
        controls.AddChild(UiTheme.Caption("Club"));
        _rangeClub = ClubPicker();
        controls.AddChild(_rangeClub);
        var hit = new Button { Text = "HIT SHOT", ThemeTypeVariation = "PrimaryButton", CustomMinimumSize = new Vector2(0, 48),
                               TooltipText = SwingTip };
        hit.Pressed += () => SwingPressed?.Invoke();
        controls.AddChild(hit);
        var clear = new Button { Text = "Clear session", ThemeTypeVariation = "FlatButton" };
        clear.Pressed += () => ClearPressed?.Invoke();
        controls.AddChild(clear);
        h.AddChild(controls);
        h.AddChild(new VSeparator());

        // Headline numbers.
        var hero = new HBoxContainer();
        hero.AddThemeConstantOverride("separation", 28);
        hero.AddChild(HeroStat("Carry", "yd", out _carry));
        hero.AddChild(HeroStat("Total", "yd", out _total));
        h.AddChild(hero);
        h.AddChild(new VSeparator());

        // Everything else.
        var grid = new GridContainer { Columns = 6, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 8);
        grid.AddThemeConstantOverride("v_separation", 8);
        foreach (var (key, unit) in RangeMetrics)
        {
            var tile = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            tile.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Card, 10, 1, 10));
            var tv = new VBoxContainer();
            tv.AddThemeConstantOverride("separation", 0);
            tv.AddChild(UiTheme.Caption(key));
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 4);
            var value = UiTheme.Text("—", 26, UiTheme.Ink, bold: true);
            row.AddChild(value);
            if (unit.Length > 0)
            {
                var u = UiTheme.Text(unit, 13, UiTheme.Muted);
                u.SizeFlagsVertical = SizeFlags.ShrinkEnd;
                row.AddChild(u);
            }
            tv.AddChild(row);
            tile.AddChild(tv);
            grid.AddChild(tile);
            _rangeValues[key] = value;
        }
        h.AddChild(grid);

        // Session history down the right, under the minimap.
        var column = new PanelContainer();
        column.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Card, 12, 1, 14));
        column.SetAnchorsPreset(LayoutPreset.RightWide);
        column.OffsetLeft = -16 - RightColumnWidth;
        column.OffsetRight = -16;
        column.OffsetTop = TopBarHeight + 12 + 230 + 12 + 12;
        column.OffsetBottom = -RangeBarHeight - 12;
        _rangeView.AddChild(column);
        var cv = new VBoxContainer();
        cv.AddThemeConstantOverride("separation", 6);
        cv.AddChild(UiTheme.Caption("Session"));
        _sessionSummary = UiTheme.Text("No shots yet. Hit one with the monitor, or press HIT SHOT.", 13, UiTheme.Muted);
        _sessionSummary.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        cv.AddChild(_sessionSummary);
        _history = new Tree { Columns = 7, ColumnTitlesVisible = true, HideRoot = true, SelectMode = Tree.SelectModeEnum.Row, SizeFlagsVertical = SizeFlags.ExpandFill };
        string[] cols = { "#", "Club", "Ball", "Spin", "Carry", "Total", "Off" };
        for (int i = 0; i < cols.Length; i++)
        {
            _history.SetColumnTitle(i, cols[i]);
            _history.SetColumnExpand(i, i is not 0);
            _history.SetColumnTitleAlignment(i, HorizontalAlignment.Left);
            if (i == 0) _history.SetColumnCustomMinimumWidth(0, 34);
        }
        _historyRoot = _history.CreateItem();
        cv.AddChild(_history);
        column.AddChild(cv);
    }

    private static Control HeroStat(string caption, string unit, out Label value)
    {
        var v = new VBoxContainer { CustomMinimumSize = new Vector2(140, 0), SizeFlagsVertical = SizeFlags.ShrinkCenter };
        v.AddThemeConstantOverride("separation", 0);
        v.AddChild(UiTheme.Caption(caption));
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        value = UiTheme.Text("—", 64, UiTheme.Ink, bold: true);
        row.AddChild(value);
        var u = UiTheme.Text(unit, 18, UiTheme.Muted);
        u.SizeFlagsVertical = SizeFlags.ShrinkEnd;
        row.AddChild(u);
        v.AddChild(row);
        return v;
    }

    /// <summary>Shows a range shot: headline and detail numbers, history row and session summary.</summary>
    public void AddRangeShot(string number, string club, RangeShot s, FlightResult f)
    {
        _carry.Text = f.LandingIndex == 0 ? "—" : $"{f.CarryYards:0}";
        _total.Text = $"{f.TotalYards:0}";
        void Set(string key, string value) => _rangeValues[key].Text = value;
        Set("Ball speed", $"{s.BallMph:0.0}");
        Set("Club speed", s.ClubMph is > 0 ? $"{s.ClubMph:0.0}" : "—");
        Set("Smash", s.Smash is > 0 ? $"{s.Smash:0.00}" : "—");
        Set("Launch", $"{s.LaunchDeg:0.0}°");
        Set("Direction", RangeShot.Side(s.DirectionDeg) + "°");
        Set("Apex", f.LandingIndex == 0 ? "—" : $"{f.ApexYards:0}");
        Set("Spin", $"{s.SpinRpm:0}");
        Set("Spin axis", RangeShot.Side(s.SpinAxisDeg) + "°");
        Set("Descent", f.LandingIndex == 0 ? "—" : $"{f.DescentDeg:0}°");
        Set("Offline", RangeShot.Side(f.LateralYards, "0"));
        Set("Roll", f.LandingIndex == 0 ? "—" : $"{f.TotalYards - f.CarryYards:0}");
        Set("Hang time", f.LandingIndex == 0 ? "—" : $"{f.HangTimeSec:0.0}");

        var row = _history.CreateItem(_historyRoot, 0);
        string[] vals = { number, club, $"{s.BallMph:0}", $"{s.SpinRpm:0}", f.LandingIndex == 0 ? "—" : $"{f.CarryYards:0}", $"{f.TotalYards:0}", RangeShot.Side(f.LateralYards, "0") };
        for (int i = 0; i < vals.Length; i++) row.SetText(i, vals[i]);

        if (f.LandingIndex > 0) _session.Add((club, f.CarryYards, f.TotalYards, f.LateralYards));
        UpdateSessionSummary(club);
    }

    private void UpdateSessionSummary(string club)
    {
        var mine = _session.Where(x => x.Club == club).ToList();
        int n = _historyRoot.GetChildCount();
        string shots = n == 1 ? "1 shot" : $"{n} shots";
        if (mine.Count == 0) { _sessionSummary.Text = shots; return; }
        double carry = mine.Average(x => x.Carry), total = mine.Average(x => x.Total);
        double spread = mine.Count > 1 ? Math.Sqrt(mine.Average(x => Math.Pow(x.Offline - mine.Average(y => y.Offline), 2))) : 0;
        _sessionSummary.Text = $"{shots} · {club} average {carry:0} carry, {total:0} total"
                             + (mine.Count > 1 ? $", ±{spread:0} yd offline ({mine.Count})" : "");
    }

    public void ClearShots()
    {
        _carry.Text = _total.Text = "—";
        foreach (var t in _rangeValues.Values) t.Text = "—";
        foreach (var child in _historyRoot.GetChildren()) child.Free();
        _session.Clear();
        _sessionSummary.Text = "No shots yet. Hit one with the monitor, or press HIT SHOT.";
    }

    // ---- course view -------------------------------------------------------------------------

    private void BuildCourseView()
    {
        _courseView = new Control { MouseFilter = MouseFilterEnum.Ignore };
        _courseView.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_courseView);

        BuildHoleCard();
        BuildShotPanel();
        BuildResultCard();
        BuildScorecard();
    }

    private void BuildHoleCard()
    {
        var card = new PanelContainer();
        card.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Card, 12, 1, 16));
        card.Position = new Vector2(16, TopBarHeight + 12);
        card.CustomMinimumSize = new Vector2(300, 0);
        _courseView.AddChild(card);

        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 2);
        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 10);
        _holeNumber = UiTheme.Text("HOLE 1", 26, UiTheme.Ink, bold: true);
        top.AddChild(_holeNumber);
        _holeFacts = UiTheme.Text("", 15, UiTheme.Muted);
        _holeFacts.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        top.AddChild(_holeFacts);
        v.AddChild(top);
        _holeName = UiTheme.Text("", 14, UiTheme.Muted);
        v.AddChild(_holeName);
        v.AddChild(new HSeparator());
        _holeProgress = UiTheme.Text("", 15, UiTheme.Ink);
        v.AddChild(_holeProgress);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 6);
        var scorecard = new Button { Text = "Scorecard", ThemeTypeVariation = "FlatButton", TooltipText = "Show the scorecard  [S]" };
        scorecard.Pressed += ToggleScorecard;
        var restart = new Button { Text = "Restart", ThemeTypeVariation = "FlatButton", TooltipText = "Restart this hole  [R]" };
        restart.Pressed += () => RestartHolePressed?.Invoke();
        var next = new Button { Text = "Next hole", ThemeTypeVariation = "FlatButton", TooltipText = "Go to the next hole  [N]" };
        next.Pressed += () => NextHolePressed?.Invoke();
        buttons.AddChild(scorecard);
        buttons.AddChild(restart);
        buttons.AddChild(next);
        v.AddChild(buttons);
        _credit = UiTheme.Text("", 11, UiTheme.Muted);
        _credit.Visible = false;
        v.AddChild(_credit);
        card.AddChild(v);
    }

    /// <summary>One labelled block of the shot panel: caption, main value, optional note.</summary>
    private static VBoxContainer PlanBlock(string caption, out Label value, out Label note, float width, out Label captionLabel)
    {
        var v = new VBoxContainer { CustomMinimumSize = new Vector2(width, 0) };
        v.AddThemeConstantOverride("separation", 2);
        captionLabel = UiTheme.Caption(caption);
        v.AddChild(captionLabel);
        value = UiTheme.Text("—", 28, UiTheme.Ink, bold: true);
        v.AddChild(value);
        note = UiTheme.Text("", 13, UiTheme.Muted);
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        note.CustomMinimumSize = new Vector2(width, 0);
        v.AddChild(note);
        return v;
    }

    private void BuildShotPanel()
    {
        var panel = _shotPanel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Bar, 16, 1, 18));
        panel.SetAnchorsPreset(LayoutPreset.CenterBottom);
        panel.GrowHorizontal = GrowDirection.Both;
        panel.GrowVertical = GrowDirection.Begin;
        panel.OffsetBottom = -16;
        panel.OffsetTop = -16 - ShotPanelHeight;
        _courseView.AddChild(panel);

        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 22);
        panel.AddChild(h);

        h.AddChild(PlanBlock("To the pin", out _toPin, out _toPinNote, 150, out _));
        h.AddChild(new VSeparator());

        var lieBlock = new VBoxContainer { CustomMinimumSize = new Vector2(190, 0) };
        lieBlock.AddThemeConstantOverride("separation", 4);
        lieBlock.AddChild(UiTheme.Caption("Lie"));
        _lieChip = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
        _lie = UiTheme.Text("Tee", 18, new Color(0.06f, 0.08f, 0.06f), bold: true);
        _lieChip.AddChild(_lie);
        lieBlock.AddChild(_lieChip);
        _lieNote = UiTheme.Text("", 13, UiTheme.Muted);
        _lieNote.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _lieNote.CustomMinimumSize = new Vector2(190, 0);
        lieBlock.AddChild(_lieNote);
        h.AddChild(lieBlock);
        h.AddChild(new VSeparator());

        h.AddChild(PlanBlock("Stance", out _read, out _readNote, 190, out _readTitle));
        h.AddChild(new VSeparator());

        var clubBlock = new VBoxContainer { CustomMinimumSize = new Vector2(210, 0) };
        clubBlock.AddThemeConstantOverride("separation", 4);
        clubBlock.AddChild(UiTheme.Caption("Club"));
        _courseClub = ClubPicker();
        clubBlock.AddChild(_courseClub);
        _clubNote = UiTheme.Text("", 13, UiTheme.Muted);
        clubBlock.AddChild(_clubNote);
        h.AddChild(clubBlock);
        h.AddChild(new VSeparator());

        var aimBlock = new VBoxContainer { CustomMinimumSize = new Vector2(170, 0) };
        aimBlock.AddThemeConstantOverride("separation", 4);
        aimBlock.AddChild(UiTheme.Caption("Aim"));
        var aimRow = new HBoxContainer();
        aimRow.AddThemeConstantOverride("separation", 6);
        var left = new Button { Text = "‹", CustomMinimumSize = new Vector2(40, 40), TooltipText = "Aim left 1°  [←, Shift = 5°]" };
        left.AddThemeFontSizeOverride("font_size", 22);
        left.Pressed += () => AimPressed?.Invoke(Input.IsKeyPressed(Key.Shift) ? -5 : -1);
        var right = new Button { Text = "›", CustomMinimumSize = new Vector2(40, 40), TooltipText = "Aim right 1°  [→, Shift = 5°]" };
        right.AddThemeFontSizeOverride("font_size", 22);
        right.Pressed += () => AimPressed?.Invoke(Input.IsKeyPressed(Key.Shift) ? 5 : 1);
        aimRow.AddChild(left);
        aimRow.AddChild(right);
        aimBlock.AddChild(aimRow);
        _aim = UiTheme.Text("", 13, UiTheme.Muted);
        _aim.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _aim.CustomMinimumSize = new Vector2(170, 0);
        aimBlock.AddChild(_aim);
        h.AddChild(aimBlock);

        var actions = new VBoxContainer { SizeFlagsVertical = SizeFlags.ShrinkCenter };
        actions.AddThemeConstantOverride("separation", 8);
        _hit = new Button { Text = "HIT SHOT", ThemeTypeVariation = "PrimaryButton", CustomMinimumSize = new Vector2(180, 64),
                            TooltipText = SwingTip };
        _hit.AddThemeFontSizeOverride("font_size", 20);
        _hit.Pressed += () => { if (_holeDone) NextHolePressed?.Invoke(); else SwingPressed?.Invoke(); };
        actions.AddChild(_hit);
        _flag = new Button { ThemeTypeVariation = "FlatButton", TooltipText = "Leave the flag in the hole, or pull it (the default)  [F]" };
        _flag.Pressed += () => FlagPressed?.Invoke();
        actions.AddChild(_flag);
        var read = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        read.AddThemeConstantOverride("separation", 6);
        _heatMap = new Button { ThemeTypeVariation = "FlatButton", ToggleMode = true, Text = "Heat map  [H]",
                                TooltipText = "Colour the green by height: blue low, red high  [H]" };
        _heatMap.Pressed += () => HeatMapPressed?.Invoke();
        _grid = new Button { ThemeTypeVariation = "FlatButton", ToggleMode = true, Text = "Grid  [G]",
                             TooltipText = "Grid on the green, with dots rolling downhill along it (faster = steeper)  [G]" };
        _grid.Pressed += () => GridPressed?.Invoke();
        read.AddChild(_heatMap);
        read.AddChild(_grid);
        actions.AddChild(read);
        h.AddChild(actions);
        // Lie, stance/read, club and aim (with their separators) only matter while there's a shot to play.
        _planDetails = h.GetChildren().OfType<Control>().Skip(1).Take(8).ToArray();
    }

    private Control[] _planDetails = Array.Empty<Control>();
    private bool _holeDone;

    private void BuildResultCard()
    {
        _result = new PanelContainer { Visible = false };
        _result.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Card, 14, 1, 16));
        _result.SetAnchorsPreset(LayoutPreset.CenterTop);
        _result.GrowHorizontal = GrowDirection.Both;
        _result.OffsetTop = TopBarHeight + 12;
        _courseView.AddChild(_result);

        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 6);
        _resultHeadline = UiTheme.Text("", 20, UiTheme.Ink, bold: true);
        _resultHeadline.HorizontalAlignment = HorizontalAlignment.Center;
        v.AddChild(_resultHeadline);
        _resultStats = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        _resultStats.AddThemeConstantOverride("separation", 30);
        v.AddChild(_resultStats);
        _resultNote = UiTheme.Text("", 13, UiTheme.Muted);
        _resultNote.HorizontalAlignment = HorizontalAlignment.Center;
        v.AddChild(_resultNote);
        _result.AddChild(v);
    }

    private void BuildScorecard()
    {
        _scorecardPanel = new PanelContainer { Visible = false };
        _scorecardPanel.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Bar, 16, 1, 22));
        _scorecardPanel.SetAnchorsPreset(LayoutPreset.Center);
        _scorecardPanel.GrowHorizontal = GrowDirection.Both;
        _scorecardPanel.GrowVertical = GrowDirection.Both;
        _courseView.AddChild(_scorecardPanel);
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 12);
        var head = new HBoxContainer();
        _scorecardTitle = UiTheme.Text("Scorecard", 20, UiTheme.Ink, bold: true);
        _scorecardTitle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        head.AddChild(_scorecardTitle);
        var close = new Button { Text = "✕", ThemeTypeVariation = "FlatButton", TooltipText = "Close  [S]" };
        close.Pressed += ToggleScorecard;
        head.AddChild(close);
        v.AddChild(head);
        _scorecard = new GridContainer();
        _scorecard.AddThemeConstantOverride("h_separation", 18);
        _scorecard.AddThemeConstantOverride("v_separation", 6);
        v.AddChild(_scorecard);
        _scorecardPanel.AddChild(v);
    }

    public void SetHoleCard(int number, string name, int par, double yards, string progress)
    {
        _holeNumber.Text = $"HOLE {number}";
        _holeFacts.Text = $"Par {par} · {yards:0} yd";
        _holeName.Text = name;
        _holeName.Visible = name.Length > 0;
        _holeProgress.Text = progress;
    }

    /// <summary>Shows which green-reading aids are on (the buttons also toggle themselves when clicked).</summary>
    public void SetGreenOverlays(bool heatMap, bool grid)
    {
        _heatMap.SetPressedNoSignal(heatMap);
        _grid.SetPressedNoSignal(grid);
        _heatMap.Text = heatMap ? "Heat map: on  [H]" : "Heat map  [H]";
        _grid.Text = grid ? "Grid: on  [G]" : "Grid  [G]";
    }

    public void SetShotPlan(ShotPlan p)
    {
        _toPin.Text = p.ToPin;
        SetNote(_toPinNote, p.ToPinNote);
        _lie.Text = p.Lie;
        _lieChip.AddThemeStyleboxOverride("panel", UiTheme.Box(p.LieColor, 8, 0, 4, 12));
        SetNote(_lieNote, p.LieNote);
        _readTitle.Text = p.ReadTitle.ToUpperInvariant();
        _read.Text = p.Read;
        SetNote(_readNote, p.ReadNote);
        SetNote(_clubNote, p.ClubNote);
        SetNote(_aim, p.Aim);
        _flag.Visible = p.OnGreen;
        _flag.Text = p.FlagIn ? "Flag: in  [F]" : "Flag: pulled  [F]";
        // Hole finished: the panel becomes "Holed" plus a NEXT HOLE action.
        _holeDone = p.HoleDone;
        foreach (var c in _planDetails) c.Visible = !p.HoleDone;
        _hit.Text = p.HoleDone ? "NEXT HOLE" : "HIT SHOT";
        _hit.TooltipText = p.HoleDone ? "Go to the next hole  [N]" : SwingTip;
        _hit.Disabled = !p.CanHit && !p.HoleDone;
    }

    private static void SetNote(Label l, string text)
    {
        l.Text = text;
        l.Visible = text.Length > 0;
    }

    /// <summary>The result card: a headline (where the ball finished) and a few numbers. Hidden by <see cref="HideResult"/>.</summary>
    public void ShowResult(string headline, IReadOnlyList<Stat> stats, string note = "")
    {
        _resultHeadline.Text = headline;
        foreach (var c in _resultStats.GetChildren()) c.QueueFree();
        foreach (var s in stats)
        {
            var b = new VBoxContainer();
            b.AddThemeConstantOverride("separation", 0);
            var cap = UiTheme.Caption(s.Caption);
            cap.HorizontalAlignment = HorizontalAlignment.Center;
            b.AddChild(cap);
            var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
            row.AddThemeConstantOverride("separation", 4);
            row.AddChild(UiTheme.Text(s.Value, 30, UiTheme.Ink, bold: true));
            if (s.Unit.Length > 0)
            {
                var u = UiTheme.Text(s.Unit, 14, UiTheme.Muted);
                u.SizeFlagsVertical = SizeFlags.ShrinkEnd;
                row.AddChild(u);
            }
            b.AddChild(row);
            _resultStats.AddChild(b);
        }
        _resultStats.Visible = stats.Count > 0;
        SetNote(_resultNote, note);
        _result.Visible = true;
        _result.Modulate = new Color(1, 1, 1, 0);
        CreateTween().TweenProperty(_result, "modulate:a", 1.0f, 0.25);
    }

    public void HideResult() => _result.Visible = false;

    public void ToggleScorecard() => _scorecardPanel.Visible = _courseMode && !_scorecardPanel.Visible;
    public void ShowScorecard(bool show) => _scorecardPanel.Visible = _courseMode && show;

    /// <summary>
    /// Rows: hole numbers, par, score (blank until played), with a total column. Courses longer than 9 holes are
    /// shown nine at a time (Out / In), with the overall total at the end of the back nine.
    /// </summary>
    public void SetScorecard(string title, IReadOnlyList<int> pars, IReadOnlyList<int?> scores, int current)
    {
        _scorecardTitle.Text = title;
        foreach (var c in _scorecard.GetChildren()) c.Free();
        int perRow = pars.Count > 9 ? 9 : pars.Count;
        bool split = pars.Count > perRow;
        _scorecard.Columns = perRow + (split ? 3 : 2);
        void Cell(string text, Color? color = null, bool bold = false)
        {
            var l = UiTheme.Text(text, 15, color ?? UiTheme.Ink, bold);
            l.HorizontalAlignment = HorizontalAlignment.Center;
            l.CustomMinimumSize = new Vector2(34, 0);
            _scorecard.AddChild(l);
        }
        Color ScoreColor(int i) => scores[i] is not { } s ? UiTheme.Muted
            : s < pars[i] ? UiTheme.Accent : s > pars[i] ? UiTheme.Warn : UiTheme.Ink;
        string Total(int from, int count)
        {
            var idx = Enumerable.Range(from, count).Where(i => scores[i] != null).ToList();
            if (idx.Count == 0) return "–";
            int played = idx.Sum(i => scores[i]!.Value), par = idx.Sum(i => pars[i]);
            return $"{played} ({ToPar(played - par)})";
        }

        for (int start = 0; start < pars.Count; start += perRow)
        {
            int n = Math.Min(perRow, pars.Count - start);
            bool last = start + n >= pars.Count;
            string label = !split ? "Tot" : start == 0 ? "Out" : "In";
            void Pad() { for (int i = n; i < perRow; i++) Cell(""); }

            Cell("Hole", UiTheme.Muted);
            for (int i = start; i < start + n; i++) Cell((i + 1).ToString(), i == current ? UiTheme.Accent : UiTheme.Muted, i == current);
            Pad();
            Cell(label, UiTheme.Muted);
            if (split) Cell(last ? "Tot" : "", UiTheme.Muted);

            Cell("Par", UiTheme.Muted);
            for (int i = start; i < start + n; i++) Cell(pars[i].ToString());
            Pad();
            Cell(pars.Skip(start).Take(n).Sum().ToString());
            if (split) Cell(last ? pars.Sum().ToString() : "");

            Cell("Score", UiTheme.Muted);
            for (int i = start; i < start + n; i++) Cell(scores[i]?.ToString() ?? "–", ScoreColor(i), scores[i] != null);
            Pad();
            Cell(Total(start, n), bold: true);
            if (split) Cell(last ? Total(0, pars.Count) : "", bold: true);
        }
    }

    public static string ToPar(int d) => d == 0 ? "E" : d > 0 ? $"+{d}" : d.ToString();

    // ---- mode & shared state -----------------------------------------------------------------

    public void SetCourseMode(bool course)
    {
        _courseMode = course;
        _rangeTab.SetPressedNoSignal(!course);
        _courseTab.SetPressedNoSignal(course);
        _rangeView.Visible = !course;
        _courseView.Visible = course;
        _coursePicker.Visible = course;
        if (!course) { _scorecardPanel.Visible = false; _result.Visible = false; }
        LayoutMinimap();
        LayoutSwingPanel();
    }

    public void SetCourses(IEnumerable<string> names, int selected)
    {
        _coursePicker.Clear();
        foreach (var n in names) _coursePicker.AddItem(n);
        _coursePicker.Selected = selected;
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

    public void SetCameraLabel(bool follow) => _camera.Text = follow ? "Camera: follow" : "Camera: fixed";

    // ---- monitor -----------------------------------------------------------------------------

    public void SetStatus(string text) => _status.Text = text;
    public void SetStat(string key, string value) => _stats[key].Text = value;
    public void SetScanning(bool scanning) => _scan.Text = scanning ? "Stop" : "Scan";

    public void ResetStats(bool keepShots = true)
    {
        foreach (var (k, v) in _stats) if (!keepShots || k != "Shots") v.Text = "—";
        _battery.Value = 0;
        _batteryText.Text = "—";
        _batteryLevel = null;
        UpdateChip(UiTheme.Muted);
    }

    public void SetBattery(int level, string charge)
    {
        _battery.Value = level;
        var fill = level <= 15 ? UiTheme.Danger : level <= 35 ? UiTheme.Warn : UiTheme.Accent;
        _battery.AddThemeStyleboxOverride("fill", UiTheme.Box(fill, 4, 0, 0));
        _batteryText.Text = $"{level}%  ·  {charge}";
        _batteryLevel = level;
        _monitorChip.Text = ChipText();
    }

    /// <summary>Monitor readiness in the top bar ("Ready — hit!", "Place a ball", ...).</summary>
    public void SetReady(string text, Color color)
    {
        _readyText = text;
        UpdateChip(color);
    }

    private Color _chipColor = UiTheme.Muted;

    private void UpdateChip(Color accent)
    {
        _chipColor = accent;
        _monitorChip.Text = ChipText();
        foreach (var (state, bg) in new[] { ("normal", UiTheme.Control), ("hover", UiTheme.ControlHover), ("pressed", UiTheme.ControlPressed) })
        {
            var box = UiTheme.Box(bg, 8, 0, 8, 14);
            box.BorderWidthLeft = 5;
            box.BorderColor = accent;
            _monitorChip.AddThemeStyleboxOverride(state, box);
        }
    }

    private string ChipText() => _batteryLevel is { } b ? $"{_readyText}   ·   {b}%" : _readyText;

    public void SetConnected(bool connected)
    {
        _connected = connected;
        UpdateButtons();
    }

    public void Log(string line) => _log.AddText(line + "\n");

    /// <summary>Re-renders the monitor list, keeping the selection.</summary>
    public void SetDevices(IReadOnlyList<DeviceEntry> devices)
    {
        ulong? selected = SelectedDevice?.Address;
        _shownDevices = devices.ToList();
        _devices.Clear();
        for (int i = 0; i < _shownDevices.Count; i++)
        {
            var d = _shownDevices[i];
            bool stale = (DateTime.Now - d.SeenAt).TotalSeconds > 10;
            int idx = _devices.AddItem($"{d.Name}   ·   {d.Rssi} dBm");
            _devices.SetItemTooltip(idx, d.AddressText);
            if (stale) _devices.SetItemCustomFgColor(idx, UiTheme.Muted);
            if (d.Address == selected) _devices.Select(idx);
        }
        if (selected == null && _shownDevices.Count > 0) _devices.Select(0);
        UpdateButtons();
    }

    public DeviceEntry? SelectedDevice
    {
        get
        {
            var sel = _devices.GetSelectedItems();
            return sel.Length > 0 && sel[0] < _shownDevices.Count ? _shownDevices[sel[0]] : null;
        }
    }

    private void UpdateButtons()
    {
        _connect.Disabled = _connected || SelectedDevice == null;
        _disconnect.Disabled = !_connected;
        _scan.Disabled = _connected;
    }
}
