using Godot;
using GrimesGolf.Core.Course;

namespace GrimesGolf.Game;

/// <summary>
/// New Game's Game Setup, under the player list: the game (stroke play, scramble, best ball, match play), which holes,
/// the weather (wind, altitude, temperature and rain) and the course (fairway speed, green firmness and speed). Each
/// choice is a row of buttons (one press with a gamepad), with a line saying what the game and holes mean for the
/// players picked. Opens with the last setup used.
/// </summary>
public partial class MainMenu
{
    private Label _typeNote = null!, _holesNote = null!;
    private GameSetup _setup = GameSetup.Default;
    private IReadOnlyList<Golfer> _golfers = Array.Empty<Golfer>();
    private readonly Dictionary<int, IReadOnlyList<Hole>> _holes = new();
    private readonly Dictionary<int, double> _altitudeFeet = new();
    private readonly Dictionary<GameType, Button> _typeButtons = new();
    private Button _courseAltitude = null!;
    /// <summary>Puts every row's buttons back to match <see cref="_setup"/>.</summary>
    private readonly List<Action> _syncRows = new();

    private const float MenuWidth = 560;

    /// <summary>Game Setup, filling what the player list leaves of the column. It scrolls if it doesn't fit (large text, four players).</summary>
    private void BuildSetupCard(VBoxContainer column)
    {
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true, SizeFlagsVertical = SizeFlags.ExpandFill };
        var card = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkBegin };
        card.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Card, 12, 1, 18, 14));
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 5);
        card.AddChild(v);
        scroll.AddChild(card);
        column.AddChild(scroll);

        v.AddChild(UiTheme.Text("Game Setup", 24, UiTheme.Ink, bold: true));
        var types = Row(v, "Game type", Enum.GetValues<GameType>(), GameSetup.Name, () => _setup.Type, t => _setup = _setup with { Type = t },
            t => t switch
            {
                GameType.Scramble => "Everyone in a team hits; the team plays the best shot",
                GameType.BestBall => "Everyone plays their own ball; a team counts its best score on each hole",
                GameType.MatchPlay => "Hole by hole: the lower score wins the hole; most holes won wins",
                _ => "Everyone plays their own ball; the lowest total wins",
            });
        foreach (var (t, b) in types) _typeButtons[t] = b;
        _typeNote = Note(v);
        Row(v, "Holes", Enum.GetValues<HoleSet>(), GameSetup.Name, () => _setup.Holes, h => _setup = _setup with { Holes = h });
        _holesNote = Note(v);

        v.AddChild(new HSeparator());
        v.AddChild(UiTheme.Caption("Environment"));
        Row(v, "Wind", Enum.GetValues<WindStrength>(), w => w.ToString(), () => _setup.Wind, w => _setup = _setup with { Wind = w },
            w => w switch
            {
                WindStrength.Light => "2-7 mph", WindStrength.Breezy => "7-13 mph", WindStrength.Windy => "13-21 mph",
                _ => "No wind",
            } + "; it changes a little from hole to hole");
        var altitude = Row(v, "Altitude", Enum.GetValues<AltitudeSetting>(), GameSetup.Name, () => _setup.Altitude, a => _setup = _setup with { Altitude = a },
            _ => "Thinner air higher up: the ball flies further");
        _courseAltitude = altitude[0].Button;
        Row(v, "Weather", Enum.GetValues<Weather>(), w => w.ToString(), () => _setup.Weather, w => _setup = _setup with { Weather = w },
            w => w switch
            {
                Weather.Hot => "95 °F: warm air is thinner, the ball carries further",
                Weather.Cold => "45 °F: cold air is denser, the ball carries less",
                Weather.Wet => "Rain: soft, wet ground; the ball plugs and rolls out less",
                _ => "Mild and dry",
            });

        v.AddChild(new HSeparator());
        v.AddChild(UiTheme.Caption("Course"));
        Row(v, "Fairways", Enum.GetValues<FairwaySpeed>(), f => f.ToString(), () => _setup.Fairways, f => _setup = _setup with { Fairways = f },
            f => f switch { FairwaySpeed.Slow => "Lush: the ball stops quickly", FairwaySpeed.Fast => "Firm and dry: lots of run-out", _ => "Normal run-out" });
        Row(v, "Green firmness", Enum.GetValues<GreenFirmness>(), g => g.ToString(), () => _setup.Greens, g => _setup = _setup with { Greens = g },
            g => g switch { GreenFirmness.Soft => "Approach shots stop and spin back", GreenFirmness.Firm => "Approach shots bounce and release", _ => "Normal" });
        Row(v, "Green speed", GameSetup.Stimps, s => $"{s:0}", () => _setup.Stimp, s => _setup = _setup with { Stimp = s },
            s => $"Stimp {s:0}: {(s <= 8 ? "slow" : s <= 10 ? "medium" : s <= 11 ? "quick" : "tournament fast")}");
    }

    private static Label Note(VBoxContainer into)
    {
        var l = UiTheme.Text("", 14, UiTheme.Muted);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.CustomMinimumSize = new Vector2(400, 0);
        into.AddChild(l);
        return l;
    }

    /// <summary>A caption and a row of buttons, one per value, exactly one of them down (the setup's).</summary>
    private List<(T Value, Button Button)> Row<T>(VBoxContainer into, string caption, IEnumerable<T> values, Func<T, string> name,
        Func<T> current, Action<T> pick, Func<T, string>? tip = null) where T : notnull
    {
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 6);
        var c = UiTheme.Text(caption, 16, UiTheme.Ink);
        c.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        c.CustomMinimumSize = new Vector2(124, 38);
        c.VerticalAlignment = VerticalAlignment.Center;
        h.AddChild(c);
        // The buttons wrap when large text leaves too little width for them.
        var flow = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        flow.AddThemeConstantOverride("h_separation", 6);
        flow.AddThemeConstantOverride("v_separation", 6);
        h.AddChild(flow);
        var group = new ButtonGroup();
        var buttons = new List<(T, Button)>();
        foreach (var value in values)
        {
            var b = new Button { Text = name(value), ToggleMode = true, ButtonGroup = group, ThemeTypeVariation = "TabButton",
                                 CustomMinimumSize = new Vector2(66, 34), TooltipText = tip?.Invoke(value) ?? "" };
            b.AddThemeFontSizeOverride("font_size", 16);
            var v = value;
            b.Toggled += on => { if (on && !EqualityComparer<T>.Default.Equals(current(), v)) { pick(v); RefreshSetupNotes(); } };
            flow.AddChild(b);
            buttons.Add((value, b));
        }
        into.AddChild(h);
        _syncRows.Add(() =>
        {
            foreach (var (value, b) in buttons) b.SetPressedNoSignal(EqualityComparer<T>.Default.Equals(current(), value));
        });
        return buttons;
    }

    /// <summary>Esc / B: from New Game back to the menu.</summary>
    public void Back() => ShowHome();

    /// <summary>What the chosen game and holes mean here: the teams, or why the game can't be played; how long the round is.</summary>
    private void RefreshSetupNotes()
    {
        int n = Math.Max(1, _golfers.Count);
        foreach (var (type, b) in _typeButtons)
        {
            string? problem = GameSetup.Problem(type, n);
            b.Disabled = problem != null && type != _setup.Type;
            b.TooltipText = problem == null ? b.TooltipText.Split("\n(")[0] : $"{b.TooltipText.Split("\n(")[0]}\n({GameSetup.Name(type)} {problem})";
        }
        var playing = _setup.TypeFor(n);
        var sides = Match.SidesFor(playing, n);
        string Side(IReadOnlyList<int> s) => string.Join(" & ", s.Select(p => p < _golfers.Count ? _golfers[p].Name : $"Player {p + 1}"));
        string teams = sides.Count == 1 ? Side(sides[0]) : string.Join(" vs ", sides.Select(Side));
        _typeNote.Text = GameSetup.Problem(_setup.Type, n) is { } why
            ? $"{GameSetup.Name(_setup.Type)} {why}: with {n} player{(n == 1 ? "" : "s")} it's played as stroke play."
            : playing switch
            {
                GameType.Scramble => $"{teams}. Everyone in a team hits from the same spot, then the team plays the best shot.",
                GameType.BestBall => $"{teams}. Everyone plays their own ball; each hole counts the team's best score.",
                GameType.MatchPlay => $"{teams}. {(sides[0].Count > 1 ? "Each team's best score on a hole" : "The lower score on a hole")} wins it; most holes won wins the match.",
                _ => n == 1 ? $"{teams}: every stroke counts." : $"{teams}: every stroke counts, the lowest total wins.",
            };

        if (_holes.TryGetValue(_selected, out var all))
        {
            var holes = _setup.Pick(all);
            string which = holes.Count == all.Count ? "" : holes.Count <= 6 ? $" ({string.Join(", ", holes.Select(h => h.Number))})" : $" ({holes[0].Number}-{holes[^1].Number})";
            _holesNote.Text = $"{holes.Count} hole{(holes.Count == 1 ? "" : "s")}{which}  ·  Par {holes.Sum(h => h.Par)}  ·  {holes.Sum(h => h.LengthYards):#,0} yd";
        }
        else _holesNote.Text = "Loading the course…";
        _courseAltitude.Text = _altitudeFeet.TryGetValue(_selected, out var ft) ? $"Course ({ft:#,0} ft)" : "Course";
    }

    /// <summary>A course's holes and height above sea level (ft), once it's loaded, for Game Setup.</summary>
    public void SetCourseHoles(int index, IReadOnlyList<Hole> holes, double altitudeFeet)
    {
        _holes[index] = holes;
        _altitudeFeet[index] = altitudeFeet;
        if (index == _selected && OnSubPage) RefreshSetupNotes();
    }
}
