using Godot;

namespace SquareGspro.Range3D;

/// <summary>
/// The score on a hole announced across the upper part of the screen when a player holes out ("BIRDIE!"): the letters
/// drop in one after another with a bounce, hold, then rise and fade away. Under par gets a burst of sparks behind it.
/// Drawn by hand (no layout), so it never moves anything else; it takes no input.
/// </summary>
public partial class ScoreBurst : Control
{
    private const double In = 0.5, Stagger = 0.045, Hold = 1.9, Out = 0.5;

    private string _text = "", _sub = "";
    private Color _color = Colors.White;
    private bool _sparks;
    private double _t = -1;
    private readonly List<(float Angle, float Speed, float Size)> _sparkSeeds = new();
    private readonly Random _rng = new();

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
    }

    /// <summary>Plays the announcement: <paramref name="text"/> big, <paramref name="sub"/> (the player, the strokes) small under it.</summary>
    public void Play(string text, string sub, Color color, bool sparks)
    {
        _text = text;
        _sub = sub;
        _color = color;
        _sparks = sparks;
        _sparkSeeds.Clear();
        if (sparks)
            for (int i = 0; i < 36; i++)
                _sparkSeeds.Add(((float)(_rng.NextDouble() * Mathf.Tau), 260 + (float)_rng.NextDouble() * 380, 2 + (float)_rng.NextDouble() * 4));
        _t = 0;
        Visible = true;
        MoveToFront();
    }

    private double Total => In + Stagger * _text.Length + Hold + Out;

    public override void _Process(double delta)
    {
        if (_t < 0) return;
        _t += delta;
        if (_t > Total) { _t = -1; Visible = false; return; }
        QueueRedraw();
    }

    private static float EaseOutBack(float x)
    {
        const float c1 = 1.70158f, c3 = c1 + 1;
        return 1 + c3 * Mathf.Pow(x - 1, 3) + c1 * Mathf.Pow(x - 1, 2);
    }

    public override void _Draw()
    {
        if (_t < 0) return;
        var font = UiTheme.Bold;
        float t = (float)_t;
        float outStart = (float)(Total - Out);
        float fade = t < outStart ? 1 : 1 - (t - outStart) / (float)Out;
        float rise = t < outStart ? 0 : (t - outStart) / (float)Out * 60;
        // In the upper third: the ball (and the cup it just went into) is in the middle of the view.
        var centre = new Vector2(Size.X / 2, Size.Y * 0.3f - rise);
        int size = _text.Length > 10 ? 76 : 96;

        if (_sparks)
        {
            float k = Mathf.Clamp(t / 1.1f, 0, 1);
            foreach (var (angle, speed, dot) in _sparkSeeds)
            {
                var p = centre + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle) * 0.6f) * speed * (1 - (1 - k) * (1 - k));
                DrawCircle(p, dot * (1 - k * 0.6f), new Color(_color.Lerp(Colors.White, 0.4f), (1 - k) * fade));
            }
        }

        // A soft dark band behind the text so it reads over the sky or the grass.
        float textWidth = font.GetStringSize(_text, HorizontalAlignment.Left, -1, size).X;
        float band = Mathf.Clamp(t / 0.25f, 0, 1) * fade;
        var bandRect = new Rect2(centre.X - textWidth / 2 - 60, centre.Y - size * 0.95f, textWidth + 120, size * (_sub.Length > 0 ? 2.3f : 1.75f));
        DrawStyleBox(UiTheme.Box(new Color(0.03f, 0.04f, 0.05f, 0.55f * band), 24, 0, 0), bandRect);

        // Letters drop in one by one with a bounce.
        float x = centre.X - textWidth / 2;
        float baseline = centre.Y + font.GetAscent(size) / 2 - size * 0.12f;
        for (int i = 0; i < _text.Length; i++)
        {
            string ch = _text[i].ToString();
            float w = font.GetStringSize(ch, HorizontalAlignment.Left, -1, size).X;
            float local = Mathf.Clamp((t - (float)(Stagger * i)) / (float)In, 0, 1);
            if (local > 0)
            {
                float pop = EaseOutBack(local);
                float drop = (1 - pop) * -90;
                float scale = 0.4f + 0.6f * pop;
                var at = new Vector2(x + w / 2, baseline + drop);
                DrawSetTransform(at, 0, new Vector2(scale, scale));
                var colour = new Color(_color, Mathf.Min(1, local * 2) * fade);
                DrawStringOutline(font, new Vector2(-w / 2, 0), ch, HorizontalAlignment.Left, -1, size, 14, new Color(0, 0, 0, 0.7f * colour.A));
                DrawString(font, new Vector2(-w / 2, 0), ch, HorizontalAlignment.Left, -1, size, colour);
                DrawSetTransform(Vector2.Zero, 0, Vector2.One);
            }
            x += w;
        }

        if (_sub.Length > 0)
        {
            float subIn = Mathf.Clamp((t - (float)(In + Stagger * _text.Length) * 0.7f) / 0.3f, 0, 1) * fade;
            const int subSize = 28;
            float sw = font.GetStringSize(_sub, HorizontalAlignment.Left, -1, subSize).X;
            var at = new Vector2(centre.X - sw / 2, baseline + size * 0.55f);
            DrawStringOutline(font, at, _sub, HorizontalAlignment.Left, -1, subSize, 8, new Color(0, 0, 0, 0.7f * subIn));
            DrawString(font, at, _sub, HorizontalAlignment.Left, -1, subSize, new Color(UiTheme.Ink, subIn));
        }
    }
}
