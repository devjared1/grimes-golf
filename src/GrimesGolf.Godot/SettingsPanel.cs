using Godot;
using GrimesGolf.Core.Course;
using GrimesGolf.Core.Monitors;
using GrimesGolf.Core.Storage;

namespace GrimesGolf.Game;

/// <summary>
/// The settings screen, over everything: the home player, players (1-4: name and handedness), house rules (with the gimme
/// circle, auto-aligned putts and auto-putting), the "Ready" chime, the launch monitor,
/// text size, full screen and graphics quality, and the camera's hole flyover and landing zoom. Works with a mouse, the keyboard or a gamepad (arrows /
/// D-pad move between fields, Enter / A presses; Esc / B closes without saving). Nothing changes until it's saved.
/// </summary>
public partial class SettingsPanel : Control
{
    /// <summary>Saved: the new settings, and whether to start a new round with them now.</summary>
    public event Action<AppSettings, bool>? Applied;
    /// <summary>Edit the home player (their panel opens over this one, and saves by itself).</summary>
    public event Action? HomePressed;

    private AppSettings _current = AppSettings.Defaults;
    private Label _home = null!;
    private Button _editHome = null!;
    private PlayerListEditor _players = null!;
    private GuestListEditor _guests = null!;
    private Button _save = null!, _saveNewRound = null!;
    private SpinBox _mulligans = null!;
    private CheckBox _doublePar = null!, _autoConnect = null!, _fullScreen = null!, _flyover = null!, _soundEffects = null!, _checkForUpdates = null!,
                     _autoAlign = null!, _autoPutt = null!, _readySound = null!;
    private OptionButton _textSize = null!, _graphics = null!, _gimme = null!, _monitorType = null!;
    private LineEdit _monitorName = null!;
    private Label _roundNote = null!;

