using Godot;

namespace SquareGspro.Range3D;

/// <summary>
/// A three-press swing meter, as in classic golf games. Press once to start the backswing: the marker leaves the
/// impact line and climbs, speeding up as it goes. Press again to set the power (or it stops at 100%), and the
/// marker swings back down, faster the more power was taken. Press a third time as it crosses the impact line:
/// on the line is perfectly timed, before it early, past it late. If it runs off the end of the timing zone the
/// swing is as late as it gets.
/// </summary>
public partial class SwingMeter : Control
{
    public enum Stage { Idle, Backswing, Downswing }

    /// <summary>The timing zone reaches this far either side of the impact line (as a share of full power).</summary>
    private const float Zone = 0.1f;
    /// <summary>Backswing speed (meter per second) at the impact line and its growth with height: 0→100% in about 1.15 s.</summary>
    private const float UpBase = 0.55f, UpGrowth = 0.75f;
    /// <summary>Downswing speed (meter per second) at no power and the extra at full power.</summary>
    private const float DownBase = 0.7f, DownPerPower = 1.0f;

    private float _pos, _power, _shownPower = -1, _shownAccuracy, _resultFade;
    private float? _target;
    private string _scale = "";

    public Stage Phase { get; private set; } = Stage.Idle;
    public bool Busy => Phase != Stage.Idle;

    /// <summary>Raised on the third press (or when the marker runs out of the timing zone): power 0-1, accuracy -1…1 (negative early).</summary>
    public event Action<float, float>? Finished;

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(440, 76);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    /// <summary>What the meter shows while idle and during the swing: where the shot calls for (0-1, null = none) and what 100% means.</summary>
    public void Prepare(float? target, string scale)
    {
        _target = target is { } t ? Mathf.Clamp(t, 0, 1) : null;
        _scale = scale;
        QueueRedraw();
    }

    /// <summary>Starts the swing, sets the power or strikes, depending on the stage.</summary>
    public void Press()
    {
        switch (Phase)
        {
            case Stage.Idle:
                Phase = Stage.Backswing;
                _pos = 0;
                _shownPower = -1;
                _resultFade = 0;
                break;
            case Stage.Backswing:
                _power = Mathf.Max(_pos, 0.01f);
                Phase = Stage.Downswing;
                break;
            case Stage.Downswing:
                Finish(Mathf.Clamp(-_pos / Zone, -1, 1));
                break;
        }
        QueueRedraw();
    }

    public void Cancel()
    {
        Phase = Stage.Idle;
        _pos = 0;
        QueueRedraw();
    }

    private void Finish(float accuracy)
    {
        Phase = Stage.Idle;
        _shownPower = _power;
        _shownAccuracy = accuracy;
        _resultFade = 1;
        Finished?.Invoke(_power, accuracy);
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        switch (Phase)
        {
            case Stage.Backswing:
                _pos += (UpBase + UpGrowth * _pos) * dt;
                if (_pos >= 1) { _pos = 1; _power = 1; Phase = Stage.Downswing; }
                QueueRedraw();
                break;
            case Stage.Downswing:
                _pos -= (DownBase + DownPerPower * _power) * dt;
                if (_pos <= -Zone) { _pos = -Zone; Finish(1); }
                QueueRedraw();
                break;
            default:
                if (_resultFade > 0) { _resultFade = Mathf.Max(0, _resultFade - dt / 4); QueueRedraw(); }
                break;
        }
    }

    /// <summary>Words for a timing (accuracy -1…1) and a colour for them.</summary>
    public static (string Text, Color Color) TimingWords(float accuracy, float perfect)
    {
        float a = Mathf.Abs(accuracy);
        if (a <= perfect) return ("Perfect", new Color(0.45f, 0.95f, 0.5f));
        string side = accuracy < 0 ? "Early" : "Late";
        return a < 0.6f ? ($"{side}", new Color(0.98f, 0.85f, 0.3f)) : ($"Very {side.ToLowerInvariant()}", new Color(0.98f, 0.45f, 0.35f));
    }

