using Godot;

namespace GrimesGolf.Game;

/// <summary>
/// Drawn over the overview map: a ring on the ball (in the player's colour), a dashed line from it to where the shot
/// is aimed (along the aim, as far as the power set carries it, so it swings with the aim and stretches with the
/// power), a target ring there with the shot's distance, and the flag with its distance. Points are in this
/// control's pixels.
/// </summary>
public partial class MapOverlay : Control
{
    private Vector2? _ball, _aim, _pin;
    private string _aimText = "", _pinText = "";
    private Color _color = Colors.White;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    /// <summary>The ball, aim point and flag (any may be null: no ball hides everything; no aim, no line; no flag on the range).</summary>
    public void Show(Vector2? ball, Vector2? aim, Vector2? pin, string aimText, string pinText, Color color)
    {
        _ball = ball;
        _aim = aim;
        _pin = pin;
        _aimText = aimText;
        _pinText = pinText;
        _color = color;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_ball is not { } ball) return;
        var font = GetThemeDefaultFont();
        Rect2? flagArea = null;
        if (_pin is { } pin)
        {
            DrawLine(pin, pin + new Vector2(0, -20), Colors.White, 2);
            DrawColoredPolygon(new[] { pin + new Vector2(0, -20), pin + new Vector2(13, -15.5f), pin + new Vector2(0, -11) }, new Color(0.95f, 0.2f, 0.2f));
            DrawCircle(pin, 3, Colors.White);
            var area = new Rect2(pin + new Vector2(-3, -20), new Vector2(16, 23));
            if (_pinText.Length > 0)
            {
                var pinSize = font.GetStringSize(_pinText, HorizontalAlignment.Left, -1, 14);
                var pinAt = InMap(pin + new Vector2(16, -22), pinSize);
                Tag(font, pinAt, _pinText, 14, new Color(0.95f, 0.2f, 0.2f));
                area = area.Merge(TagBox(pinAt, pinSize));
            }
            flagArea = area;
        }
        if (_aim is { } aim)
        {
            var dir = aim - ball;
            float length = dir.Length();
            if (length > 1)
            {
                dir /= length;
                for (float d = 9; d < length - 8; d += 12)
                    DrawLine(ball + dir * d, ball + dir * Mathf.Min(d + 7, length - 8), new Color(1, 1, 1, 0.95f), 2.5f, true);
            }
            DrawArc(aim, 7, 0, Mathf.Tau, 24, new Color(0, 0, 0, 0.6f), 4.5f, true);
            DrawArc(aim, 7, 0, Mathf.Tau, 24, new Color(1, 0.9f, 0.3f), 2.5f, true);
            if (_aimText.Length > 0)
            {
                // Beside the target ring, on whichever side is clear of the line and of the flag and its distance.
                var size = font.GetStringSize(_aimText, HorizontalAlignment.Left, -1, 17);
                var side = length > 1 ? new Vector2(-dir.Y, dir.X) : Vector2.Right;
                if (side.X < 0) side = -side;
                Tag(font, ClearSpot(aim, side, size, flagArea), _aimText, 17, Colors.White);
            }
        }
        DrawArc(ball, 7, 0, Mathf.Tau, 24, new Color(0, 0, 0, 0.6f), 4.5f, true);
        DrawArc(ball, 7, 0, Mathf.Tau, 24, _color, 2.5f, true);
        DrawCircle(ball, 2.5f, Colors.White);
    }

    /// <summary>
    /// Where the aim's distance goes: to one side of the target ring, then the other, then above or below it, or failing
    /// those, under the flag's distance (a short shot's target can sit right on the flag).
    /// </summary>
    private Vector2 ClearSpot(Vector2 aim, Vector2 side, Vector2 size, Rect2? flagArea)
    {
        Vector2[] spots =
        {
            aim + side * (size.X / 2 + 16) - size / 2,
            aim - side * (size.X / 2 + 16) - size / 2,
            aim + new Vector2(-size.X / 2, -size.Y - 12),
            aim + new Vector2(-size.X / 2, 12),
        };
        if (flagArea is not { } taken) return InMap(spots[0], size);
        foreach (var spot in spots)
        {
            var at = InMap(spot, size);
            if (!TagBox(at, size).Intersects(taken)) return at;
        }
        return InMap(new Vector2(taken.Position.X + 6, taken.End.Y + 4), size);
    }

    private Vector2 InMap(Vector2 at, Vector2 size) =>
        new(Mathf.Clamp(at.X, 6, Size.X - size.X - 6), Mathf.Clamp(at.Y, 4, Size.Y - size.Y - 4));

    /// <summary>The dark box behind a tag's text.</summary>
    private static Rect2 TagBox(Vector2 at, Vector2 size) => new(at - new Vector2(6, 2), size + new Vector2(12, 4));

    /// <summary>Text on a dark rounded box, kept inside the map.</summary>
    private void Tag(Font font, Vector2 at, string text, int size, Color color)
    {
        var s = font.GetStringSize(text, HorizontalAlignment.Left, -1, size);
        at = InMap(at, s);
        DrawStyleBox(UiTheme.Box(new Color(0.05f, 0.06f, 0.07f, 0.85f), 6, 0, 0), TagBox(at, s));
        DrawString(font, at + new Vector2(0, font.GetAscent(size)), text, HorizontalAlignment.Left, -1, size, color);
    }
}
