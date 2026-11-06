using Godot;
using GrimesGolf.Core;
using GrimesGolf.Core.Course;
using GrimesGolf.Core.Storage;

namespace GrimesGolf.Game;

/// <summary>
/// The home player: asked for when the app starts with none saved, and edited from the settings. Their name, hand and
/// (optionally) the carries they know for their clubs; the launch monitor is detected, not picked. "Not now" leaves
/// the app as it was (a gamepad can't type a name, so it mustn't be a dead end), and it asks again next time.
/// </summary>
public partial class HomePlayerPanel : Control
{
    /// <summary>Carries outside this range (yards) aren't accepted.</summary>
    private const int MinYards = 10, MaxYards = 450;

    public event Action<HomePlayer>? Saved;

    private HomePlayer? _editing;
    private Control? _returnFocus;
    private Label _title = null!, _intro = null!, _monitor = null!;
    private LineEdit _name = null!;
    private Button _right = null!, _left = null!, _clubsToggle = null!, _save = null!, _cancel = null!;
    private VBoxContainer _clubs = null!, _content = null!;
    private ScrollContainer _scroll = null!;
    private readonly Dictionary<string, LineEdit> _yards = new();

    public bool IsOpen => Visible;

    public override void _Ready()
    {
        Visible = false;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.55f), MouseFilter = MouseFilterEnum.Ignore };
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(dim);

        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(UiTheme.Bar, 1), 16, 1, 24));
        panel.SetAnchorsPreset(LayoutPreset.Center);
        panel.GrowHorizontal = GrowDirection.Both;
        panel.GrowVertical = GrowDirection.Both;
        AddChild(panel);

        _scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true };
        panel.AddChild(_scroll);
        var v = _content = new VBoxContainer { CustomMinimumSize = new Vector2(620, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        v.AddThemeConstantOverride("separation", 10);
        _scroll.AddChild(v);

        _title = UiTheme.Text("", 24, UiTheme.Ink, bold: true);
        v.AddChild(_title);
        _intro = UiTheme.Text("", 14, UiTheme.Muted);
        _intro.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        v.AddChild(_intro);

        v.AddChild(UiTheme.Caption("Name"));
        _name = new LineEdit { PlaceholderText = "Your name", MaxLength = 16, CustomMinimumSize = new Vector2(320, 38), SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
        _name.AddThemeFontSizeOverride("font_size", 17);
        _name.TextChanged += _ => UpdateSave();
        _name.TextSubmitted += _ => { if (!_save.Disabled) Save(); };
        v.AddChild(_name);

        v.AddChild(UiTheme.Caption("Hand"));
        var hands = new HBoxContainer();
        hands.AddThemeConstantOverride("separation", 8);
        var group = new ButtonGroup();
        _right = HandButton("Right-handed", group);
        _left = HandButton("Left-handed", group);
        hands.AddChild(_right);
        hands.AddChild(_left);
        v.AddChild(hands);

        v.AddChild(UiTheme.Caption("Launch monitor"));
        _monitor = UiTheme.Text("", 15, UiTheme.Ink);
        _monitor.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        v.AddChild(_monitor);

        v.AddChild(new HSeparator());
        _clubsToggle = new Button { ToggleMode = true, ThemeTypeVariation = "FlatButton", Alignment = HorizontalAlignment.Left, Text = "Club distances (optional)" };
        _clubsToggle.Toggled += ShowClubs;
        v.AddChild(_clubsToggle);
        _clubs = BuildClubs();
        v.AddChild(_clubs);
        v.AddChild(new HSeparator());

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 10);
        _save = new Button { Text = "Save", ThemeTypeVariation = "PrimaryButton", CustomMinimumSize = new Vector2(120, 44) };
        _save.Pressed += Save;
        _cancel = new Button { ThemeTypeVariation = "FlatButton", CustomMinimumSize = new Vector2(0, 44) };
        _cancel.Pressed += Close;
        buttons.AddChild(_save);
        buttons.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        buttons.AddChild(_cancel);
        v.AddChild(buttons);
    }

    private static Button HandButton(string text, ButtonGroup group) =>
        new() { Text = text, ToggleMode = true, ButtonGroup = group, ThemeTypeVariation = "TabButton", CustomMinimumSize = new Vector2(150, 40) };

    private VBoxContainer BuildClubs()
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 10);
        var note = UiTheme.Text("Carry in yards. Fill in the ones you know; the grey numbers are estimates from them. These set the caddie's club "
                                + "suggestions and the aim line, and how far swings from the screen go. Shots from a launch monitor are never changed.", 13, UiTheme.Muted);
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(note);
        var grid = new GridContainer { Columns = 4 };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 6);
        foreach (var club in ClubPresets.All.Where(c => c.Code != "PT"))
        {
            var label = UiTheme.Text(club.Label, 14, UiTheme.Ink);
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            grid.AddChild(label);
            var field = new LineEdit { MaxLength = 3, CustomMinimumSize = new Vector2(76, 34), Alignment = HorizontalAlignment.Right,
                                       VirtualKeyboardType = LineEdit.VirtualKeyboardTypeEnum.Number };
            field.TextChanged += text =>
            {
                string digits = new(text.Where(char.IsAsciiDigit).ToArray());
                if (digits != text) { field.Text = digits; field.CaretColumn = digits.Length; }
                RefreshClubs();
            };
            grid.AddChild(field);
            _yards[club.Code] = field;
        }
        box.AddChild(grid);
        return box;
    }

    /// <summary>Sizes the panel to its contents, up to the height of the screen (then it scrolls).</summary>
    public override void _Process(double delta)
    {
        if (!Visible) return;
        var want = _content.GetCombinedMinimumSize();
        float room = GetViewportRect().Size.Y - 48 - 40;   // the panel's margins and a little space above and below
        var size = new Vector2(want.X + (want.Y > room ? 14 : 0), Mathf.Min(want.Y, room));
        if (_scroll.CustomMinimumSize != size) _scroll.CustomMinimumSize = size;
    }

    /// <summary>
    /// Opens with the saved player to edit, or (<paramref name="home"/> null) asks for one, starting from
    /// <paramref name="candidate"/> if there's a likely one.
    /// </summary>
    public void Open(HomePlayer? home, Golfer? candidate, bool showClubs = false)
    {
        _editing = home;
        _returnFocus = GetViewport().GuiGetFocusOwner();
        _title.Text = home == null ? "Who's playing?" : "Home player";
        _intro.Text = home == null ? "Set up your player once and they're loaded every time the app starts. Friends can be added and saved in Settings."
                                   : "Loaded every time the app starts.";
        _cancel.Text = home == null ? "Not now" : "Cancel";
        _name.Text = home?.Name ?? candidate?.Name ?? "";
        (home?.LeftHanded ?? candidate?.LeftHanded ?? false ? _left : _right).ButtonPressed = true;
        foreach (var (code, field) in _yards)
            field.Text = home?.ClubYards?.TryGetValue(code, out int yards) == true && yards > 0 ? yards.ToString() : "";
        _clubsToggle.SetPressedNoSignal(showClubs || home?.ClubYards is { Count: > 0 });
        ShowClubs(_clubsToggle.ButtonPressed);
        RefreshClubs();
        Visible = true;
        if (_name.Text.Length == 0) { _name.GrabFocus(); _name.Edit(); }
        else _save.GrabFocus();
    }

    public void Close()
    {
        if (!Visible) return;
        Visible = false;
        if (_returnFocus != null && IsInstanceValid(_returnFocus) && _returnFocus.IsVisibleInTree()) _returnFocus.GrabFocus();
        else GetViewport().GuiReleaseFocus();
        _returnFocus = null;
    }

    /// <summary>What's known about the launch monitor (it's detected by itself; this just says how far that's got).</summary>
    public void SetMonitor(string text)
    {
        if (_monitor.Text != text) _monitor.Text = text;
    }

    /// <summary>"Jared · Right-handed · Square Golf (hardware 1.5) · 3 club distances", for the settings screen.</summary>
    public static string Summary(HomePlayer home)
    {
        var parts = new List<string> { home.Name, home.LeftHanded ? "Left-handed" : "Right-handed" };
        if (home.LaunchMonitor != null) parts.Add(home.LaunchMonitor);
        if (home.ClubYards is { Count: > 0 } clubs) parts.Add(clubs.Count == 1 ? "1 club distance" : $"{clubs.Count} club distances");
        return string.Join("  ·  ", parts);
    }

    private void ShowClubs(bool on)
    {
        _clubs.Visible = on;
        _clubsToggle.Text = (on ? "▾  " : "▸  ") + "Club distances (optional)";
    }

    private static bool Valid(string text, out int yards) => int.TryParse(text, out yards) && yards is >= MinYards and <= MaxYards;

    /// <summary>The carries typed in that make sense, by club code.</summary>
    private Dictionary<string, int> KnownYards()
    {
        var known = new Dictionary<string, int>();
        foreach (var (code, field) in _yards)
            if (Valid(field.Text, out int yards)) known[code] = yards;
        return known;
    }

    /// <summary>Estimates for the clubs left blank, and a warning colour on a carry that can't be right.</summary>
    private void RefreshClubs()
    {
        var known = KnownYards();
        foreach (var (code, field) in _yards)
        {
            field.PlaceholderText = $"{Caddie.Carry(code, known) * Units.MetresToYards:0}";
            bool bad = field.Text.Length > 0 && !Valid(field.Text, out _);
            field.TooltipText = bad ? $"A carry from {MinYards} to {MaxYards} yards" : "";
            if (bad) field.AddThemeColorOverride("font_color", UiTheme.Danger);
            else field.RemoveThemeColorOverride("font_color");
        }
        UpdateSave();
    }

    private void UpdateSave() =>
        _save.Disabled = string.IsNullOrWhiteSpace(_name.Text) || _yards.Values.Any(f => f.Text.Length > 0 && !Valid(f.Text, out _));

    private void Save()
    {
        var known = KnownYards();
        var home = new HomePlayer(_name.Text.Trim(), _left.ButtonPressed)
        {
            LaunchMonitor = _editing?.LaunchMonitor,
            ClubYards = known.Count > 0 ? known : null,
        };
        Close();
        Saved?.Invoke(home);
    }
}
