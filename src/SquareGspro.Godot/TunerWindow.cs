using Godot;
using SquareGspro.Core;

namespace SquareGspro.Range3D;

/// <summary>One shot's numbers for the tuner's comparison table, with the tuning it was played with.</summary>
public sealed record TunerResult(
    string Label, string Club, bool Putt, double Carry, double Total, double Apex, double Descent, double Offline, PhysicsTuning Tuning)
{
    public double Roll => Total - Carry;
}

/// <summary>
/// The physics tuner: its own OS window (so it can sit beside the game or on another monitor) with a slider and
/// number box for every <see cref="PhysicsTuning"/> value. Changes apply to <see cref="ActivePhysics"/> at once, so
/// they take effect from the next shot. Shows the last few shots side by side with what changed between them,
/// replays the last shot with the current numbers, and saves and loads presets as JSON in a <c>tuning</c> folder
/// (next to the app, or in the project folder when run from Godot). A preset can be loaded at start-up.
/// </summary>
public partial class TunerWindow : Window
{
    private const int MaxResults = 6;
    private const string StartupFile = "startup.json";

    public event Action? ReplayPressed;

    private sealed class Row
    {
        public required TuningParameter Parameter;
        public required Label Name;
        public required HSlider Slider;
        public required SpinBox Box;
        public required Button Reset;
    }

    private readonly Dictionary<string, Row> _rows = new();
    private readonly List<(string Group, string Section, Control Block)> _sections = new();
    private readonly List<TunerResult> _results = new();
    private readonly Dictionary<string, Button> _groupTabs = new(), _pageTabs = new();
    private string _group = PhysicsTuning.Flight, _page = PhysicsTuning.SurfaceKinds[0].Title;
    private bool _syncing;
    private double _saveStartupIn = -1;

    private Label _changed = null!, _status = null!, _diff = null!;
    private Button _replay = null!;
    private CheckBox _startup = null!, _ghost = null!;
    private LineEdit _name = null!;
    private OptionButton _presets = null!;
    private GridContainer _table = null!;
    private HBoxContainer _pages = null!;
    private ScrollContainer _scroll = null!;

    /// <summary>Show the previous flight faintly in the 3D view for comparison.</summary>
    public bool ShowGhost => _ghost.ButtonPressed;

    /// <summary>Folder for presets: <c>tuning</c> beside the exported app, or in the project when run from Godot.</summary>
    public static string PresetFolder =>
        OS.HasFeature("template")
            ? Path.Combine(Path.GetDirectoryName(OS.GetExecutablePath()) ?? ".", "tuning")
            : Path.Combine(ProjectSettings.GlobalizePath("res://"), "tuning");

    /// <summary>The preset saved for start-up, if there is one (and it reads).</summary>
    public static PhysicsTuning? LoadStartup()
    {
        string path = Path.Combine(PresetFolder, StartupFile);
        try { return File.Exists(path) ? PhysicsTuning.FromJson(File.ReadAllText(path)) : null; }
        catch (Exception ex) { GD.PrintErr($"Couldn't read {path}: {ex.Message}"); return null; }
    }

