using Godot;

namespace SquareGspro.Range3D;

/// <summary>
/// A golf ball with a dot for where the club will strike it. Click or drag to move the dot, right-click to centre it.
/// Below centre launches higher with more backspin, above lower with less; left or right of centre curves the ball that
/// way. Off-centre strikes lose a little speed. Dimmed (and ignored) for putts.
/// </summary>
public partial class StrikePad : Control
{
    private const float Radius = 30;
    /// <summary>The dot can go this far out (share of the ball's radius), so it stays on the ball.</summary>
    private const float Reach = 0.8f;

    private Vector2 _strike;   // x right, y up, within the unit circle

    /// <summary>Strike point, right of centre (−1…1).</summary>
    public float StrikeX => _strike.X;
    /// <summary>Strike point, above centre (−1…1).</summary>
    public float StrikeY => _strike.Y;

    private bool _enabled = true;
    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; QueueRedraw(); }
    }

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(84, 84);
        MouseFilter = MouseFilterEnum.Stop;
        TooltipText = "Strike point: click or drag to move, right-click to centre.\n"
                      + "Below centre: higher, more backspin. Above: lower, less spin.\nLeft / right of centre: curves left / right.";
    }

    public void Reset()
    {
        _strike = Vector2.Zero;
        QueueRedraw();
    }

    private Vector2 Centre => new(Size.X / 2, Size.Y / 2 - 6);

    public override void _GuiInput(InputEvent e)
    {
        if (!_enabled) return;
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true }) { Reset(); AcceptEvent(); return; }
        bool drag = e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }
                    || e is InputEventMouseMotion m && m.ButtonMask.HasFlag(MouseButtonMask.Left);
        if (!drag || e is not InputEventMouse mouse) return;
        var offset = (mouse.Position - Centre) / (Radius * Reach);
        offset.Y = -offset.Y;
        _strike = offset.LengthSquared() > 1 ? offset.Normalized() : offset;
        QueueRedraw();
        AcceptEvent();
    }

    public override void _Draw()
    {
        var c = Centre;
        float alpha = _enabled ? 1 : 0.35f;
        DrawCircle(c, Radius + 1.5f, new Color(0, 0, 0, 0.5f * alpha));
        DrawCircle(c, Radius, new Color(0.95f, 0.95f, 0.93f, alpha));
        // A few dimples and a shaded lower edge so it reads as a ball.
        for (int i = 0; i < 18; i++)
        {
            float a = i * 2.39996f, r = Radius * 0.82f * Mathf.Sqrt((i + 0.5f) / 18f);
            DrawCircle(c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r, 2.2f, new Color(0.8f, 0.8f, 0.78f, alpha));
        }
        DrawArc(c, Radius - 2, 0.2f, Mathf.Pi - 0.2f, 24, new Color(0.7f, 0.7f, 0.68f, alpha), 3);
        // Crosshair for the centre.
        var line = new Color(0.3f, 0.35f, 0.4f, 0.5f * alpha);
        DrawLine(c - new Vector2(Radius * 0.9f, 0), c + new Vector2(Radius * 0.9f, 0), line, 1);
        DrawLine(c - new Vector2(0, Radius * 0.9f), c + new Vector2(0, Radius * 0.9f), line, 1);
        // The strike dot.
        var dot = c + new Vector2(_strike.X, -_strike.Y) * Radius * Reach;
        DrawCircle(dot, 5, new Color(0.85f, 0.12f, 0.12f, alpha));
        DrawArc(dot, 5, 0, Mathf.Tau, 16, new Color(1, 1, 1, 0.9f * alpha), 1.2f);

        var font = GetThemeDefaultFont();
        DrawString(font, new Vector2(0, Size.Y - 2), _enabled ? "STRIKE" : "STRIKE (putt)", HorizontalAlignment.Center, Size.X, 11,
                   new Color(1, 1, 1, 0.6f));
    }
}