    public override void _Ready()
    {
        Visible = false;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;   // modal: nothing behind it is clickable
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.55f), MouseFilter = MouseFilterEnum.Ignore };
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(dim);

        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(UiTheme.Bar, 1), 16, 1, 24));
        panel.SetAnchorsPreset(LayoutPreset.Center);
        panel.GrowHorizontal = GrowDirection.Both;
        panel.GrowVertical = GrowDirection.Both;
        AddChild(panel);

        // Scrolls when the screen is too short for it all (large text); follows the focus for a gamepad.
        _scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true };
        panel.AddChild(_scroll);
        var v = _content = new VBoxContainer { CustomMinimumSize = new Vector2(620, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        v.AddThemeConstantOverride("separation", 10);
        _scroll.AddChild(v);

        var head = new HBoxContainer();
        var title = UiTheme.Text("Settings", 24, UiTheme.Ink, bold: true);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        head.AddChild(title);
        var close = new Button { Text = "✕", ThemeTypeVariation = "FlatButton", TooltipText = "Close without saving  [Esc, gamepad B]" };
        close.Pressed += Close;
        head.AddChild(close);
        v.AddChild(head);

        // Home player
        v.AddChild(UiTheme.Caption("Home player (loaded every time the app starts)"));
        _editHome = new Button { CustomMinimumSize = new Vector2(100, 0) };
        _editHome.Pressed += () => HomePressed?.Invoke();
        var homeRow = Row("", _editHome);
        _home = (Label)homeRow.GetChild(0);
        _home.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        v.AddChild(homeRow);
        v.AddChild(new HSeparator());

        // Players
        v.AddChild(UiTheme.Caption("Players this session (in teeing order for the first hole)"));
        _players = new PlayerListEditor();
        _players.SaveGuest += g =>
        {
            if (_current.NeedsHomePlayer || !g.Name.Equals(_current.Home!.Name, StringComparison.OrdinalIgnoreCase)) _guests.AddOrUpdate(g);
        };
        v.AddChild(_players);
        v.AddChild(new HSeparator());

        // Saved guests
        v.AddChild(UiTheme.Caption("Saved guests (kept after the app closes: name and hand)"));
        _guests = new GuestListEditor();
        _guests.Changed += UpdateSavedPlayers;
        v.AddChild(_guests);
        v.AddChild(new HSeparator());

        // House rules
        v.AddChild(UiTheme.Caption("House rules"));
        _mulligans = new SpinBox { MinValue = 0, MaxValue = 9, Step = 1, CustomMinimumSize = new Vector2(110, 0) };
        v.AddChild(Row("Mulligans per player, per round", _mulligans));
        _gimme = new OptionButton { CustomMinimumSize = new Vector2(110, 0),
                                    TooltipText = "A circle drawn round the hole: putts that finish inside it are tapped in for one more stroke" };
        foreach (int feet in HouseRules.GimmeFeetChoices) _gimme.AddItem($"{feet} ft");
        v.AddChild(Row("Gimme circle (radius round the hole)", _gimme));
        _doublePar = new CheckBox { Text = "Pick up at double par (a hole never costs more than twice its par)" };
        v.AddChild(_doublePar);
        _autoAlign = new CheckBox { Text = "Auto-align putts (aim along the read, allowing for the break; off: aim at the hole)" };
        v.AddChild(_autoAlign);
        _autoPutt = new CheckBox { Text = "Auto-putting (no putting: 1 putt inside the gimme circle, 2 inside twice that, 2 or 3 beyond)",
                                   TooltipText = "Once the ball stops on the green, the hole is finished with the putts its distance from the hole calls for" };
        v.AddChild(_autoPutt);
        var setupNote = UiTheme.Text("The game, holes, wind, weather and course conditions are picked for each round under Game Setup on New Game.", 13, UiTheme.Muted);
        setupNote.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        v.AddChild(setupNote);
        v.AddChild(new HSeparator());

        // Monitor, display, start-up
        v.AddChild(UiTheme.Caption("Launch monitor"));
        _autoConnect = new CheckBox { Text = "Connect automatically (to the last monitor used, or the first one found)" };
        v.AddChild(_autoConnect);
        _monitorType = new OptionButton { CustomMinimumSize = new Vector2(220, 0), TooltipText = "Which launch monitors to look for" };
        foreach (var t in new[] { LaunchMonitorType.Auto, LaunchMonitorType.Square, LaunchMonitorType.GarminR10 })   // in LaunchMonitorType order
            _monitorType.AddItem(t == LaunchMonitorType.Auto ? "Auto (any supported)" : MonitorIdentity.Label(t));
        v.AddChild(Row("Launch monitor", _monitorType));
        _monitorName = new LineEdit
        {
            CustomMinimumSize = new Vector2(220, 0), PlaceholderText = "Any", MaxLength = 40,
            TooltipText = "Only connect to the monitor advertising exactly this name (as shown under Devices nearby), when there are several",
        };
        v.AddChild(Row("Device name (optional)", _monitorName));
        v.AddChild(UiTheme.Caption("Updates"));
        _checkForUpdates = new CheckBox { Text = "Tell me when there's a new version (checks GitHub when the app starts)" };
        v.AddChild(_checkForUpdates);
        v.AddChild(UiTheme.Caption("Display"));
        _textSize = new OptionButton { CustomMinimumSize = new Vector2(170, 0) };
        _textSize.AddItem("Small");   // in TextSize order
        _textSize.AddItem("Normal");
        _textSize.AddItem("Large");
        _textSize.AddItem("Extra large");
        v.AddChild(Row("Text size (bigger for the mat or a projector)", _textSize));
        _fullScreen = new CheckBox { Text = "Full screen", TooltipText = "The app always opens full screen. Untick this to use a window until you close it." };
        v.AddChild(_fullScreen);
        _graphics = new OptionButton { CustomMinimumSize = new Vector2(170, 0),
                                       TooltipText = "Lower it if the ball flight stutters; Ultra wants a fast graphics card" };
        _graphics.AddItem("Low");   // in GraphicsQuality order
        _graphics.AddItem("Medium");
        _graphics.AddItem("High");
        _graphics.AddItem("Ultra");
        v.AddChild(Row("Graphics quality (shadows, lighting, smoothing)", _graphics));
        v.AddChild(UiTheme.Caption("Camera"));
        _flyover = new CheckBox { Text = "Fly down each hole before the first tee shot (Space skips it)" };
        v.AddChild(_flyover);
        v.AddChild(UiTheme.Caption("Sound"));
        _soundEffects = new CheckBox { Text = "Sound effects (club, ball, cup and crowd)" };
        v.AddChild(_soundEffects);
        _readySound = new CheckBox { Text = "\"Ready\" chime when the launch monitor sees a ball and is ready for the shot" };
        v.AddChild(_readySound);
        v.AddChild(new HSeparator());

        _roundNote = UiTheme.Text("A round is in progress: player and rule changes apply from the next round.", 13, UiTheme.Muted);
        _roundNote.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        v.AddChild(_roundNote);
        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 10);
        _save = new Button { Text = "Save", ThemeTypeVariation = "PrimaryButton", CustomMinimumSize = new Vector2(120, 44) };
        _save.Pressed += () => Apply(newRound: false);
        _saveNewRound = new Button { Text = "Save and start a new round", CustomMinimumSize = new Vector2(0, 44) };
        _saveNewRound.Pressed += () => Apply(newRound: true);
        var cancel = new Button { Text = "Cancel", ThemeTypeVariation = "FlatButton", CustomMinimumSize = new Vector2(0, 44) };
        cancel.Pressed += Close;
        buttons.AddChild(_save);
        buttons.AddChild(_saveNewRound);
        buttons.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        buttons.AddChild(cancel);
        v.AddChild(buttons);
    }

    private ScrollContainer _scroll = null!;
    private VBoxContainer _content = null!;

    /// <summary>Sizes the panel to its contents, up to the height of the screen (then it scrolls).</summary>
    public override void _Process(double delta)
    {
        if (!Visible) return;
        var want = _content.GetCombinedMinimumSize();
        float room = GetViewportRect().Size.Y - 48 - 40;   // the panel's margins and a little space above and below
        var size = new Vector2(want.X + (want.Y > room ? 14 : 0), Mathf.Min(want.Y, room));
        if (_scroll.CustomMinimumSize != size) _scroll.CustomMinimumSize = size;
    }

    private static HBoxContainer Row(string label, Control field)
    {
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 12);
        var l = UiTheme.Text(label, 14, UiTheme.Ink);
        l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        h.AddChild(l);
        h.AddChild(field);
        return h;
    }

    /// <summary>Shows the settings as they are now.</summary>
    public void Open(AppSettings current, bool roundInProgress)
    {
        _guests.Load(current.Guests);
        RefreshHome(current);
        var r = current.Rules;
        _mulligans.Value = r.Mulligans;
        // The nearest of the circles offered.
        var circles = HouseRules.GimmeFeetChoices;
        _gimme.Selected = Array.IndexOf(circles, circles.MinBy(f => Math.Abs(f - r.GimmeFeet)));
        _doublePar.ButtonPressed = r.PickUpAtDoublePar;
        _autoAlign.ButtonPressed = r.AutoAlignPutts;
        _autoPutt.ButtonPressed = r.AutoPutt;
        _autoConnect.ButtonPressed = current.AutoConnect;
        _monitorType.Selected = (int)current.MonitorType;
        _monitorName.Text = current.MonitorName ?? "";
        _checkForUpdates.ButtonPressed = current.CheckForUpdates;
        _textSize.Selected = (int)current.TextSize;
        _fullScreen.ButtonPressed = current.FullScreen;
        _graphics.Selected = (int)current.Graphics;
        _flyover.ButtonPressed = current.HoleFlyover;
        _soundEffects.ButtonPressed = current.SoundEffects;
        _readySound.ButtonPressed = current.ReadySound;
        _roundNote.Visible = roundInProgress;
        _saveNewRound.Text = roundInProgress ? "Save and start a new round" : "Save and start a round";
        Visible = true;
        _save.GrabFocus();   // a gamepad can start straight away
    }

    /// <summary>Shows the home player and the players in <paramref name="current"/> (a change to the home player can rename one).</summary>
    public void RefreshHome(AppSettings current)
    {
        _current = current;
        _home.Text = current.NeedsHomePlayer ? "None saved yet" : HomePlayerPanel.Summary(current.Home!);
        _editHome.Text = current.NeedsHomePlayer ? "Set up…" : "Edit…";
        _players.NotSavable = current.Home?.Name;
        _players.Load(current.Golfers);
        UpdateSavedPlayers();
    }

    /// <summary>The pick list follows the guests as they're edited here, before they're saved.</summary>
    private void UpdateSavedPlayers() => _players.SetSavedPlayers(_current.WithGuests(_guests.Golfers).SavedPlayers);

    public void Close()
    {
        Visible = false;
        GetViewport().GuiReleaseFocus();
    }

    private void Apply(bool newRound)
    {
        var s = _current.WithGuests(_guests.Golfers) with
        {
            Players = _players.Golfers,
            HouseRules = new HouseRules((int)_mulligans.Value, HouseRules.GimmeFeetChoices[Math.Max(0, _gimme.Selected)] * Units.FeetToMetres,
                                        _doublePar.ButtonPressed, _autoAlign.ButtonPressed, _autoPutt.ButtonPressed),
            AutoConnect = _autoConnect.ButtonPressed,
            MonitorType = (LaunchMonitorType)Math.Max(0, _monitorType.Selected),
            MonitorName = string.IsNullOrWhiteSpace(_monitorName.Text) ? null : _monitorName.Text.Trim(),
            CheckForUpdates = _checkForUpdates.ButtonPressed,
            TextSize = (TextSize)Math.Max(0, _textSize.Selected),
            FullScreen = _fullScreen.ButtonPressed,
            Graphics = (GraphicsQuality)Math.Max(0, _graphics.Selected),
            HoleFlyover = _flyover.ButtonPressed,
            SoundEffects = _soundEffects.ButtonPressed,
            ReadySound = _readySound.ButtonPressed,
        };
        Close();
        Applied?.Invoke(s, newRound);
    }
}
