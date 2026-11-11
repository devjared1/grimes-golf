using Godot;
using GrimesGolf.Core;

namespace GrimesGolf.Game;

/// <summary>
/// The course view, laid out round the ball in the middle so the hole stays in sight. There's no top bar: the hole
/// badge top left (Menu and the brand; course logo, hole, par, length, stroke and score; the Ready light, monitor
/// status, whose turn and the mulligan under it), the hole overview map top right (built in <c>Hud.cs</c>) with the
/// wind under it, the last shot's numbers down the left, and bottom right the lie card (the lie, the slopes and the
/// shot penalty; on the green, the read and green aids) over the shot panel (to the pin with the elevation, club, aim,
/// power, shape, spin and HIT SHOT). The result line runs across the top, and the scorecard opens over it all. With a
/// launch monitor connected the swing replaces the on-screen shot controls: the shot panel narrows to to the pin over
/// the club, and a small leaderboard takes the bottom left.
/// </summary>
public partial class Hud
{
    // ---- course view -------------------------------------------------------------------------

    /// <summary>
    /// The course view's layout, edited in the Godot editor. Nodes the code uses are marked "Access as Unique Name"
    /// (%) there, so they can be moved around the tree freely; keep their names and types. The code adds what it
    /// can't know up front: the wind, the club list, the aim and align buttons, the shot controls, and the rows of
    /// the last shot, the leaderboard and the scorecard.
    /// </summary>
    private const string CourseViewScene = "res://HudCourse.tscn";

