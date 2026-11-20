using Godot;

namespace GrimesGolf.Game;

/// <summary>Where the next ball goes once one is holed on a practice green.</summary>
public enum AfterHoleOut { NextSpot, RandomSpot, SameSpot, Wait }

/// <summary>
/// The practice greens' bits of the course view: a panel under the hole badge to pick the hole to aim at and the spot the ball
/// is on (or put it anywhere by clicking the overview map), the session's numbers in a card under the overview map; the hole
/// badge says which green it is and how many balls have been holed, and the Mulligan button becomes Retry. Built in <c>Hud.cs</c>'s <c>_Ready</c> and shown only
/// while <see cref="SetPracticeMode"/> is on.
/// </summary>
public partial class Hud
{
    /// <summary>The hole to aim at: the next one to the right (+1) or to the left (−1) of this one.</summary>
    public event Action<int>? PracticeHolePressed;
    /// <summary>The next (+1) or previous (−1) spot to put the ball on.</summary>
    public event Action<int>? PracticeSpotPressed;
    public event Action? PracticePlacePressed, PracticeRandomPressed, PracticeAfterPressed, PracticeResetStatsPressed;

    private PanelContainer _practicePanel = null!, _practiceSession = null!;
    private Label _practiceHole = null!, _practiceSpot = null!, _strokeCaption = null!, _scoreCaption = null!;
    private Button _practicePlace = null!, _practiceAfter = null!;
    private GridContainer _practiceStats = null!;
    private bool _practiceMode, _practicePlacing;
    private string _mulliganTip = "";

    /// <summary>What the HIT SHOT button's next action is called ("HOLE": the next hole of a round; "BALL": the next ball in practice).</summary>
    private string _nextWord = "HOLE";

