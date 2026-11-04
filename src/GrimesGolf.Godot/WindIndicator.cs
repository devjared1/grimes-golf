using Godot;

namespace GrimesGolf.Game;

/// <summary>
/// The wind for the shot coming up: a dial with an arrow showing which way it blows across the screen (up = along the
/// aim, so an arrow pointing down is into the player's face), its speed and how it plays ("into, from the left").
/// </summary>
public partial class WindIndicator : PanelContainer
{
    private Dial _dial = null!;
    private Label _speed = null!, _note = null!;

    public override void _Ready()
    {
        AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Card, 12, 1, 8, 12));
        MouseFilter = MouseFilterEnum.Ignore;
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 10);
        _dial = new Dial { CustomMinimumSize = new Vector2(44, 44) };
        h.AddChild(_dial);
        var v = new VBoxContainer { SizeFlagsVertical = SizeFlags.ShrinkCenter };
        v.AddThemeConstantOverride("separation", 0);
        v.AddChild(UiTheme.Caption("Wind"));
        _speed = UiTheme.Text("", 20, UiTheme.Ink, bold: true);
        v.AddChild(_speed);
        _note = UiTheme.Text("", 12, UiTheme.Muted);
        v.AddChild(_note);
        h.AddChild(v);
        AddChild(h);
    }

    /// <summary>
    /// <paramref name="blowsToward"/>: the direction the wind blows relative to the aim (radians, 0 = straight
    /// down the aim line, positive = to the right).
    /// </summary>
    public void Show(double speedMph, double gustMph, double blowsToward, string note)
    {
        _speed.Text = gustMph >= 1 ? $"{speedMph:0} mph  (gusts {speedMph + gustMph:0})" : $"{speedMph:0} mph";
        _note.Text = note;
        _note.Visible = note.Length > 0;
        _dial.Angle = (float)blowsToward;
        _dial.Strength = Mathf.Clamp((float)speedMph / 20f, 0.25f, 1f);
        _dial.QueueRedraw();
    }

    private sealed partial class Dial : Control
    {
        public float Angle, Strength = 0.5f;

        public override void _Draw()
        {
            var c = Size / 2;
            float r = Mathf.Min(Size.X, Size.Y) / 2 - 1;
            DrawCircle(c, r, new Color(0, 0, 0, 0.3f));
            DrawArc(c, r, 0, Mathf.Tau, 32, new Color(1, 1, 1, 0.18f), 1.5f, true);
            // Up the screen is along the aim.
            var dir = new Vector2(Mathf.Sin(Angle), -Mathf.Cos(Angle));
            var side = new Vector2(-dir.Y, dir.X);
            float len = r * (0.45f + 0.4f * Strength);
            var tip = c + dir * len;
            var tail = c - dir * len;
            var color = Strength > 0.7f ? UiTheme.Warn : new Color(0.55f, 0.82f, 1f);
            DrawLine(tail, tip - dir * 5, color, 3, true);
            DrawColoredPolygon(new[] { tip, tip - dir * 9 + side * 6, tip - dir * 9 - side * 6 }, color);
        }
    }
}
