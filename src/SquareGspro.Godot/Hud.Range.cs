using Godot;
using SquareGspro.Core;

namespace SquareGspro.Range3D;

/// <summary>
/// The driving range view: club, headline and detail numbers, session history. The shot controls float above the
/// data bar on the left.
/// </summary>
public partial class Hud
{
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

        // Club and aim (Clear Session is in the menu).
        var controls = new VBoxContainer { CustomMinimumSize = new Vector2(230, 0) };
        controls.AddThemeConstantOverride("separation", 6);
        controls.AddChild(UiTheme.Caption("Club"));
        _rangeClub = ClubPicker();
        controls.AddChild(_rangeClub);
        controls.AddChild(UiTheme.Caption("Aim"));
        var aimRow = new HBoxContainer();
        aimRow.AddThemeConstantOverride("separation", 6);
        AddAimButtons(aimRow, 40);
        _rangeTarget = new Button { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 36),
                                    TooltipText = "Aim at the next flag (then straight down the range)  [F, Shift+F back, gamepad Y]" };
        _rangeTarget.Pressed += () => RangeTargetPressed?.Invoke(Input.IsKeyPressed(Key.Shift) ? -1 : 1);
        aimRow.AddChild(_rangeTarget);
        controls.AddChild(aimRow);
        _rangeAim = UiTheme.Text("", 13, UiTheme.Muted);
        controls.AddChild(_rangeAim);
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
        column.OffsetTop = TopBarHeight + TopGap + 230 + 12 + 12;
        column.OffsetBottom = -RangeBarHeight - 12;
        _rangeView.AddChild(column);
        var cv = new VBoxContainer();
        cv.AddThemeConstantOverride("separation", 6);
        cv.AddChild(UiTheme.Caption("Session"));
        _sessionSummary = UiTheme.Text(NoShotsYet, 13, UiTheme.Muted);
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

    private Button _rangeTarget = null!;
    private Label _rangeAim = null!;

    /// <summary>The range's aim: the flag aimed at (or "Straight") and how far off it the aim is.</summary>
    public void SetRangeAim(string target, string note)
    {
        _rangeTarget.Text = target;
        _rangeAim.Text = note;
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
        _sessionSummary.Text = NoShotsYet;
    }

    private const string NoShotsYet = "No shots yet. Hit one with the monitor, or press HIT SHOT.";
}