    private void BuildPracticePanel()
    {
        var column = _courseView.GetNode<VBoxContainer>("HoleColumn");
        _strokeCaption = _courseView.GetNode<Label>("HoleColumn/HoleCard/Stack/Row/StrokeFigure/Caption");
        _scoreCaption = _courseView.GetNode<Label>("HoleColumn/HoleCard/Stack/Row/ScoreFigure/Caption");
        _mulliganTip = _mulligan.TooltipText;

        _practicePanel = new PanelContainer { Visible = false, CustomMinimumSize = new Vector2(320, 0) };
        _practicePanel.AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(UiTheme.Bar, 0.9f), 12, 1, 10, 12));
        column.AddChild(_practicePanel);
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 6);
        _practicePanel.AddChild(v);

        // Which hole to aim at, and which spot the ball is on: a stepper each.
        (Label Value, Button Left, Button Right) Stepper(string caption, string leftTip, string rightTip)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);
            var c = UiTheme.Caption(caption);
            c.CustomMinimumSize = new Vector2(40, 0);
            c.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            row.AddChild(c);
            Button Step(string text, string tip)
            {
                var b = new Button { Text = text, CustomMinimumSize = new Vector2(38, TapHeight - 4), TooltipText = tip, FocusMode = FocusModeEnum.None };
                b.AddThemeFontSizeOverride("font_size", 20);
                return b;
            }
            var left = Step("‹", leftTip);
            row.AddChild(left);
            var value = UiTheme.Text("", 15, UiTheme.Ink, bold: true);
            value.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            value.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            value.HorizontalAlignment = HorizontalAlignment.Center;
            value.ClipText = true;
            row.AddChild(value);
            var right = Step("›", rightTip);
            row.AddChild(right);
            v.AddChild(row);
            return (value, left, right);
        }
        var hole = Stepper("Hole", "Aim at the hole to the left  [Shift+F]", "Aim at the hole to the right  [F, gamepad Y]");
        _practiceHole = hole.Value;
        hole.Left.Pressed += () => PracticeHolePressed?.Invoke(-1);
        hole.Right.Pressed += () => PracticeHolePressed?.Invoke(1);
        var spot = Stepper("Ball", "The spot before  [Shift+N]", "The next spot  [N]");
        _practiceSpot = spot.Value;
        spot.Left.Pressed += () => PracticeSpotPressed?.Invoke(-1);
        spot.Right.Pressed += () => PracticeSpotPressed?.Invoke(1);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 6);
        _practicePlace = new Button
        {
            Text = "Place on map", ToggleMode = true, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, TapHeight - 4), FocusMode = FocusModeEnum.None,
            TooltipText = "Then click the overview map to put the ball there",
        };
        _practicePlace.Pressed += () => PracticePlacePressed?.Invoke();
        buttons.AddChild(_practicePlace);
        var random = new Button { Text = "Random", CustomMinimumSize = new Vector2(0, TapHeight - 4), FocusMode = FocusModeEnum.None, TooltipText = "Put the ball on any other spot" };
        random.Pressed += () => PracticeRandomPressed?.Invoke();
        buttons.AddChild(random);
        v.AddChild(buttons);

        _practiceAfter = new Button { CustomMinimumSize = new Vector2(0, TapHeight - 4), FocusMode = FocusModeEnum.None,
                                      TooltipText = "What happens to the ball once it's holed: click to change" };
        _practiceAfter.AddThemeFontSizeOverride("font_size", 13);
        _practiceAfter.Pressed += () => PracticeAfterPressed?.Invoke();
        v.AddChild(_practiceAfter);

        // The session's numbers have a card of their own under the overview map.
        _practiceSession = new PanelContainer { Visible = false };
        _practiceSession.AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(UiTheme.Bar, 0.9f), 12, 1, 7, 12));
        _courseView.AddChild(_practiceSession);
        var session = new VBoxContainer();
        session.AddThemeConstantOverride("separation", 4);
        _practiceSession.AddChild(session);
        var head = new HBoxContainer();
        var title = UiTheme.Caption("This session");
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        head.AddChild(title);
        var reset = new Button { Text = "Reset", ThemeTypeVariation = "FlatButton", FocusMode = FocusModeEnum.None, TooltipText = "Start the session's numbers again" };
        reset.AddThemeFontSizeOverride("font_size", 12);
        reset.CustomMinimumSize = new Vector2(0, 20);
        reset.Pressed += () => PracticeResetStatsPressed?.Invoke();
        head.AddChild(reset);
        session.AddChild(head);
        _practiceStats = new GridContainer { Columns = 2 };
        _practiceStats.AddThemeConstantOverride("v_separation", -1);
        session.AddChild(_practiceStats);
    }

    /// <summary>The session card sits under the overview map, at its right edge (where the wind goes, which practice doesn't have).</summary>
    private void LayoutPracticeSession()
    {
        if (_practiceSession == null) return;
        _practiceSession.SetAnchorsPreset(LayoutPreset.TopRight);
        _practiceSession.GrowHorizontal = GrowDirection.Begin;
        _practiceSession.OffsetRight = -16;
        _practiceSession.OffsetLeft = _minimapFrame.OffsetLeft;
        _practiceSession.OffsetTop = _practiceSession.OffsetBottom = _minimapFrame.OffsetBottom + 6;
    }

    /// <summary>
    /// Practice on or off. On: the panel shows, the hole badge is about the practice green, and what a round has (leaderboard,
    /// whose turn it is, the flag button, the course credit) is hidden.
    /// </summary>
    public void SetPracticeMode(bool on)
    {
        if (_practiceMode == on) return;
        _practiceMode = on;
        _practicePanel.Visible = on;
        _practiceSession.Visible = on;
        _nextWord = on ? "BALL" : "HOLE";
        _scoreCaption.Text = on ? "HOLED OUT" : "SCORE";
        _flag.Visible = !on;
        if (on)
        {
            SetTurn(null, UiTheme.Ink, "");
            SetCredit(null);
            _leaderboard.Visible = false;
            _mulligan.Visible = true;
        }
        else
        {
            SetPracticePlacing(false);
            _mulligan.TooltipText = _mulliganTip;
            _flag.Visible = true;
            _mapOverlay.SetHoles(Array.Empty<Vector2>(), -1);
            UpdateShotLayout();
        }
    }

    /// <summary>The hole badge for a practice green: its name, which hole of how many, the spot, the stroke, and the balls holed so far.</summary>
    public void SetPracticeCard(string title, string facts, string spot, string stroke, string holedOut, string note)
    {
        _holeNumber.Text = title;
        _holeFacts.Text = facts;
        _holeName.Text = spot;
        _holeName.Visible = spot.Length > 0;
        _stroke.Text = stroke;
        _score.Text = holedOut;
        _scoreNote.Text = note;
    }

    /// <summary>The panel's two steppers, the After button and the session's figures.</summary>
    public void SetPracticePanel(string hole, string spot, string after, IReadOnlyList<(string Caption, string Value)> stats)
    {
        _practiceHole.Text = hole;
        _practiceSpot.Text = spot;
        _practiceAfter.Text = after;
        foreach (var c in _practiceStats.GetChildren()) { _practiceStats.RemoveChild(c); c.QueueFree(); }
        foreach (var (caption, value) in stats)
        {
            var name = UiTheme.Text(caption, 12, UiTheme.Muted);
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _practiceStats.AddChild(name);
            var figure = UiTheme.Text(value, 13, UiTheme.Ink, bold: true);
            figure.HorizontalAlignment = HorizontalAlignment.Right;
            _practiceStats.AddChild(figure);
        }
    }

    /// <summary>The Mulligan button as Retry (always there in practice; grayed out when there's nothing to take back).</summary>
    public void SetRetry(string text, bool canUse)
    {
        _mulligan.Visible = true;
        _mulligan.Text = text;
        _mulligan.Disabled = !canUse;
        _mulligan.TooltipText = "Hit it again: puts the ball back where it was before the last shot  [U, gamepad X]";
    }

    /// <summary>The overview map is for putting the ball down (a click moves it there) rather than aiming.</summary>
    public void SetPracticePlacing(bool placing)
    {
        _practicePlacing = placing;
        _practicePlace.SetPressedNoSignal(placing);
        _practicePlace.Text = placing ? "Click the map…" : "Place on map";
        var frame = UiTheme.Box(UiTheme.Card, 12, placing ? 2 : 1, 6);
        if (placing) frame.BorderColor = UiTheme.Accent;
        _minimapFrame.AddThemeStyleboxOverride("panel", frame);
        _minimapContainer.TooltipText = placing ? "Click or drag to put the ball there" : "Overview: click or drag to aim there";
    }

    /// <summary>Every cup on the overview map of a practice green, numbered (points in the minimap viewport's pixels; empty hides them).</summary>
    public void SetMapHoles(IReadOnlyList<Vector2> points, int current)
    {
        var scale = _minimapContainer.Size / (Vector2)Minimap.Size;
        _mapOverlay.SetHoles(points.Select(p => p * scale).ToList(), current);
    }
}
