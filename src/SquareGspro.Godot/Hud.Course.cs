using Godot;
using SquareGspro.Core;

namespace SquareGspro.Range3D;

/// <summary>
/// The course view, laid out round the ball in the middle: the hole card top left (hole, par, length, whose turn,
/// stroke and score, mulligan), the hole overview map top right (built in <c>Hud.cs</c>), the lie window
/// bottom left (a picture of the lie, the slopes and the shot penalty; on the green, the read and green aids), the
/// shot bar bottom right (to the pin with the elevation, club, aim, and the shot controls), plus the result card
/// and scorecard.
/// </summary>
public partial class Hud
{
    // ---- course view -------------------------------------------------------------------------

    private void BuildCourseView()
    {
        _courseView = new Control { MouseFilter = MouseFilterEnum.Ignore };
        _courseView.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_courseView);

        BuildHoleCard();
        BuildLiePanel();
        BuildShotPanel();
        BuildResultCard();
        BuildScorecard();

        _hint = new PanelContainer { Visible = false, MouseFilter = MouseFilterEnum.Ignore };
        _hint.AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(0.05f, 0.06f, 0.07f, 0.85f), 10, 1, 10, 20));
        _hint.SetAnchorsPreset(LayoutPreset.CenterTop);
        _hint.GrowHorizontal = GrowDirection.Both;
        _hint.OffsetTop = TopBarHeight + 12;
        _hintText = UiTheme.Text("", 20, UiTheme.Ink, bold: true);
        _hint.AddChild(_hintText);
        _courseView.AddChild(_hint);
    }

    private PanelContainer _hint = null!;
    private Label _hintText = null!;
    private int? _countdown;

    /// <summary>A line across the top of the view, e.g. "Hole 4 flyover · Space to skip" (null hides it).</summary>
    public void SetHint(string? text)
    {
        _hint.Visible = text != null;
        _hintText.Text = text ?? "";
    }

    /// <summary>Seconds until the next hole starts by itself, shown on the NEXT HOLE button (null = no countdown).</summary>
    public void SetNextHoleCountdown(int? seconds)
    {
        if (_countdown == seconds) return;
        _countdown = seconds;
        if (_holeDone) Shot.Hit.Text = NextHoleText;
    }

    private string NextHoleText => _countdown is { } s ? $"NEXT HOLE ({s})" : "NEXT HOLE";

    /// <summary>A shot is under way: no HIT SHOT until it has finished (the shot details stay as they were until then).</summary>
    public void LockHit() => Shot.Hit.Disabled = true;

    private void BuildHoleCard()
    {
        var card = new PanelContainer();
        card.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Card, 12, 1, 16));
        card.Position = new Vector2(16, TopBarHeight + 12);
        card.CustomMinimumSize = new Vector2(300, 0);
        _courseView.AddChild(card);

        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 4);
        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 12);
        _holeNumber = UiTheme.Text("HOLE 1", 30, UiTheme.Ink, bold: true);
        top.AddChild(_holeNumber);
        _holeFacts = UiTheme.Text("", 18, UiTheme.Ink);
        _holeFacts.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        top.AddChild(_holeFacts);
        v.AddChild(top);
        _holeName = UiTheme.Text("", 14, UiTheme.Muted);
        v.AddChild(_holeName);
        v.AddChild(new HSeparator());
        _turn = UiTheme.Text("", 22, UiTheme.Ink, bold: true);
        _turn.Visible = false;
        v.AddChild(_turn);

        var figures = new HBoxContainer();
        figures.AddThemeConstantOverride("separation", 28);
        figures.AddChild(Figure("Stroke", out _stroke, out _));
        figures.AddChild(Figure("Score", out _score, out _scoreNote));
        v.AddChild(figures);
        _standings = UiTheme.Text("", 14, UiTheme.Muted);
        _standings.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _standings.CustomMinimumSize = new Vector2(300, 0);
        _standings.Visible = false;
        v.AddChild(_standings);

        _mulligan = new Button { Visible = false, SizeFlagsHorizontal = SizeFlags.ShrinkBegin, CustomMinimumSize = new Vector2(0, 40),
                                 TooltipText = "Take the last shot again without counting it (house rules)  [U, gamepad X]" };
        _mulligan.Pressed += () => MulliganPressed?.Invoke();
        v.AddChild(_mulligan);
        _credit = UiTheme.Text("", 11, UiTheme.Muted);
        _credit.Visible = false;
        v.AddChild(_credit);
        card.AddChild(v);
    }

    /// <summary>A caption over a big value, with a small note beside the value.</summary>
    private static VBoxContainer Figure(string caption, out Label value, out Label note)
    {
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 0);
        v.AddChild(UiTheme.Caption(caption));
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        value = UiTheme.Text("—", 30, UiTheme.Ink, bold: true);
        row.AddChild(value);
        note = UiTheme.Text("", 14, UiTheme.Muted);
        note.SizeFlagsVertical = SizeFlags.ShrinkEnd;
        row.AddChild(note);
        v.AddChild(row);
        return v;
    }

    private void BuildLiePanel()
    {
        var panel = _liePanel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Bar, 14, 1, 14));
        panel.SetAnchorsPreset(LayoutPreset.BottomLeft);
        panel.GrowVertical = GrowDirection.Begin;
        panel.OffsetLeft = panel.OffsetRight = 16;
        panel.OffsetTop = panel.OffsetBottom = -16;
        _courseView.AddChild(panel);

        var v = new VBoxContainer { CustomMinimumSize = new Vector2(244, 0) };
        v.AddThemeConstantOverride("separation", 6);
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 10);
        var caption = UiTheme.Caption("Lie");
        caption.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        head.AddChild(caption);
        _lieChip = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
        _lieName = UiTheme.Text("Tee", 17, new Color(0.06f, 0.08f, 0.06f), bold: true);
        _lieChip.AddChild(_lieName);
        head.AddChild(_lieChip);
        v.AddChild(head);
        _lieView = new LieView();
        var frame = new PanelContainer();
        frame.AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(0, 0, 0, 0.3f), 8, 1, 2));
        frame.AddChild(_lieView);
        v.AddChild(frame);

        var stance = new GridContainer { Columns = 2 };
        stance.AddThemeConstantOverride("h_separation", 12);
        stance.AddThemeConstantOverride("v_separation", 2);
        Label Row(string caption)
        {
            stance.AddChild(UiTheme.Text(caption, 14, UiTheme.Muted));
            var value = UiTheme.Text("—", 15, UiTheme.Ink, bold: true);
            value.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            value.HorizontalAlignment = HorizontalAlignment.Right;
            stance.AddChild(value);
            return value;
        }
        _sideSlope = Row("Left / Right");
        _upSlope = Row("Up / Down");
        _penalty = Row("Shot penalty");
        var stanceBox = new VBoxContainer();
        stanceBox.AddThemeConstantOverride("separation", 2);
        stanceBox.AddChild(stance);
        _lieNote = UiTheme.Text("", 13, UiTheme.Muted);
        _lieNote.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _lieNote.CustomMinimumSize = new Vector2(244, 0);
        stanceBox.AddChild(_lieNote);
        _stanceRows = stanceBox;
        v.AddChild(stanceBox);

        // On the green: the read and the green aids instead.
        var green = new VBoxContainer { Visible = false };
        green.AddThemeConstantOverride("separation", 4);
        var readRow = new HBoxContainer();
        readRow.AddChild(UiTheme.Text("Green read", 14, UiTheme.Muted));
        _greenRead = UiTheme.Text("", 17, UiTheme.Ink, bold: true);
        _greenRead.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _greenRead.HorizontalAlignment = HorizontalAlignment.Right;
        readRow.AddChild(_greenRead);
        green.AddChild(readRow);
        _greenReadNote = UiTheme.Text("", 13, UiTheme.Muted);
        _greenReadNote.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _greenReadNote.CustomMinimumSize = new Vector2(244, 0);
        green.AddChild(_greenReadNote);
        var aids = new HBoxContainer();
        aids.AddThemeConstantOverride("separation", 4);
        _flag = new Button { ThemeTypeVariation = "FlatButton", TooltipText = "Leave the flag in the hole, or pull it (the default)  [F]" };
        _flag.Pressed += () => FlagPressed?.Invoke();
        _heatMap = new Button { ThemeTypeVariation = "FlatButton", ToggleMode = true, Text = "Heat map", TooltipText = "Colour the green by height: blue low, red high  [H]" };
        _heatMap.Pressed += () => HeatMapPressed?.Invoke();
        _grid = new Button { ThemeTypeVariation = "FlatButton", ToggleMode = true, Text = "Grid",
                             TooltipText = "Grid on the green, with dots rolling downhill along it (faster = steeper)  [G]" };
        _grid.Pressed += () => GridPressed?.Invoke();
        aids.AddChild(_flag);
        aids.AddChild(_heatMap);
        aids.AddChild(_grid);
        green.AddChild(aids);
        _greenRows = green;
        v.AddChild(green);
        panel.AddChild(v);
    }

    /// <summary>One labelled block of the shot bar: caption, main value, optional notes.</summary>
    private static VBoxContainer PlanBlock(string caption, out Label value, float width)
    {
        var v = new VBoxContainer { CustomMinimumSize = new Vector2(width, 0) };
        v.AddThemeConstantOverride("separation", 2);
        v.AddChild(UiTheme.Caption(caption));
        value = UiTheme.Text("—", 34, UiTheme.Ink, bold: true);
        v.AddChild(value);
        return v;
    }

    private static Label Note(VBoxContainer block, float width, int size = 13, Color? color = null)
    {
        var note = UiTheme.Text("", size, color ?? UiTheme.Muted);
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        note.CustomMinimumSize = new Vector2(width, 0);
        block.AddChild(note);
        return note;
    }

    private void BuildShotPanel()
    {
        var panel = _shotPanel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Bar, 16, 1, 16));
        panel.SetAnchorsPreset(LayoutPreset.BottomRight);
        panel.GrowHorizontal = GrowDirection.Begin;
        panel.GrowVertical = GrowDirection.Begin;
        panel.OffsetLeft = panel.OffsetRight = -16;
        panel.OffsetTop = panel.OffsetBottom = -16;
        _courseView.AddChild(panel);

        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 18);
        panel.AddChild(h);

        // To the pin, with the rise or fall to it ("153 yds", "up 2'").
        var pin = PlanBlock("To the pin", out _toPin, 160);
        _elevation = Note(pin, 160, 17, UiTheme.Ink);
        _playsLike = Note(pin, 160);
        h.AddChild(pin);
        h.AddChild(new VSeparator());

        var clubBlock = new VBoxContainer { CustomMinimumSize = new Vector2(200, 0) };
        clubBlock.AddThemeConstantOverride("separation", 6);
        clubBlock.AddChild(UiTheme.Caption("Club"));
        _courseClub = ClubPicker();
        clubBlock.AddChild(_courseClub);
        _clubNote = Note(clubBlock, 200);

        clubBlock.AddChild(new Control { CustomMinimumSize = new Vector2(0, 2) });
        clubBlock.AddChild(UiTheme.Caption("Aim"));
        var aimRow = new HBoxContainer();
        aimRow.AddThemeConstantOverride("separation", 6);
        AddAimButtons(aimRow, 44);
        _aim = UiTheme.Text("", 13, UiTheme.Muted);
        _aim.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _aim.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _aim.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        aimRow.AddChild(_aim);
        clubBlock.AddChild(aimRow);
        h.AddChild(clubBlock);
        h.AddChild(new VSeparator());

        // The shot controls and HIT SHOT are moved in here on a course (see PlaceShotControls).
        _courseShotSlot = new HBoxContainer();
        h.AddChild(_courseShotSlot);
        // Club, aim and the shot's details (with their separators) only matter while there's a shot to play.
        _planDetails = new Control[] { h.GetChild<Control>(1), clubBlock, h.GetChild<Control>(3), Shot };
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
        var newRound = new Button { Text = "New round", ThemeTypeVariation = "FlatButton",
                                    TooltipText = "Start a new round from the first tee (this one is not kept)" };
        newRound.Pressed += () => { _scorecardPanel.Visible = false; NewRoundPressed?.Invoke(); };
        head.AddChild(newRound);
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

    /// <summary>
    /// The hole card: number, par, length and name; the stroke being played (or how the hole ended) and the score
    /// (to par over the holes played, with a note such as "thru 4").
    /// </summary>
    public void SetHoleCard(int number, string name, int par, double yards, string stroke, string score, string scoreNote)
    {
        _holeNumber.Text = $"HOLE {number}";
        _holeFacts.Text = $"Par {par}  ·  {yards:0} yd";
        _holeName.Text = name;
        _holeName.Visible = name.Length > 0;
        _stroke.Text = stroke;
        _score.Text = score;
        _scoreNote.Text = scoreNote;
    }

    private Label _turn = null!, _standings = null!;

    /// <summary>
    /// Multiplayer: "ANN TO PLAY" in her colour, and everyone's score to par (with this hole so far). Null
    /// <paramref name="player"/> hides the turn line (one player, or the hole is finished).
    /// </summary>
    public void SetTurn(string? player, Color color, string standings)
    {
        _turn.Visible = player != null;
        _turn.Text = player == null ? "" : $"{player.ToUpperInvariant()} TO PLAY";
        _turn.AddThemeColorOverride("font_color", color);
        SetNote(_standings, standings);
    }

    /// <summary>The mulligan button: hidden when the house rules give none, greyed out when it can't be used now.</summary>
    public void SetMulligans(int perRound, int left, bool canUse)
    {
        _mulligan.Visible = perRound > 0;
        _mulligan.Text = $"Mulligan  ({left} left)";
        _mulligan.Disabled = !canUse;
    }

    /// <summary>Shows which green-reading aids are on (the buttons also toggle themselves when clicked).</summary>
    public void SetGreenOverlays(bool heatMap, bool grid)
    {
        _heatMap.SetPressedNoSignal(heatMap);
        _grid.SetPressedNoSignal(grid);
        _heatMap.Text = heatMap ? "Heat map: on" : "Heat map";
        _grid.Text = grid ? "Grid: on" : "Grid";
    }

    public void SetShotPlan(ShotPlan p)
    {
        _toPin.Text = p.ToPin;
        SetNote(_elevation, p.Elevation);
        SetNote(_playsLike, p.PlaysLike);

        _lieName.Text = p.LieName;
        _lieChip.AddThemeStyleboxOverride("panel", UiTheme.Box(p.LieColor, 8, 0, 3, 12));
        _lieView.Show(p.Lie, p.UpSlope, p.SideSlope);
        _stanceRows.Visible = !p.OnGreen;
        _greenRows.Visible = p.OnGreen;
        _sideSlope.Text = Math.Abs(p.SideSlope) < 0.5 ? "Level" : $"{(p.SideSlope > 0 ? "Ball above feet" : "Ball below feet")} {Math.Abs(p.SideSlope):0.0}°";
        _upSlope.Text = Math.Abs(p.UpSlope) < 0.5 ? "Level" : $"{(p.UpSlope > 0 ? "Uphill" : "Downhill")} {Math.Abs(p.UpSlope):0.0}°";
        int penalty = (int)Math.Round(p.Penalty * 100);
        _penalty.Text = penalty <= 0 ? "None" : $"−{penalty}%";
        _penalty.AddThemeColorOverride("font_color", penalty <= 0 ? UiTheme.Accent : penalty < 15 ? UiTheme.Warn : UiTheme.Danger);
        SetNote(_lieNote, p.LieNote);
        _greenRead.Text = p.GreenRead;
        SetNote(_greenReadNote, p.GreenReadNote);
        _flag.Text = p.FlagIn ? "Flag: in" : "Flag: pulled";

        SetNote(_clubNote, p.ClubNote);
        _aim.Text = p.Aim;
        // Hole finished: the bar becomes "Holed" plus a NEXT HOLE action.
        _holeDone = p.HoleDone;
        foreach (var c in _planDetails) c.Visible = !p.HoleDone;
        Shot.Hit.Text = p.HoleDone ? NextHoleText : "HIT SHOT";
        Shot.Hit.TooltipText = p.HoleDone ? "Go to the next hole  [Space, N]" : HitTip;
        Shot.Hit.Disabled = !p.CanHit && !p.HoleDone;
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
    public bool ScorecardOpen => _scorecardPanel.Visible;

    /// <summary>
    /// Rows: hole numbers, par, then a score row per player (blank until played), with a total column. Courses
    /// longer than 9 holes are shown nine at a time (Out / In), with the overall total at the end of the back nine.
    /// </summary>
    public void SetScorecard(string title, IReadOnlyList<int> pars, IReadOnlyList<ScorecardRow> players, int current)
    {
        _scorecardTitle.Text = title;
        foreach (var c in _scorecard.GetChildren()) c.Free();
        int perRow = pars.Count > 9 ? 9 : pars.Count;
        bool split = pars.Count > perRow;
        _scorecard.Columns = perRow + (split ? 3 : 2);
        void Cell(string text, Color? color = null, bool bold = false, bool first = false)
        {
            var l = UiTheme.Text(text, 15, color ?? UiTheme.Ink, bold);
            l.HorizontalAlignment = first ? HorizontalAlignment.Left : HorizontalAlignment.Center;
            l.CustomMinimumSize = new Vector2(first ? 96 : 34, 0);
            l.ClipText = first;
            _scorecard.AddChild(l);
        }
        Color ScoreColor(IReadOnlyList<int?> scores, int i) => scores[i] is not { } s ? UiTheme.Muted
            : s < pars[i] ? UiTheme.Accent : s > pars[i] ? UiTheme.Warn : UiTheme.Ink;
        string Total(IReadOnlyList<int?> scores, int from, int count)
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

            Cell("Hole", UiTheme.Muted, first: true);
            for (int i = start; i < start + n; i++) Cell((i + 1).ToString(), i == current ? UiTheme.Accent : UiTheme.Muted, i == current);
            Pad();
            Cell(label, UiTheme.Muted);
            if (split) Cell(last ? "Tot" : "", UiTheme.Muted);

            Cell("Par", UiTheme.Muted, first: true);
            for (int i = start; i < start + n; i++) Cell(pars[i].ToString());
            Pad();
            Cell(pars.Skip(start).Take(n).Sum().ToString());
            if (split) Cell(last ? pars.Sum().ToString() : "");

            foreach (var p in players)
            {
                Cell(players.Count == 1 ? "Score" : p.Name, players.Count == 1 ? UiTheme.Muted : p.Color, bold: players.Count > 1, first: true);
                for (int i = start; i < start + n; i++) Cell(p.Scores[i]?.ToString() ?? "–", ScoreColor(p.Scores, i), p.Scores[i] != null);
                Pad();
                Cell(Total(p.Scores, start, n), bold: true);
                if (split) Cell(last ? Total(p.Scores, 0, pars.Count) : "", bold: true);
            }
        }
    }

    public static string ToPar(int d) => d == 0 ? "E" : d > 0 ? $"+{d}" : d.ToString();
}
