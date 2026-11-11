using Godot;
using GrimesGolf.Core.Course;
using GrimesGolf.Core.Storage;

namespace GrimesGolf.Game;

/// <summary>
/// History, from the main menu: finished rounds with their scorecards, and practice by day with each player's
/// averages per club. A list on the left (arrows / D-pad move through it), what's picked on the right.
/// </summary>
public partial class HistoryPanel : Control
{
    public event Action? Closed;

    private Button _roundsTab = null!, _practiceTab = null!, _close = null!;
    private VBoxContainer _list = null!, _detail = null!;
    private ScrollContainer _detailScroll = null!;
    private ButtonGroup _itemGroup = new();
    private IReadOnlyList<SavedRound> _rounds = Array.Empty<SavedRound>();
    private IReadOnlyList<ShotDay> _days = Array.Empty<ShotDay>();

    public bool IsOpen => Visible;

    public override void _Ready()
    {
        Visible = false;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.55f), MouseFilter = MouseFilterEnum.Ignore };
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(dim);

        // Most of the screen, so an 18-hole card and a full bag of clubs fit at large text sizes.
        var panel = new PanelContainer { AnchorLeft = 0.06f, AnchorRight = 0.94f, AnchorTop = 0.07f, AnchorBottom = 0.93f };
        panel.AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(UiTheme.Bar, 1), 16, 1, 24));
        AddChild(panel);

        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 12);
        panel.AddChild(v);

        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 8);
        var title = UiTheme.Text("History", 26, UiTheme.Ink, bold: true);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        head.AddChild(title);
        var tabs = new ButtonGroup();
        _roundsTab = Tab("Rounds", "Finished rounds and their scorecards", tabs);
        _roundsTab.Toggled += on => { if (on) ShowRounds(); };
        head.AddChild(_roundsTab);
        _practiceTab = Tab("Practice", "Shots by day, with each player's averages per club", tabs);
        _practiceTab.Toggled += on => { if (on) ShowDays(); };
        head.AddChild(_practiceTab);
        v.AddChild(head);

        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 16);
        var listScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true, CustomMinimumSize = new Vector2(370, 0) };
        _list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _list.AddThemeConstantOverride("separation", 6);
        listScroll.AddChild(_list);
        body.AddChild(listScroll);
        _detailScroll = new ScrollContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Auto };
        var detailBox = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        detailBox.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Card, 12, 1, 18));
        _detail = new VBoxContainer();
        _detail.AddThemeConstantOverride("separation", 10);
        detailBox.AddChild(_detail);
        _detailScroll.AddChild(detailBox);
        body.AddChild(_detailScroll);
        v.AddChild(body);

        var buttons = new HBoxContainer();
        buttons.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        _close = new Button { Text = "Done", ThemeTypeVariation = "FlatButton", CustomMinimumSize = new Vector2(120, 48), TooltipText = "Back to the menu  [Esc, gamepad B]" };
        _close.Pressed += Close;
        buttons.AddChild(_close);
        v.AddChild(buttons);
    }

    private static Button Tab(string text, string tooltip, ButtonGroup group)
    {
        var b = new Button { Text = text, TooltipText = tooltip, ToggleMode = true, ButtonGroup = group, ThemeTypeVariation = "TabButton", CustomMinimumSize = new Vector2(130, 44) };
        b.AddThemeFontSizeOverride("font_size", 18);
        return b;
    }

    /// <summary>Opens on Rounds, or on Practice when there are no finished rounds but there are shots.</summary>
    public void Open(IReadOnlyList<SavedRound> rounds, IReadOnlyList<ShotDay> days)
    {
        _rounds = rounds;
        _days = days;
        Visible = true;
        var tab = rounds.Count == 0 && days.Count > 0 ? _practiceTab : _roundsTab;
        if (tab.ButtonPressed) { if (tab == _roundsTab) ShowRounds(); else ShowDays(); }
        else tab.ButtonPressed = true;
    }

    /// <summary>Switches to the Practice tab.</summary>
    public void ShowPractice() => _practiceTab.ButtonPressed = true;

    public void Close()
    {
        Visible = false;
        ClearList();
        ClearDetail();
        Closed?.Invoke();
    }

    // ---- rounds ------------------------------------------------------------------------------

    private void ShowRounds()
    {
        ClearList();
        if (_rounds.Count == 0)
        {
            Empty("No finished rounds yet", "A round is kept here when its last hole is played. Unfinished rounds are under Resume Game.");
            _roundsTab.GrabFocus();
            return;
        }
        foreach (var r in _rounds)
        {
            // One player: their score; more: how many (the scores are on the right, they'd be cut off here).
            string who = r.Players.Length == 1 && History.Totals(r)[0] is var t ? $"{t.Strokes} ({Hud.ToPar(t.ToPar)})" : $"{r.Players.Length} players";
            AddItem($"{Short(r.Course)}\n{r.Started:ddd d MMM yyyy}  ·  {who}", () => ShowRound(r));
        }
    }

    private void ShowRound(SavedRound r)
    {
        ClearDetail();
        var totals = History.Totals(r);
        Heading(r.Course, $"{r.Started:dddd d MMMM yyyy, HH:mm}  ·  {GameSetup.Name(r.Game.TypeFor(r.Players.Length))}  ·  {r.Pars.Length} holes, par {r.Pars.Sum()}");

        var line = new HFlowContainer();
        line.AddThemeConstantOverride("h_separation", 22);
        for (int i = 0; i < totals.Count; i++)
        {
            var t = totals[i];
            string holes = t.HolesPlayed < r.Pars.Length ? $"  ({t.HolesPlayed} holes)" : "";
            line.AddChild(UiTheme.Text($"{t.Player}  {t.Strokes} ({Hud.ToPar(t.ToPar)}){holes}", 19, UiTheme.PlayerColor(i), bold: true));
        }
        _detail.AddChild(line);

        var grid = new GridContainer();
        grid.AddThemeConstantOverride("h_separation", 4);
        grid.AddThemeConstantOverride("v_separation", 6);
        _detail.AddChild(grid);
        var rows = r.Players.Select((p, i) => new ScorecardRow(p.Name, p.Scores, UiTheme.PlayerColor(i))).ToList();
        Hud.FillScorecard(grid, History.HoleNumbers(r), r.Pars, rows, -1);

        var rules = r.Rules;
        _detail.AddChild(Note($"House rules: {rules.Mulligans} mulligan{(rules.Mulligans == 1 ? "" : "s")} each" +
                              (rules.GimmeMetres > 0 ? $", gimmes inside {rules.GimmeFeet:0.#} ft" : "") +
                              (rules.PickUpAtDoublePar ? ", pick up at double par" : "")));
    }

    // ---- practice ----------------------------------------------------------------------------

    private void ShowDays()
    {
        ClearList();
        if (_days.Count == 0)
        {
            Empty("No shots saved yet", "Every shot is kept, on the range and on a course. They're listed here by day.");
            _practiceTab.GrabFocus();
            return;
        }
        foreach (var d in _days)
            AddItem($"{d.Day:ddd d MMM yyyy}\n{ShotCount(d)}  ·  {string.Join(", ", d.Places.Select(Short))}", () => ShowDay(d));
    }

    private static string ShotCount(ShotDay d) =>
        d.MonitorShots > 0 ? $"{d.MonitorShots} shot{(d.MonitorShots == 1 ? "" : "s")}" : $"{d.ScreenShots} on-screen";

    private void ShowDay(ShotDay d)
    {
        ClearDetail();
        var parts = new List<string>();
        if (d.MonitorShots > 0) parts.Add($"{d.MonitorShots} launch monitor shot{(d.MonitorShots == 1 ? "" : "s")}");
        if (d.ScreenShots > 0) parts.Add($"{d.ScreenShots} on-screen swing{(d.ScreenShots == 1 ? "" : "s")}");
        parts.Add(string.Join(", ", d.Places));
        Heading($"{d.Day:dddd d MMMM yyyy}", string.Join("  ·  ", parts));

        for (int p = 0; p < d.Players.Count; p++)
        {
            var player = d.Players[p];
            _detail.AddChild(new Control { CustomMinimumSize = new Vector2(0, 4) });
            _detail.AddChild(UiTheme.Text($"{player.Player}  ·  {player.Shots} shot{(player.Shots == 1 ? "" : "s")}", 20, UiTheme.PlayerColor(p), bold: true));
            if (player.Clubs.Count == 0)
            {
                _detail.AddChild(Note("No launch monitor shots with a full club (only on-screen swings or putts)."));
                continue;
            }
            var grid = new GridContainer { Columns = 6 };
            grid.AddThemeConstantOverride("h_separation", 22);
            grid.AddThemeConstantOverride("v_separation", 6);
            foreach (var h in new[] { "Club", "Shots", "Carry", "Total", "Longest", "Ball speed" }) Cell(grid, h, UiTheme.Muted, left: h == "Club");
            foreach (var c in player.Clubs)
            {
                Cell(grid, c.Label, UiTheme.Ink, bold: true, left: true);
                Cell(grid, c.Shots.ToString(), UiTheme.Muted);
                Cell(grid, $"{c.CarryYards:0} yd", UiTheme.Accent, bold: true);
                Cell(grid, $"{c.TotalYards:0} yd", UiTheme.Ink);
                Cell(grid, $"{c.LongestYards:0} yd", UiTheme.Ink);
                Cell(grid, $"{c.BallMph:0} mph", UiTheme.Ink);
            }
            _detail.AddChild(grid);
        }
        _detail.AddChild(Note("Averages are launch monitor shots only; on-screen swings and putts are left out."));
    }

    private static void Cell(GridContainer grid, string text, Color color, bool bold = false, bool left = false)
    {
        var l = UiTheme.Text(text, 16, color, bold);
        l.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        l.CustomMinimumSize = new Vector2(left ? 150 : 70, 0);
        grid.AddChild(l);
    }

    // ---- shared ------------------------------------------------------------------------------

    private void AddItem(string text, Action show)
    {
        var b = new Button { Text = text, ToggleMode = true, ThemeTypeVariation = "TabButton", Alignment = HorizontalAlignment.Left,
                             CustomMinimumSize = new Vector2(0, 62), ClipText = true };
        b.AddThemeFontSizeOverride("font_size", 16);
        b.ButtonGroup = _itemGroup;
        // Moving through the list shows each item, so a gamepad needs no extra press.
        b.FocusEntered += () => b.ButtonPressed = true;
        b.Toggled += on => { if (on) show(); };
        _list.AddChild(b);
        if (_list.GetChildCount() == 1)
        {
            b.ButtonPressed = true;
            b.GrabFocus();
        }
    }

    private void Empty(string title, string text)
    {
        ClearDetail();
        Heading(title, text);
    }

    private void Heading(string title, string subtitle)
    {
        _detail.AddChild(UiTheme.Text(title, 24, UiTheme.Ink, bold: true));
        _detail.AddChild(Note(subtitle));
        _detail.AddChild(new ColorRect { Color = UiTheme.Accent, CustomMinimumSize = new Vector2(90, 3), SizeFlagsHorizontal = SizeFlags.ShrinkBegin });
        _detailScroll.ScrollVertical = 0;
    }

    private static Label Note(string text)
    {
        var l = UiTheme.Text(text, 15, UiTheme.Muted);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        return l;
    }

    /// <summary>"Payne's Valley Golf Course (Hollister, MO)" → "Payne's Valley Golf Course" for the list.</summary>
    private static string Short(string course) => course.IndexOf(" (", StringComparison.Ordinal) is var i and > 0 ? course[..i] : course;

    private void ClearList()
    {
        foreach (var c in _list.GetChildren()) { _list.RemoveChild(c); c.QueueFree(); }
        _itemGroup = new ButtonGroup();
    }

    private void ClearDetail()
    {
        foreach (var c in _detail.GetChildren()) { _detail.RemoveChild(c); c.QueueFree(); }
    }
}