    private void BuildCourseView()
    {
        _courseView = GD.Load<PackedScene>(CourseViewScene).Instantiate<Control>();
        AddChild(_courseView);
        T N<T>(string name) where T : Node => _courseView.GetNode<T>("%" + name);

        // Hole badge and what's under it
        _courseLogo = N<TextureRect>("CourseLogo");
        _holeNumber = N<Label>("HoleNumber");
        _holeFacts = N<Label>("HoleFacts");
        _holeName = N<Label>("HoleName");
        _stroke = N<Label>("Stroke");
        _score = N<Label>("Score");
        _scoreNote = N<Label>("ScoreNote");
        _turn = N<Label>("Turn");
        _standings = N<Label>("Standings");
        _mulligan = N<Button>("Mulligan");
        _mulligan.Pressed += () => MulliganPressed?.Invoke();
        _credit = N<Label>("Credit");

        // There's no top bar over the course: Menu and the brand head the hole badge, and the Ready light and the
        // monitor status (which opens the monitor drawer) sit under it.
        N<Button>("CourseMenu").Pressed += () => MenuPressed?.Invoke();
        if (Brand.Logo is { } logo)
        {
            var logoRect = Brand.LogoRect(logo, new Vector2(150, 30), TextureRect.StretchModeEnum.KeepAspectCentered);
            logoRect.TooltipText = $"{Brand.Name} Simulator {Version}";
            logoRect.Modulate = new Color(1.5f, 1.5f, 1.5f);   // the fine script dims at this size
            N<HBoxContainer>("BrandSlot").AddChild(logoRect);
        }
        _statusRow = N<HBoxContainer>("StatusRow");
        _courseStatus = N<Button>("MonitorStatus");
        _courseStatus.Pressed += ToggleDrawer;

        // Last shot, lie card, leaderboard
        _lastShot = N<PanelContainer>("LastShot");
        _lastShotOffset = _lastShot.OffsetTop;
        _holeColumn = _courseView.GetNodeOrNull<Control>("HoleColumn");
        _resultStats = N<VBoxContainer>("ResultStats");
        _liePanel = N<PanelContainer>("LiePanel");
        _lieChip = N<PanelContainer>("LieChip");
        _lieName = N<Label>("LieName");
        _stanceRows = N<Control>("StanceRows");
        _sideSlope = N<Label>("SideSlope");
        _upSlope = N<Label>("UpSlope");
        _penalty = N<Label>("Penalty");
        _lieNote = N<Label>("LieNote");
        _greenRows = N<Control>("GreenRows");
        _greenRead = N<Label>("GreenRead");
        _greenReadNote = N<Label>("GreenReadNote");
        _flag = N<Button>("Flag");
        _flag.Pressed += () => FlagPressed?.Invoke();
        _heatMap = N<Button>("HeatMap");
        _heatMap.Pressed += () => HeatMapPressed?.Invoke();
        _grid = N<Button>("Grid");
        _grid.Pressed += () => GridPressed?.Invoke();
        _leaderboard = N<PanelContainer>("Leaderboard");
        _leaderboardCaption = N<Label>("LeaderboardCaption");
        _leaderboardRows = N<GridContainer>("LeaderboardRows");

        // Shot panel (its first child must stay the row SetCompact tightens), with the lie card above it
        _shotPanel = N<PanelContainer>("ShotPanel");
        _planTop = N<BoxContainer>("Top");
        _clubCaption = N<Label>("ClubCaption");
        _toPin = N<Label>("ToPin");
        _elevation = N<Label>("Elevation");
        _playsLike = N<Label>("PlaysLike");
        _clubBlock = N<Control>("ClubBlock");
        _courseClub = ClubPicker(N<OptionButton>("CourseClub"));
        _courseAimRow = N<HBoxContainer>("AimRow");
        AddAimButtons((HBoxContainer)_courseAimRow, 40);
        _aim = N<Label>("Aim");
        N<HBoxContainer>("ClubRow").AddChild(AlignButton());
        _courseShotSlot = N<HBoxContainer>("CourseShotSlot");

        // Result line, scorecard, hint
        _result = N<PanelContainer>("Result");
        _resultHeadline = N<Label>("ResultHeadline");
        _resultNote = N<Label>("ResultNote");
        _scorecardPanel = N<PanelContainer>("Scorecard");
        _scorecardTitle = N<Label>("ScorecardTitle");
        _scorecard = N<GridContainer>("ScorecardGrid");
        _scorecardNote = N<Label>("ScorecardNote");
        N<Button>("NewRound").Pressed += () => { _scorecardPanel.Visible = false; NewRoundPressed?.Invoke(); };
        N<Button>("CloseScorecard").Pressed += ToggleScorecard;
        _hint = N<PanelContainer>("Hint");
        _hintText = N<Label>("HintText");

        // The wind goes under the map (placed in LayoutMinimap), drawn under the hint.
        _wind = new WindIndicator { Visible = false };
        _wind.SetAnchorsPreset(LayoutPreset.TopRight);
        _wind.GrowHorizontal = GrowDirection.Begin;
        _courseView.AddChild(_wind);
        _courseView.MoveChild(_wind, _hint.GetIndex());
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

    /// <summary>The course's logo on the hole badge (hidden when null).</summary>
    public void SetCourseLogo(Texture2D? logo)
    {
        _courseLogo.Texture = logo;
        _courseLogo.Visible = logo != null;
    }

    /// <summary>
    /// The wind for the shot coming up (hidden when calm). <paramref name="blowsToward"/> is the way it blows relative
    /// to the aim (radians, 0 = down the aim line, positive = to the right).
    /// </summary>
    public void SetWind(double speedMph, double gustMph, double blowsToward, string note)
    {
        _wind.Visible = speedMph >= 0.5;
        if (_wind.Visible) _wind.Show(speedMph, gustMph, blowsToward, note);
    }

    private Control _clubBlock = null!, _courseAimRow = null!;
    private BoxContainer _planTop = null!;
    private Label _clubCaption = null!;
    private HBoxContainer _statusRow = null!;
    private Button _courseStatus = null!;
    private bool _holeDone, _monitorLive;
    private string _standingsText = "";

    /// <summary>A launch monitor is connected and talking: real swings replace the on-screen shot controls.</summary>
    public void SetMonitorLive(bool live)
    {
        if (_monitorLive == live) return;
        _monitorLive = live;
        UpdateShotLayout();
    }

    /// <summary>
    /// Shows what the moment calls for. The lie card always sits on top of the shot panel, bottom right. No monitor:
    /// the on-screen shot controls, with to the pin beside the club and aim. With a monitor: a narrow column (to the
    /// pin over the club, the lie card the same width above) and the leaderboard bottom left. Either way, club and the
    /// shot's details only matter while there's a shot to play, and NEXT HOLE (on the HIT SHOT button) stays.
    /// </summary>
    private void UpdateShotLayout()
    {
        Shot.Visible = !_monitorLive && !_holeDone;
        Shot.Hit.Visible = !_monitorLive || _holeDone;
        _courseShotSlot.Visible = !_monitorLive || _holeDone;
        _clubBlock.Visible = !_holeDone;
        _courseAimRow.Visible = !_monitorLive;
        foreach (var b in _aimButtons) b.Visible = !_monitorLive;
        foreach (var b in _alignButtons) b.Visible = _monitorLive;
        _rangeShotSlot.Visible = _rangeView.Visible && !_monitorLive;
        PlaceBallZone();

        _planTop.Vertical = _monitorLive;
        _clubCaption.Visible = !_monitorLive;
        var width = _monitorLive ? SizeFlags.Fill : SizeFlags.ShrinkEnd;
        _liePanel.SizeFlagsHorizontal = width;
        _shotPanel.SizeFlagsHorizontal = width;
        _leaderboard.Visible = _monitorLive && _leaderboardRowCount > 1;
        SetNote(_standings, _monitorLive ? "" : _standingsText);
    }

    /// <summary>Rows on the leaderboard: with only one player (or one team) there's nobody to compare with, so it isn't shown.</summary>
    private int _leaderboardRowCount;

    private readonly List<Button> _alignButtons = new();
    private string _alignmentShort = "Align", _alignmentLong = "Not aligned this session";

    /// <summary>Shows the alignment and opens the panel to change it (with a monitor connected).</summary>
    private Button AlignButton()
    {
        var b = new Button { Text = _alignmentShort, Visible = false, CustomMinimumSize = new Vector2(0, 36), SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
                             TooltipText = _alignmentLong, FocusMode = FocusModeEnum.None };
        b.Pressed += () => AlignPressed?.Invoke();
        _alignButtons.Add(b);
        return b;
    }

    /// <summary>The alignment set this session: "R1.2°" and when (null: none yet).</summary>
    public void SetAlignment(string? angle, string when)
    {
        _alignmentShort = angle == null ? "Align" : $"Aligned {angle}";
        _alignmentLong = (angle == null ? "Not aligned this session" : $"Aligned {angle} at {when}") + ". Click to align with the white stick.";
        foreach (var b in _alignButtons) { b.Text = _alignmentShort; b.TooltipText = _alignmentLong; }
        SetStat("Alignment", angle == null ? "Not this session" : $"{angle} at {when}");
    }

    /// <summary>The ball position panel is hidden for now: set this to true to bring it back.</summary>
    private static readonly bool ShowBallZone = false;

    private BallZone _ballZone = null!;
    private Core.Square.BallPosition? _ball;
    private bool _ballIsReady;

    /// <summary>The ball the monitor sees (null: none), for the ball position panel.</summary>
    public void SetBall(Core.Square.BallPosition? ball, bool ready = false)
    {
        _ball = ball;
        _ballIsReady = ready;
        _ballZone.Show(ball, ready);
        PlaceBallZone();
    }

    /// <summary>Beside the leaderboard on a course, above the numbers on the range. Only while a monitor is seeing a ball.</summary>
    private void PlaceBallZone()
    {
        _ballZone.Visible = ShowBallZone && _monitorLive && _ball != null && !_menuShown;
        _ballZone.SetAnchorsPreset(LayoutPreset.BottomLeft);
        _ballZone.GrowHorizontal = GrowDirection.End;
        _ballZone.GrowVertical = GrowDirection.Begin;
        _ballZone.OffsetLeft = _ballZone.OffsetRight = _courseMode ? 272 : 16;
        _ballZone.OffsetTop = _ballZone.OffsetBottom = _courseMode ? -16 : -RangeBarHeight - 12;
    }

    private PanelContainer _leaderboard = null!;
    private Label _leaderboardCaption = null!;
    private GridContainer _leaderboardRows = null!;

    /// <summary>The small leaderboard's rows, best first (shown with a monitor connected, when there's more than one).</summary>
    public void SetLeaderboard(string caption, IReadOnlyList<LeaderboardRow> rows)
    {
        _leaderboardCaption.Text = caption;
        _leaderboardRowCount = rows.Count;
        _leaderboard.Visible = _monitorLive && rows.Count > 1;
        foreach (var c in _leaderboardRows.GetChildren()) { _leaderboardRows.RemoveChild(c); c.QueueFree(); }
        foreach (var r in rows)
        {
            var name = UiTheme.Text(r.Name, 16, r.Color, bold: r.Up);
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            name.CustomMinimumSize = new Vector2(110, 0);
            name.ClipText = true;
            _leaderboardRows.AddChild(name);
            var score = UiTheme.Text(r.Score, 16, UiTheme.Ink, bold: true);
            score.HorizontalAlignment = HorizontalAlignment.Right;
            _leaderboardRows.AddChild(score);
            var note = UiTheme.Text(r.Note, 13, UiTheme.Muted);
            note.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            note.CustomMinimumSize = new Vector2(44, 0);
            _leaderboardRows.AddChild(note);
        }
    }

    /// <summary>
    /// The hole badge: number, par, length and name; the stroke being played (or how the hole ended) and the score
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
        _standingsText = standings;
        SetNote(_standings, _monitorLive ? "" : standings);   // with a monitor, the leaderboard shows this
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
        _stanceRows.Visible = !p.OnGreen;
        _greenRows.Visible = p.OnGreen;
        _sideSlope.Text = Math.Abs(p.SideSlope) < 0.5 ? "Level" : $"{(p.SideSlope > 0 ? "Above feet" : "Below feet")} {Math.Abs(p.SideSlope):0.0}°";
        _upSlope.Text = Math.Abs(p.UpSlope) < 0.5 ? "Level" : $"{(p.UpSlope > 0 ? "Uphill" : "Downhill")} {Math.Abs(p.UpSlope):0.0}°";
        int penalty = (int)Math.Round(p.Penalty * 100);
        _penalty.Text = penalty <= 0 ? "None" : $"−{penalty}%";
        _penalty.AddThemeColorOverride("font_color", penalty <= 0 ? UiTheme.Accent : penalty < 15 ? UiTheme.Warn : UiTheme.Danger);
        SetNote(_lieNote, p.LieNote);
        _liePanel.TooltipText = "Shot penalty: how much shorter the club carries from this lie and stance than from a clean, level one";
        _greenRead.Text = p.GreenRead;
        SetNote(_greenReadNote, p.GreenReadNote);
        _flag.Text = p.FlagIn ? "Flag: in" : "Flag: out";

        // What the club carries from here goes on the club picker's tooltip.
        _courseClub.TooltipText = (p.ClubNote.Length > 0 ? p.ClubNote + "\n" : "") + "Club (sent to the monitor)  [numpad 8 / 2, D-pad ↑ / ↓]";
        _aim.Text = p.Aim;
        // Hole finished: the panel becomes "Holed" plus a NEXT HOLE action.
        _holeDone = p.HoleDone;
        UpdateShotLayout();
        Shot.Hit.Text = p.HoleDone ? NextHoleText : "HIT SHOT";
        Shot.Hit.TooltipText = p.HoleDone ? "Go to the next hole  [Space, N]" : HitTip;
        Shot.Hit.Disabled = !p.CanHit && !p.HoleDone;
    }

