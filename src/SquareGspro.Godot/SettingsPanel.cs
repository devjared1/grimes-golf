using Godot;
using SquareGspro.Core.Course;
using SquareGspro.Core.Storage;

namespace SquareGspro.Range3D;

/// <summary>
/// The settings screen, over everything: players (1-4: name and handedness), house rules, the launch monitor,
/// text size, full screen and graphics quality, and the camera's hole flyover and landing zoom. Works with a mouse, the keyboard or a gamepad (arrows /
/// D-pad move between fields, Enter / A presses; Esc / B closes without saving). Nothing changes until it's saved.
/// </summary>
public partial class SettingsPanel : Control
{
    /// <summary>Saved: the new settings, and whether to start a new round with them now.</summary>
    public event Action<AppSettings, bool>? Applied;

    private AppSettings _current = AppSettings.Defaults;
    private PlayerListEditor _players = null!;
    private Button _save = null!, _saveNewRound = null!;
    private SpinBox _mulligans = null!, _gimme = null!;
    private CheckBox _doublePar = null!, _autoConnect = null!, _fullScreen = null!, _flyover = null!;
    private OptionButton _textSize = null!, _graphics = null!;
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

        // Players
        v.AddChild(UiTheme.Caption("Players (in teeing order for the first hole)"));
        _players = new PlayerListEditor();
        v.AddChild(_players);
        v.AddChild(new HSeparator());

        // House rules
        v.AddChild(UiTheme.Caption("House rules"));
        _mulligans = new SpinBox { MinValue = 0, MaxValue = 9, Step = 1, CustomMinimumSize = new Vector2(110, 0) };
        v.AddChild(Row("Mulligans per player, per round", _mulligans));
        _gimme = new SpinBox { MinValue = 0, MaxValue = 6, Step = 0.5, Suffix = "ft", CustomMinimumSize = new Vector2(110, 0),
                               TooltipText = "Putts that finish this close are tapped in for one more stroke (0 = hole everything out)" };
        v.AddChild(Row("Gimme distance (0 = hole everything out)", _gimme));
        _doublePar = new CheckBox { Text = "Pick up at double par (a hole never costs more than twice its par)" };
        v.AddChild(_doublePar);
        var setupNote = UiTheme.Text("The game, holes, wind, weather and course conditions are picked for each round on New Game > Game Setup.", 13, UiTheme.Muted);
        setupNote.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        v.AddChild(setupNote);
        v.AddChild(new HSeparator());

        // Monitor, display, start-up
        v.AddChild(UiTheme.Caption("Launch monitor"));
        _autoConnect = new CheckBox { Text = "Connect automatically (to the last monitor used, or the first one found)" };
        v.AddChild(_autoConnect);
        v.AddChild(UiTheme.Caption("Display"));
        _textSize = new OptionButton { CustomMinimumSize = new Vector2(170, 0) };
        _textSize.AddItem("Small");   // in TextSize order
        _textSize.AddItem("Normal");
        _textSize.AddItem("Large");
        _textSize.AddItem("Extra large");
        v.AddChild(Row("Text size (bigger for the mat or a projector)", _textSize));
        _fullScreen = new CheckBox { Text = "Full screen" };
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
        _current = current;
        _players.Load(current.Golfers);
        var r = current.Rules;
        _mulligans.Value = r.Mulligans;
        _gimme.Value = Math.Round(r.GimmeMetres * 3.281 * 2) / 2;
        _doublePar.ButtonPressed = r.PickUpAtDoublePar;
        _autoConnect.ButtonPressed = current.AutoConnect;
        _textSize.Selected = (int)current.TextSize;
        _fullScreen.ButtonPressed = current.FullScreen;
        _graphics.Selected = (int)current.Graphics;
        _flyover.ButtonPressed = current.HoleFlyover;
        _roundNote.Visible = roundInProgress;
        _saveNewRound.Text = roundInProgress ? "Save and start a new round" : "Save and start a round";
        Visible = true;
        _save.GrabFocus();   // a gamepad can start straight away
    }

    public void Close()
    {
        Visible = false;
        GetViewport().GuiReleaseFocus();
    }

    private void Apply(bool newRound)
    {
        var s = _current with
        {
            Players = _players.Golfers,
            HouseRules = new HouseRules((int)_mulligans.Value, _gimme.Value / 3.281, _doublePar.ButtonPressed),
            AutoConnect = _autoConnect.ButtonPressed,
            TextSize = (TextSize)Math.Max(0, _textSize.Selected),
            FullScreen = _fullScreen.ButtonPressed,
            Graphics = (GraphicsQuality)Math.Max(0, _graphics.Selected),
            HoleFlyover = _flyover.ButtonPressed,
        };
        Close();
        Applied?.Invoke(s, newRound);
    }
}
