using Godot;
using SquareGspro.Core.Course;

namespace SquareGspro.Range3D;

/// <summary>
/// A small side-on picture of the ball in its lie: teed up, sitting on short grass, down in the rough or deep rough,
/// settled in sand, on the green, in water or on a cart path. The ground tilts with the slope along the shot (uphill
/// rises to the right, the way the shot goes), and a little level shows the ball-above/below-feet slope.
/// </summary>
public partial class LieView : Control
{
    private Lie _lie = Lie.Tee;
    private float _uphill, _aboveFeet;

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(220, 104);
        MouseFilter = MouseFilterEnum.Ignore;
        ClipContents = true;
    }

    /// <summary>The lie and slopes in degrees (uphill along the aim; ball above the feet).</summary>
    public void Show(Lie lie, double uphill, double aboveFeet)
    {
        _lie = lie;
        _uphill = (float)uphill;
        _aboveFeet = (float)aboveFeet;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var size = Size;
        var rect = new Rect2(Vector2.Zero, size);
        DrawRect(rect, new Color(0.36f, 0.5f, 0.66f));                                       // sky
        DrawRect(new Rect2(0, 0, size.X, size.Y * 0.45f), new Color(0.46f, 0.62f, 0.8f));

        // Ground line through the middle, tilted by the slope (exaggerated so a few degrees show).
        float tilt = Mathf.DegToRad(Mathf.Clamp(_uphill * 2.5f, -30, 30));
        var centre = new Vector2(size.X * 0.5f, size.Y * 0.66f);
        var along = new Vector2(Mathf.Cos(tilt), -Mathf.Sin(tilt));
        var down = new Vector2(-along.Y, along.X);
        Vector2 At(float x, float depth) => centre + along * x + down * depth;
        float half = size.X;

        var (ground, top) = _lie switch
        {
            Lie.Bunker => (new Color(0.86f, 0.77f, 0.55f), new Color(0.93f, 0.85f, 0.64f)),
            Lie.Water => (new Color(0.2f, 0.36f, 0.6f), new Color(0.32f, 0.52f, 0.8f)),
            Lie.CartPath => (new Color(0.45f, 0.45f, 0.44f), new Color(0.62f, 0.62f, 0.6f)),
            Lie.Green => (new Color(0.24f, 0.5f, 0.2f), new Color(0.42f, 0.75f, 0.34f)),
            Lie.DeepRough => (new Color(0.2f, 0.34f, 0.12f), new Color(0.3f, 0.46f, 0.18f)),
            Lie.Rough => (new Color(0.22f, 0.4f, 0.15f), new Color(0.34f, 0.55f, 0.24f)),
            _ => (new Color(0.24f, 0.46f, 0.18f), new Color(0.38f, 0.66f, 0.28f)),
        };
        DrawColoredPolygon(new[] { At(-half, 0), At(half, 0), At(half, size.Y * 2), At(-half, size.Y * 2) }, ground);
        DrawLine(At(-half, 0), At(half, 0), top, 3);

        const float r = 11;
        // How far the ball's centre sits above the ground line (negative = below, nestled down).
        float lift = _lie switch
        {
            Lie.Tee => r + 9, Lie.Fairway or Lie.Fringe or Lie.Green or Lie.CartPath => r, Lie.Rough => r * 0.45f,
            Lie.DeepRough => -r * 0.1f, Lie.Bunker => r * 0.35f, Lie.Water => -r * 0.6f, _ => r,
        };
        var ball = At(0, -lift);

        if (_lie == Lie.Tee)
        {
            DrawLine(At(0, 0), At(0, -lift + r), new Color(0.95f, 0.85f, 0.4f), 4);         // tee peg
        }
        if (_lie == Lie.Bunker)
        {
            // Sand pushed up around the ball.
            DrawColoredPolygon(new[] { At(-r * 2.2f, 0), At(-r * 0.6f, -r * 0.5f), At(r * 0.6f, -r * 0.5f), At(r * 2.2f, 0) }, top);
        }

        bool behind = _lie != Lie.Water;
        if (behind) Blades(At, back: true);
        DrawCircle(ball + new Vector2(1.5f, 1.5f), r, new Color(0, 0, 0, 0.25f));
        DrawCircle(ball, r, new Color(0.97f, 0.97f, 0.95f));
        for (int i = 0; i < 7; i++)
        {
            float a = i * 2.39996f, d = r * 0.65f * Mathf.Sqrt((i + 0.5f) / 7f);
            DrawCircle(ball + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * d, 1.4f, new Color(0.82f, 0.82f, 0.8f));
        }
        if (behind) Blades(At, back: false);
        if (_lie == Lie.Water)
            DrawColoredPolygon(new[] { At(-half, 0), At(half, 0), At(half, size.Y * 2), At(-half, size.Y * 2) }, new Color(0.2f, 0.4f, 0.7f, 0.55f));

        // Ball above / below the feet: a small level bubble in the corner.
        float side = Mathf.Clamp(_aboveFeet / 8f, -1, 1);
        var lv = new Rect2(size.X - 58, 8, 50, 12);
        DrawRect(lv, new Color(0, 0, 0, 0.35f));
        DrawLine(new Vector2(lv.GetCenter().X, lv.Position.Y), new Vector2(lv.GetCenter().X, lv.End.Y), new Color(1, 1, 1, 0.5f), 1);
        DrawCircle(new Vector2(lv.GetCenter().X + side * (lv.Size.X / 2 - 6), lv.GetCenter().Y), 5, new Color(0.7f, 0.95f, 0.5f));
    }

    /// <summary>Grass in front of (or behind) the ball: taller and thicker the worse the lie.</summary>
    private void Blades(Func<float, float, Vector2> at, bool back)
    {
        var (height, count, color) = _lie switch
        {
            Lie.DeepRough => (30f, 34, new Color(0.3f, 0.5f, 0.16f)),
            Lie.Rough => (17f, 30, new Color(0.36f, 0.58f, 0.22f)),
            Lie.Fairway or Lie.Tee => (5f, 40, new Color(0.42f, 0.7f, 0.3f)),
            Lie.Fringe => (7f, 40, new Color(0.44f, 0.72f, 0.32f)),
            Lie.Green => (2.5f, 50, new Color(0.48f, 0.8f, 0.38f)),
            _ => (0f, 0, Colors.Transparent),
        };
        if (count == 0) return;
        if (back) color = color.Darkened(0.25f);
        float width = Size.X * 1.1f;
        var rng = new RandomNumberGenerator { Seed = back ? 7UL : 11UL };
        for (int i = 0; i < count; i++)
        {
            float x = -width / 2 + width * (i + rng.Randf() * 0.8f) / count;
            float h = height * (0.6f + rng.Randf() * 0.6f) * (back ? 1.1f : 0.85f);
            float lean = (rng.Randf() - 0.5f) * height * 0.5f;
            DrawLine(at(x, 0), at(x + lean, -h), color, back ? 2 : 1.6f);
        }
    }
}