    private static void SetNote(Label l, string text)
    {
        l.Text = text;
        l.Visible = text.Length > 0;
    }

    /// <summary>
    /// After a shot, the line across the top: only for what needs saying (holed, a penalty, relief, who's up, the
    /// next hole). Where the ball lies and how far it has left are on the lie card and in the shot's numbers, so a
    /// plain result shows nothing here. Empty text hides it; it otherwise stays until <see cref="HideResult"/>.
    /// </summary>
    public void ShowResult(string headline, string note = "")
    {
        if (headline.Length == 0) (headline, note) = (note, "");
        _result.Visible = headline.Length > 0;
        if (!_result.Visible) return;
        _resultHeadline.Text = headline;
        SetNote(_resultNote, note);
        _result.Modulate = new Color(1, 1, 1, 0);
        CreateTween().TweenProperty(_result, "modulate:a", 1.0f, 0.25);
    }

    private Control? _holeColumn;
    private float _lastShotOffset;

    /// <summary>
    /// The shot's numbers sit centred down the left (as laid out in the scene), but never up over the hole badge and
    /// what's under it: they're moved down when they'd reach it. Every frame, since either can change height.
    /// </summary>
    private void KeepLastShotClear()
    {
        if (!_courseMode || !_lastShot.Visible || _holeColumn is not { Visible: true }) return;
        float height = _lastShot.GetCombinedMinimumSize().Y;
        float top = _courseView.Size.Y / 2 + _lastShotOffset - height / 2;   // where the scene's anchors put it (it grows both ways)
        float clear = _holeColumn.GetGlobalRect().End.Y - _courseView.GetGlobalRect().Position.Y + 12;
        float offset = _lastShotOffset + Math.Max(0, clear - top);
        if (_lastShot.OffsetTop != offset) _lastShot.OffsetTop = _lastShot.OffsetBottom = offset;
    }