    public override void _Draw()
    {
        var font = GetThemeDefaultFont();
        var size = Size;
        const float barTop = 24, barHeight = 26;
        float left = 8, right = size.X - 8;
        // Meter position (−Zone … 1) to x.
        float X(float v) => left + (v + Zone) / (1 + Zone) * (right - left);
        var bar = new Rect2(left, barTop, right - left, barHeight);

        DrawRect(bar, new Color(0.06f, 0.08f, 0.09f, 0.9f));
        // Power scale: green to amber to red as the swing gets bigger.
        const int steps = 40;
        for (int i = 0; i < steps; i++)
        {
            float a = i / (float)steps, b = (i + 1) / (float)steps;
            var c = a < 0.6f ? new Color(0.2f, 0.55f, 0.3f).Lerp(new Color(0.75f, 0.65f, 0.2f), a / 0.6f)
                             : new Color(0.75f, 0.65f, 0.2f).Lerp(new Color(0.8f, 0.25f, 0.2f), (a - 0.6f) / 0.4f);
            DrawRect(new Rect2(X(a), barTop + 4, X(b) - X(a), barHeight - 8), new Color(c, 0.35f));
        }
        // The swing so far fills the scale.
        if (Phase == Stage.Backswing) DrawRect(new Rect2(X(0), barTop + 4, X(_pos) - X(0), barHeight - 8), new Color(1, 0.85f, 0.3f, 0.55f));
        if (Phase == Stage.Downswing || _shownPower >= 0)
        {
            float p = Phase == Stage.Downswing ? _power : _shownPower;
            DrawRect(new Rect2(X(0), barTop + 4, X(p) - X(0), barHeight - 8), new Color(1, 0.85f, 0.3f, Phase == Stage.Downswing ? 0.45f : 0.3f * _resultFade + 0.1f));
            DrawLine(new Vector2(X(p), barTop - 2), new Vector2(X(p), barTop + barHeight + 2), Colors.White, 2);
        }
        // Timing zone around the impact line, with its perfect band.
        DrawRect(new Rect2(X(-Zone), barTop, X(Zone) - X(-Zone), barHeight), new Color(0.35f, 0.6f, 0.95f, 0.35f));
        float perfect = Zone * (float)SquareGspro.Core.Course.Caddie.PerfectTiming;
        DrawRect(new Rect2(X(-perfect), barTop, X(perfect) - X(-perfect), barHeight), new Color(0.45f, 0.95f, 0.5f, 0.55f));
        DrawLine(new Vector2(X(0), barTop - 4), new Vector2(X(0), barTop + barHeight + 4), Colors.White, 2);

        // Ticks every 25%.
        foreach (float t in new[] { 0.25f, 0.5f, 0.75f, 1f })
        {
            DrawLine(new Vector2(X(t), barTop + barHeight - 6), new Vector2(X(t), barTop + barHeight), new Color(1, 1, 1, 0.5f), 1);
            DrawString(font, new Vector2(X(t) - 14, barTop + barHeight + 14), $"{t * 100:0}%", HorizontalAlignment.Center, 28, 11, new Color(1, 1, 1, 0.55f));
        }
        // Where this shot calls for.
        if (_target is { } target)
        {
            DrawLine(new Vector2(X(target), barTop - 6), new Vector2(X(target), barTop + barHeight + 6), new Color(0.4f, 0.9f, 1f), 2);
            DrawString(font, new Vector2(X(target) - 30, barTop + barHeight + 26), "target", HorizontalAlignment.Center, 60, 11, new Color(0.4f, 0.9f, 1f));
        }
        // The marker.
        if (Busy)
        {
            float x = X(_pos);
            DrawRect(new Rect2(x - 3, barTop - 6, 6, barHeight + 12), new Color(1, 0.95f, 0.6f));
        }

        // Caption: what to do next, or how the last swing went.
        string text;
        Color color = new(1, 1, 1, 0.85f);
        switch (Phase)
        {
            case Stage.Backswing: text = $"Power {_pos * 100:0}%   ·   Space to set power"; break;
            case Stage.Downswing: text = $"Power {_power * 100:0}%   ·   Space on the line to strike"; break;
            default:
                if (_resultFade > 0)
                {
                    var (words, c) = TimingWords(_shownAccuracy, (float)SquareGspro.Core.Course.Caddie.PerfectTiming);
                    text = $"Power {_shownPower * 100:0}%   ·   {words}";
                    color = c;
                }
                else text = "Space to swing";
                break;
        }
        DrawString(font, new Vector2(left, 16), text, HorizontalAlignment.Left, -1, 15, color);
        if (_scale.Length > 0) DrawString(font, new Vector2(left, 16), _scale, HorizontalAlignment.Right, right - left, 13, new Color(1, 1, 1, 0.6f));
    }
}
