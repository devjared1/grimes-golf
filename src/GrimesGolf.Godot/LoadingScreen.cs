using Godot;

namespace GrimesGolf.Game;

/// <summary>
/// Shown over everything while a course loads (New Game, Resume Game): the logo, the course (with its logo when there
/// is one), what's being done, and a spinning ring. It takes all input while it's up and fades out when done.
/// </summary>
public partial class LoadingScreen : Control
{
    private Label _title = null!, _step = null!;
    private TextureRect _courseLogo = null!;
    private Spinner _spinner = null!;
    private Tween? _fade;

    public bool IsOpen => Visible;

    public override void _Ready()
    {
        Visible = false;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;

        var bg = new TextureRect
        {
            Texture = Background(), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var centre = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        centre.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(centre);
        var v = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        v.AddThemeConstantOverride("separation", 18);
        centre.AddChild(v);

        if (Brand.Logo is { } logo)
            v.AddChild(new TextureRect
            {
                Texture = logo, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = new Vector2(380, 300), SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
            });
        _courseLogo = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(220, 150), SizeFlagsHorizontal = SizeFlags.ShrinkCenter, Visible = false,
        };
        v.AddChild(_courseLogo);
        _title = UiTheme.Text("", 30, UiTheme.Ink, bold: true);
        _title.HorizontalAlignment = HorizontalAlignment.Center;
        v.AddChild(_title);

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 14);
        _spinner = new Spinner { CustomMinimumSize = new Vector2(30, 30), SizeFlagsVertical = SizeFlags.ShrinkCenter };
        row.AddChild(_spinner);
        _step = UiTheme.Text("", 18, UiTheme.Muted);
        row.AddChild(_step);
        v.AddChild(row);
    }

    /// <summary>Puts the screen up for <paramref name="title"/> (straight away, no fade in).</summary>
    public void Open(string title, Texture2D? courseLogo, string step)
    {
        _fade?.Kill();
        _title.Text = title;
        _courseLogo.Texture = courseLogo;
        _courseLogo.Visible = courseLogo != null;
        _step.Text = step;
        Modulate = Colors.White;
        Visible = true;
        MoveToFront();
    }

    public void SetStep(string step) => _step.Text = step;

    /// <summary>Fades the screen out (the first frames of the course show through as it goes).</summary>
    public void Close()
    {
        if (!Visible) return;
        _fade?.Kill();
        _fade = CreateTween();
        _fade.TweenProperty(this, "modulate:a", 0.0f, 0.45).SetDelay(0.15);
        _fade.TweenCallback(Callable.From(() => Visible = false));
    }

    private static Texture2D Background()
    {
        var g = new Gradient();
        g.SetColor(0, new Color(0.09f, 0.16f, 0.11f));
        g.SetColor(1, new Color(0.025f, 0.03f, 0.035f));
        return new GradientTexture2D
        {
            Gradient = g, Width = 256, Height = 256, Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.42f), FillTo = new Vector2(1.15f, 1.1f),
        };
    }

    private sealed partial class Spinner : Control
    {
        private float _angle;

        public override void _Process(double delta)
        {
            if (!IsVisibleInTree()) return;
            _angle = (_angle + (float)delta * 5.5f) % Mathf.Tau;
            QueueRedraw();
        }

        public override void _Draw()
        {
            var c = Size / 2;
            float r = Mathf.Min(Size.X, Size.Y) / 2 - 3;
            DrawArc(c, r, 0, Mathf.Tau, 40, new Color(1, 1, 1, 0.12f), 4, true);
            DrawArc(c, r, _angle, _angle + 1.9f, 24, UiTheme.Accent, 4, true);
        }
    }
}