    /// <summary>
    /// The shot's numbers down the left, three to a row: the launch (shown as soon as the ball is struck) and, under it,
    /// the result (added once the ball has stopped). They stay until <see cref="HideResult"/> (the next shot, or a new hole).
    /// </summary>
    public void ShowShotNumbers(IReadOnlyList<Stat> launch, IReadOnlyList<Stat> result)
    {
        foreach (var c in _resultStats.GetChildren()) { _resultStats.RemoveChild(c); c.QueueFree(); }
        void Group(string caption, IReadOnlyList<Stat> stats)
        {
            if (stats.Count == 0) return;
            if (_resultStats.GetChildCount() > 0) _resultStats.AddChild(new HSeparator());
            _resultStats.AddChild(UiTheme.Caption(caption));
            var grid = new GridContainer { Columns = 3 };
            grid.AddThemeConstantOverride("h_separation", 18);
            grid.AddThemeConstantOverride("v_separation", 6);
            foreach (var s in stats) grid.AddChild(StatBlock(s));
            _resultStats.AddChild(grid);
        }
        Group("LAUNCH", launch);
        Group("RESULT", result);

        bool wasShown = _lastShot.Visible;
        _lastShot.Visible = launch.Count + result.Count > 0;
        if (_lastShot.Visible && !wasShown)
        {
            _lastShot.Modulate = new Color(1, 1, 1, 0);
            CreateTween().TweenProperty(_lastShot, "modulate:a", 1.0f, 0.2);
        }
    }

