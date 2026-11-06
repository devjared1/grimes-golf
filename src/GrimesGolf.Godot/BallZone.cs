using Godot;
using GrimesGolf.Core.Square;

namespace GrimesGolf.Game;

/// <summary>
/// Where the monitor sees the ball, from above, so a guest can tell why it isn't "Ready". The monitor reports the
/// ball's position but not where it needs to be, so the zone drawn is where balls have gone ready so far in real use:
/// a guide, not the monitor's own limit. It doesn't report how much of the ball its cameras can see.
/// </summary>
public partial class BallZone : PanelContainer
{
    /// <summary>The spread of positions the monitor called ready (mm), from the first session on hardware 1.5.</summary>
    private const double ZoneXMin = -75, ZoneXMax = 25, ZoneYMin = -75, ZoneYMax = -15;
    /// <summary>The area shown (mm): the zone and what's around it.</summary>
    private const double ViewXMin = -120, ViewXMax = 70, ViewYMin = -145, ViewYMax = 25;
    private const double BallRadiusMm = 21.3;
    /// <summary>A ball on a tee reads about 50 mm higher than on the mat, which reads near −33 mm.</summary>
    private const double TeedAbove = -5;

    private Control _plot = null!;
    private Label _status = null!;
    private BallPosition? _ball;
    private bool _ready;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Bar, 14, 1, 10));
        var v = new VBoxContainer { CustomMinimumSize = new Vector2(170, 0), MouseFilter = MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", 4);
        AddChild(v);
        v.AddChild(UiTheme.Caption("Ball position"));
        _plot = new Control { CustomMinimumSize = new Vector2(170, 120), MouseFilter = MouseFilterEnum.Ignore };
        _plot.Draw += DrawPlot;
        v.AddChild(_plot);
        _status = UiTheme.Text("", 13, UiTheme.Ink);
        _status.HorizontalAlignment = HorizontalAlignment.Center;
        v.AddChild(_status);
    }

    public void Show(BallPosition? ball, bool ready)
    {
        _ball = ball;
        _ready = ready;
        if (ball is { } b)
        {
            bool inZone = b.X is >= ZoneXMin and <= ZoneXMax && b.Y is >= ZoneYMin and <= ZoneYMax;
            string where = b.Z > TeedAbove ? "On a tee" : "On the mat";
            _status.Text = ready ? $"{where}: ready" : inZone ? $"{where}: settling" : $"{where}: outside the usual spot";
            _status.AddThemeColorOverride("font_color", ready ? UiTheme.Accent : UiTheme.Warn);
        }
        _plot.QueueRedraw();
    }

    /// <summary>Up the panel is along the target line, across is sideways: the way Square's own stick screen draws these axes.</summary>
    private Vector2 ToPlot(double x, double y)
    {
        var s = _plot.Size;
        return new Vector2(
            (float)((ViewYMax - y) / (ViewYMax - ViewYMin)) * s.X,
            (float)((ViewXMax - x) / (ViewXMax - ViewXMin)) * s.Y);
    }

    private void DrawPlot()
    {
        var s = _plot.Size;
        _plot.DrawRect(new Rect2(Vector2.Zero, s), UiTheme.Card);
        var a = ToPlot(ZoneXMax, ZoneYMax);
        var b = ToPlot(ZoneXMin, ZoneYMin);
        _plot.DrawRect(new Rect2(a, b - a), new Color(UiTheme.Accent, 0.16f));
        _plot.DrawRect(new Rect2(a, b - a), new Color(UiTheme.Accent, 0.6f), false, 1.5f);
        if (_ball is not { } ball) return;

        float radius = (float)(BallRadiusMm / (ViewYMax - ViewYMin)) * s.X;
        var at = ToPlot(ball.X, ball.Y);
        at = new Vector2(Mathf.Clamp(at.X, radius, s.X - radius), Mathf.Clamp(at.Y, radius, s.Y - radius));
        _plot.DrawCircle(at, radius, _ready ? UiTheme.Accent : UiTheme.Warn);
        _plot.DrawArc(at, radius, 0, Mathf.Tau, 24, Colors.White, 1.5f, true);
    }
}