    public override void _Ready()
    {
        Title = "Physics tuner — Square Range";
        Visible = false;
        Transient = false;
        Exclusive = false;
        MinSize = new Vector2I(560, 520);
        Theme = UiTheme.Build();
        CloseRequested += Hide;

        var root = new PanelContainer();
        root.AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(UiTheme.Bar, 1), 0, 0, 14));
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 10);
        root.AddChild(v);

        BuildHeader(v);
        BuildResults(v);
        BuildTabs(v);
        _scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 10);
        _scroll.AddChild(list);
        v.AddChild(_scroll);
        BuildParameters(list);

        ActivePhysics.Changed += SyncAll;
        SyncAll();
        ShowGroup(PhysicsTuning.Flight);
        RefreshPresets();
        RefreshResults();
    }

    public override void _ExitTree() => ActivePhysics.Changed -= SyncAll;

    public override void _Process(double delta)
    {
        if (_saveStartupIn < 0) return;
        _saveStartupIn -= delta;
        if (_saveStartupIn < 0) WriteStartup();
    }

    /// <summary>Shows the window (beside the game window the first time, if there's room on that screen) or hides it.</summary>
    public void Toggle()
    {
        if (Visible) { Hide(); return; }
        if (Position == Vector2I.Zero)
        {
            var main = GetTree().Root;
            var screen = DisplayServer.ScreenGetUsableRect(DisplayServer.WindowGetCurrentScreen());
            int height = Math.Min(980, screen.Size.Y - 60);
            Size = new Vector2I(620, height);
            int x = main.Position.X + main.Size.X + 8;
            if (x + Size.X > screen.End.X) x = screen.End.X - Size.X - 8;   // no room: overlap the game's right edge
            Position = new Vector2I(Math.Max(screen.Position.X, x), Math.Max(screen.Position.Y + 30, main.Position.Y));
        }
        Show();
    }

    // ---- results -----------------------------------------------------------------------------

    /// <summary>Adds a shot to the comparison table (newest first).</summary>
    public void AddResult(TunerResult r)
    {
        _results.Insert(0, r);
        if (_results.Count > MaxResults) _results.RemoveAt(_results.Count - 1);
        RefreshResults();
    }

    /// <summary>Enables Replay, describing the shot it would play again (null: nothing to replay).</summary>
    public void SetReplay(string? description)
    {
        _replay.Disabled = description == null;
        _replay.TooltipText = description == null ? "Hit a shot first" : $"Play the last shot again with the current numbers: {description}  [F5]";
    }

    private void BuildResults(VBoxContainer v)
    {
        var card = new PanelContainer();
        card.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Card, 10, 1, 12));
        var c = new VBoxContainer();
        c.AddThemeConstantOverride("separation", 8);
        card.AddChild(c);

        var head = new HBoxContainer();
        var caption = UiTheme.Caption("Recent shots");
        caption.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        caption.VerticalAlignment = VerticalAlignment.Center;
        head.AddChild(caption);
        _ghost = new CheckBox { Text = "Show previous flight", ButtonPressed = true, TooltipText = "Draw the previous shot's flight faintly in the game, when it was struck from the same spot" };
        head.AddChild(_ghost);
        _replay = new Button { Text = "Replay last shot", ThemeTypeVariation = "PrimaryButton", Disabled = true };
        _replay.Shortcut = new Shortcut { Events = new Godot.Collections.Array { new InputEventKey { Keycode = Key.F5 } } };
        _replay.Pressed += () => ReplayPressed?.Invoke();
        head.AddChild(_replay);
        c.AddChild(head);

        _table = new GridContainer { Columns = 8 };
        _table.AddThemeConstantOverride("h_separation", 14);
        _table.AddThemeConstantOverride("v_separation", 4);
        c.AddChild(_table);

        _diff = UiTheme.Text("", 12, UiTheme.Muted);
        _diff.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        c.AddChild(_diff);
        v.AddChild(card);
    }

    private void RefreshResults()
    {
        foreach (var child in _table.GetChildren()) child.QueueFree();
        void Cell(string text, Color color, bool bold = false, bool right = true)
        {
            var l = UiTheme.Text(text, 13, color, bold);
            l.HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            _table.AddChild(l);
        }
        foreach (var h in new[] { "Shot", "Club", "Carry", "Total", "Roll", "Apex", "Descent", "Offline" })
        {
            var l = UiTheme.Caption(h);
            l.HorizontalAlignment = h is "Shot" or "Club" ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            _table.AddChild(l);
        }
        if (_results.Count == 0)
        {
            Cell("—", UiTheme.Muted, right: false);
            for (int i = 1; i < 8; i++) Cell("", UiTheme.Muted);
            _diff.Text = "Hit a shot (or HIT SHOT in the game), change some numbers here, then Replay to fly the same shot again.";
            return;
        }
        for (int i = 0; i < _results.Count; i++)
        {
            var r = _results[i];
            var color = i == 0 ? UiTheme.Ink : UiTheme.Muted;
            Cell(r.Label, color, bold: i == 0, right: false);
            Cell(r.Club, color, right: false);
            Cell(r.Putt ? "—" : $"{r.Carry:0.0}", color);
            Cell($"{r.Total:0.0}", color);
            Cell($"{r.Roll:+0.0;−0.0;0.0}", color);
            Cell(r.Putt ? "—" : $"{r.Apex:0.0}", color);
            Cell(r.Putt ? "—" : $"{r.Descent:0.0}°", color);
            Cell(RangeShot.Side(r.Offline), color);
            if (i == 0 && _results.Count > 1)
            {
                // Change from the shot before, right under the newest.
                var p = _results[1];
                string D(double a, double b, string unit = "") => Math.Abs(a - b) < 0.05 ? "·" : $"{a - b:+0.0;−0.0}{unit}";
                var accent = UiTheme.Accent;
                Cell("Δ", accent, bold: true, right: false);
                Cell("", accent, right: false);
                Cell(r.Putt || p.Putt ? "" : D(r.Carry, p.Carry), accent);
                Cell(D(r.Total, p.Total), accent);
                Cell(D(r.Roll, p.Roll), accent);
                Cell(r.Putt || p.Putt ? "" : D(r.Apex, p.Apex), accent);
                Cell(r.Putt || p.Putt ? "" : D(r.Descent, p.Descent, "°"), accent);
                Cell(D(r.Offline, p.Offline), accent);
            }
        }

        var changes = _results.Count > 1 ? _results[0].Tuning.ChangesFrom(_results[1].Tuning).ToList() : new();
        _diff.Text = _results.Count < 2 ? "Yards and degrees. Roll is total minus carry (negative = it spun back)."
            : changes.Count == 0 ? "Same physics as the shot before."
            : "Changed since the shot before: " + string.Join(" · ", changes.Select(c => $"{FullName(c.Parameter)} {c.Parameter.Format(c.Before)} → {c.Parameter.Format(c.After)}"));
    }

    private static string FullName(TuningParameter p) =>
        p.Group == PhysicsTuning.Flight ? p.Label : $"{p.Section} {char.ToLowerInvariant(p.Label[0])}{p.Label[1..]}";

    // ---- header and presets ------------------------------------------------------------------

    private void BuildHeader(VBoxContainer v)
    {
        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 10);
        var title = UiTheme.Text("Physics tuner", 20, UiTheme.Ink, bold: true);
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        top.AddChild(title);
        _changed = UiTheme.Text("", 13, UiTheme.Muted);
        _changed.VerticalAlignment = VerticalAlignment.Center;
        top.AddChild(_changed);
        var resetAll = new Button { Text = "Reset all", TooltipText = "Put every value back to the engine's default" };
        resetAll.Pressed += () => Apply(ActivePhysics.Tuning.Reset());
        top.AddChild(resetAll);
        v.AddChild(top);

        var note = UiTheme.Text("Changes apply from the next shot. Defaults are the engine's provisional values, not yet calibrated against measured shots.", 12, UiTheme.Muted);
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        v.AddChild(note);

        var presets = new HBoxContainer();
        presets.AddThemeConstantOverride("separation", 8);
        _name = new LineEdit { PlaceholderText = "Preset name", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _name.TextSubmitted += _ => SavePreset();
        presets.AddChild(_name);
        var save = new Button { Text = "Save", TooltipText = "Save these values as a preset" };
        save.Pressed += SavePreset;
        presets.AddChild(save);
        _presets = new OptionButton { CustomMinimumSize = new Vector2(190, 0), TooltipText = "Load a saved preset" };
        _presets.ItemSelected += i => LoadPreset((int)i);
        presets.AddChild(_presets);
        v.AddChild(presets);

        var options = new HBoxContainer();
        _startup = new CheckBox
        {
            Text = "Use these values at start-up", ButtonPressed = File.Exists(Path.Combine(PresetFolder, StartupFile)),
            TooltipText = "Keep the current values (saved as tuning/startup.json) and load them every time the app starts",
        };
        _startup.Toggled += on =>
        {
            if (on) WriteStartup();
            else
            {
                try { File.Delete(Path.Combine(PresetFolder, StartupFile)); } catch (IOException) { }
                Status("Start-up values cleared: the app will start with the defaults.");
            }
        };
        options.AddChild(_startup);
        _status = UiTheme.Text("", 12, UiTheme.Muted);
        _status.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _status.HorizontalAlignment = HorizontalAlignment.Right;
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.ClipText = true;
        options.AddChild(_status);
        v.AddChild(options);
    }

    private void Status(string text) => _status.Text = text;

    private IEnumerable<string> PresetFiles() =>
        Directory.Exists(PresetFolder)
            ? Directory.GetFiles(PresetFolder, "*.json").Where(f => !Path.GetFileName(f).Equals(StartupFile, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f)
            : Enumerable.Empty<string>();

    private void RefreshPresets()
    {
        _presets.Clear();
        _presets.AddItem("Load preset…");
        foreach (var f in PresetFiles()) _presets.AddItem(Path.GetFileNameWithoutExtension(f));
        _presets.Selected = 0;
    }

    private void SavePreset()
    {
        string name = _name.Text.Trim();
        if (name.Length == 0) name = ActivePhysics.Tuning.Name is { Length: > 0 } n && n != "Defaults" ? n : "My tuning";
        string file = string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        if (file.Equals(Path.GetFileNameWithoutExtension(StartupFile), StringComparison.OrdinalIgnoreCase)) file += " preset";
        try
        {
            Directory.CreateDirectory(PresetFolder);
            string path = Path.Combine(PresetFolder, file + ".json");
            var named = ActivePhysics.Tuning.Named(name);
            File.WriteAllText(path, named.ToJson());
            _syncing = true;
            ActivePhysics.Apply(named);
            _syncing = false;
            _name.Text = name;
            RefreshPresets();
            Status($"Saved {path}");
        }
        catch (Exception ex) { Status($"Couldn't save: {ex.Message}"); }
    }

    private void LoadPreset(int index)
    {
        if (index <= 0) return;
        var files = PresetFiles().ToList();
        string path = files[index - 1];
        try
        {
            var t = PhysicsTuning.FromJson(File.ReadAllText(path));
            Apply(t);
            _name.Text = t.Name;
            Status($"Loaded {Path.GetFileName(path)}: {t.ChangedCount} values off default");
        }
        catch (Exception ex) { Status($"Couldn't load {Path.GetFileName(path)}: {ex.Message}"); }
        _presets.Selected = 0;
    }

    private void WriteStartup()
    {
        _saveStartupIn = -1;
        try
        {
            Directory.CreateDirectory(PresetFolder);
            File.WriteAllText(Path.Combine(PresetFolder, StartupFile), ActivePhysics.Tuning.ToJson());
            Status("Start-up values saved.");
        }
        catch (Exception ex) { Status($"Couldn't save start-up values: {ex.Message}"); }
    }

    // ---- parameters --------------------------------------------------------------------------

    private void BuildTabs(VBoxContainer v)
    {
        var tabs = new HBoxContainer();
        tabs.AddThemeConstantOverride("separation", 4);
        var group = new ButtonGroup();
        foreach (var g in new[] { PhysicsTuning.Flight, PhysicsTuning.Ground, PhysicsTuning.Lies })
        {
            var b = new Button { Text = g, ToggleMode = true, ButtonGroup = group, ThemeTypeVariation = "TabButton" };
            b.Pressed += () => ShowGroup(g);
            tabs.AddChild(b);
            _groupTabs[g] = b;
        }
        v.AddChild(tabs);

        // Bounce & roll has a page per surface.
        _pages = new HBoxContainer();
        _pages.AddThemeConstantOverride("separation", 2);
        var pageGroup = new ButtonGroup();
        foreach (var title in PhysicsTuning.SurfaceKinds.Select(s => s.Title).Append("Conditions"))
        {
            var b = new Button { Text = title, ToggleMode = true, ButtonGroup = pageGroup, ThemeTypeVariation = "TabButton" };
            b.AddThemeFontSizeOverride("font_size", 12);
            b.Pressed += () => { _page = title; ShowGroup(PhysicsTuning.Ground); };
            _pages.AddChild(b);
            _pageTabs[title] = b;
        }
        v.AddChild(_pages);
    }

    /// <summary>Shows a tab by name: a group ("Lies"), or a surface page of Bounce &amp; roll ("Green").</summary>
    public void ShowTab(string name)
    {
        if (_pageTabs.ContainsKey(name)) { _page = name; ShowGroup(PhysicsTuning.Ground); }
        else if (_groupTabs.ContainsKey(name)) ShowGroup(name);
    }

    private void ShowGroup(string group)
    {
        _group = group;
        _groupTabs[group].SetPressedNoSignal(true);
        bool paged = group == PhysicsTuning.Ground;
        _pages.Visible = paged;
        if (paged) _pageTabs[_page].SetPressedNoSignal(true);
        foreach (var (g, section, block) in _sections) block.Visible = g == group && (!paged || section == _page);
        _scroll.ScrollVertical = 0;
    }

    private void BuildParameters(VBoxContainer list)
    {
        foreach (var bySection in PhysicsTuning.Parameters.GroupBy(p => (p.Group, p.Section)))
        {
            var card = new PanelContainer();
            card.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Card, 10, 1, 12));
            var c = new VBoxContainer();
            c.AddThemeConstantOverride("separation", 10);
            card.AddChild(c);

            var head = new HBoxContainer();
            var caption = UiTheme.Caption(bySection.Key.Section);
            caption.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            caption.VerticalAlignment = VerticalAlignment.Center;
            head.AddChild(caption);
            var reset = new Button { Text = "Reset section", ThemeTypeVariation = "FlatButton" };
            var (g, s) = bySection.Key;
            reset.Pressed += () => Apply(ActivePhysics.Tuning.Reset(g, s));
            head.AddChild(reset);
            c.AddChild(head);

            foreach (var p in bySection) c.AddChild(BuildRow(p));
            list.AddChild(card);
            _sections.Add((g, s, card));
        }
    }

    private Control BuildRow(TuningParameter p)
    {
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 2);
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 10);

        var name = UiTheme.Text(p.Label, 14, UiTheme.Ink);
        name.CustomMinimumSize = new Vector2(190, 0);
        name.VerticalAlignment = VerticalAlignment.Center;
        name.MouseFilter = Control.MouseFilterEnum.Stop;
        name.TooltipText = p.Help;
        h.AddChild(name);

        var slider = new HSlider
        {
            MinValue = p.Min, MaxValue = p.Max, Step = p.Step, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, FocusMode = Control.FocusModeEnum.None,
        };
        h.AddChild(slider);

        var box = new SpinBox
        {
            MinValue = p.Min, MaxValue = p.Max, Step = p.Step, CustomMinimumSize = new Vector2(118, 0),
            Suffix = p.Unit, SelectAllOnFocus = true, Alignment = HorizontalAlignment.Right,
        };
        h.AddChild(box);

        var reset = new Button { Text = "↺", ThemeTypeVariation = "FlatButton", TooltipText = $"Reset to {p.Format(p.Default)} {p.Unit}".TrimEnd() };
        reset.Pressed += () => Apply(ActivePhysics.Tuning.With(p.Key, p.Default));
        h.AddChild(reset);
        v.AddChild(h);

        if (p.Help.Length > 0)
        {
            var help = UiTheme.Text(p.Help, 11, UiTheme.Muted);
            help.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            help.CustomMinimumSize = new Vector2(200, 0);
            v.AddChild(help);
        }

        slider.ValueChanged += value => Changed(p, value);
        box.ValueChanged += value => Changed(p, value);
        _rows[p.Key] = new Row { Parameter = p, Name = name, Slider = slider, Box = box, Reset = reset };
        return v;
    }

    private void Changed(TuningParameter p, double value)
    {
        if (_syncing) return;
        Apply(ActivePhysics.Tuning.With(p.Key, value));
    }

    private void Apply(PhysicsTuning t)
    {
        ActivePhysics.Apply(t);   // raises Changed → SyncAll
    }

    /// <summary>Shows the current tuning in every row (after a change from here, a preset or a reset).</summary>
    private void SyncAll()
    {
        var t = ActivePhysics.Tuning;
        _syncing = true;
        foreach (var row in _rows.Values)
        {
            double value = t[row.Parameter.Key];
            row.Slider.SetValueNoSignal(value);
            row.Box.SetValueNoSignal(value);
            bool changed = !t.IsDefault(row.Parameter.Key);
            row.Name.Text = changed ? "● " + row.Parameter.Label : row.Parameter.Label;
            row.Name.AddThemeColorOverride("font_color", changed ? UiTheme.Accent : UiTheme.Ink);
            row.Reset.Disabled = !changed;
            row.Reset.Modulate = new Color(1, 1, 1, changed ? 1 : 0.25f);
        }
        _syncing = false;

        int n = t.ChangedCount;
        _changed.Text = n == 0 ? "All defaults" : $"{n} value{(n == 1 ? "" : "s")} changed";
        _changed.AddThemeColorOverride("font_color", n == 0 ? UiTheme.Muted : UiTheme.Warn);
        foreach (var (g, b) in _groupTabs)
        {
            int k = PhysicsTuning.Parameters.Count(p => p.Group == g && !t.IsDefault(p.Key));
            b.Text = k == 0 ? g : $"{g} ({k})";
        }
        foreach (var (title, b) in _pageTabs)
        {
            int k = PhysicsTuning.Parameters.Count(p => p.Group == PhysicsTuning.Ground && p.Section == title && !t.IsDefault(p.Key));
            b.Text = k == 0 ? title : $"{title} ({k})";
        }
        if (_startup.ButtonPressed) _saveStartupIn = 0.5;
    }
}