    private static Control StatBlock(Stat s)
    {
        var b = new VBoxContainer { CustomMinimumSize = new Vector2(96, 0) };
        b.AddThemeConstantOverride("separation", -2);
        b.AddChild(UiTheme.Caption(s.Caption));
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        row.AddChild(UiTheme.Text(s.Value, 26, UiTheme.Ink, bold: true));
        if (s.Unit.Length > 0)
        {
            var u = UiTheme.Text(s.Unit, 13, UiTheme.Muted);
            u.SizeFlagsVertical = SizeFlags.ShrinkEnd;
            row.AddChild(UiTheme.Pad(u, 0, 4, mouse: false));
        }
        b.AddChild(row);
        return b;
    }

    public void HideResult()
    {
        _result.Visible = false;
        _lastShot.Visible = false;
    }

    public void ToggleScorecard() => _scorecardPanel.Visible = _courseMode && !_scorecardPanel.Visible;
    public void ShowScorecard(bool show) => _scorecardPanel.Visible = _courseMode && show;
    public bool ScorecardOpen => _scorecardPanel.Visible;

    /// <summary>
    /// Rows: hole numbers, par, then a score row per player or team (blank until played), with a total column.
    /// Rounds longer than 9 holes are shown nine at a time (Out / In), with the overall total at the end of the back
    /// nine. A row with <see cref="ScorecardRow.Marks"/> (the match) shows those instead of scores.
    /// </summary>
    public void SetScorecard(string title, IReadOnlyList<int> numbers, IReadOnlyList<int> pars, IReadOnlyList<ScorecardRow> players, int current)
    {
        _scorecardTitle.Text = title;
        // The match's standing goes under the card (it doesn't fit a total column).
        SetNote(_scorecardNote, players.FirstOrDefault(p => p.TotalText != null)?.TotalText ?? "");
        FillScorecard(_scorecard, numbers, pars, players, current);
    }

    /// <summary>Fills <paramref name="grid"/> with a scorecard (see <see cref="SetScorecard"/>; also used by History). <paramref name="current"/> -1 highlights no hole.</summary>
    public static void FillScorecard(GridContainer grid, IReadOnlyList<int> numbers, IReadOnlyList<int> pars, IReadOnlyList<ScorecardRow> players, int current)
    {
        foreach (var c in grid.GetChildren()) c.Free();
        int perRow = pars.Count > 9 ? 9 : pars.Count;
        bool split = pars.Count > perRow;
        grid.Columns = perRow + (split ? 3 : 2);
        void Cell(string text, Color? color = null, bool bold = false, bool first = false)
        {
            var l = UiTheme.Text(text, 15, color ?? UiTheme.Ink, bold);
            l.HorizontalAlignment = first ? HorizontalAlignment.Left : HorizontalAlignment.Center;
            l.CustomMinimumSize = new Vector2(first ? (players.Any(p => p.Team) ? 136 : 96) : 34, 0);
            l.ClipText = first;
            grid.AddChild(l);
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
            for (int i = start; i < start + n; i++) Cell(numbers[i].ToString(), i == current ? UiTheme.Accent : UiTheme.Muted, i == current);
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
                if (p.Marks is { } marks)
                {
                    for (int i = start; i < start + n; i++) Cell(marks[i]?.Text ?? "–", marks[i]?.Color ?? UiTheme.Muted, marks[i] != null);
                    Pad();
                    Cell("");
                    if (split) Cell("");
                    continue;
                }
                for (int i = start; i < start + n; i++) Cell(p.Scores[i]?.ToString() ?? "–", ScoreColor(p.Scores, i), p.Scores[i] != null || p.Team);
                Pad();
                Cell(Total(p.Scores, start, n), bold: true);
                if (split) Cell(last ? Total(p.Scores, 0, pars.Count) : "", bold: true);
            }
        }
    }

    public static string ToPar(int d) => d == 0 ? "E" : d > 0 ? $"+{d}" : d.ToString();
}
