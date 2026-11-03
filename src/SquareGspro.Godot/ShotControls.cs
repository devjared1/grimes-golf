using Godot;
using SquareGspro.Core.Course;

namespace SquareGspro.Range3D;

/// <summary>
/// Hitting a shot from the screen (no launch monitor needed): a power slider (1-100%, with what it means in yards or
/// feet beside it), shot shape (draw, straight, fade), spin (low, medium, high), a Ready / Not ready light and the
/// HIT SHOT button. On a course the power is set for the shot each time a new one comes up; it can be changed after.
/// Shape and spin are greyed out for putts.
/// </summary>
public partial class ShotControls : VBoxContainer
{
    public event Action? HitPressed;
    /// <summary>The player changed the power (the slider or ↑ / ↓), not <see cref="SetPower"/>.</summary>
    public event Action? PowerChanged;

    private HSlider _power = null!;
    private Label _powerValue = null!, _readyText = null!;
    private PanelContainer _readyLight = null!;
    private readonly Button[] _shape = new Button[3], _spin = new Button[3];
    private HBoxContainer _shapeRow = null!, _spinRow = null!;
    private bool? _ready;
    private string _readyShown = "";

    public Button Hit { get; private set; } = null!;

    /// <summary>What a power (0.01-1) means for this club and shot, e.g. "118 yd carry"; shown beside the slider.</summary>
    public Func<double, string>? Describe { get; set; }

    public double Power => _power.Value / 100;
    public ShotShape Shape => (ShotShape)Array.FindIndex(_shape, b => b.ButtonPressed);
    public SpinLevel Spin => (SpinLevel)Array.FindIndex(_spin, b => b.ButtonPressed);
    public Swing Swing => new(Power, Shape, Spin);

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 6);

        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 8);
        var caption = UiTheme.Caption("Power");
        caption.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        head.AddChild(caption);
        _powerValue = UiTheme.Text("", 15, UiTheme.Ink, bold: true);
        _powerValue.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _powerValue.HorizontalAlignment = HorizontalAlignment.Right;
        head.AddChild(_powerValue);
        AddChild(head);

        _power = new HSlider
        {
            MinValue = 1, MaxValue = 100, Step = 1, Value = 100, CustomMinimumSize = new Vector2(280, 26),
            FocusMode = FocusModeEnum.None,   // ←/→ aim, so the slider mustn't take them
            TooltipText = "Shot power: share of the club's full carry (putter: of the roll shown)  [↑ / ↓, Shift = 5%]",
        };
        _power.ValueChanged += _ => { ShowPower(); PowerChanged?.Invoke(); };
        AddChild(_power);

        _shapeRow = Segmented("Shape", new[] { "Draw", "Straight", "Fade" }, _shape, 1,
            new[] { "Starts right, curves left (right-hander)", "Straight at the aim", "Starts left, curves right (right-hander)" });
        AddChild(_shapeRow);
        _spinRow = Segmented("Spin", new[] { "Low", "Medium", "High" }, _spin, 1,
            new[] { "Lower flight, less backspin, more roll", "The club's normal spin", "Higher flight, more backspin, stops quicker" });
        AddChild(_spinRow);
        ShowPower();
    }

    /// <summary>The Ready light and HIT SHOT button, laid out by whoever places this (they sit beside the controls).</summary>
    public Control BuildHitColumn(string tooltip)
    {
        var v = new VBoxContainer { SizeFlagsVertical = SizeFlags.ShrinkCenter };
        v.AddThemeConstantOverride("separation", 8);
        _readyLight = new PanelContainer();
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        _readyText = UiTheme.Text("", 15, Colors.White, bold: true);
        row.AddChild(_readyText);
        _readyLight.AddChild(row);
        v.AddChild(_readyLight);
        Hit = new Button { Text = "HIT SHOT", ThemeTypeVariation = "PrimaryButton", CustomMinimumSize = new Vector2(180, 60), TooltipText = tooltip };
        Hit.AddThemeFontSizeOverride("font_size", 20);
        Hit.Pressed += () => HitPressed?.Invoke();
        v.AddChild(Hit);
        SetReady(false, "Not ready");
        return v;
    }

    private HBoxContainer Segmented(string caption, string[] labels, Button[] buttons, int selected, string[] tips)
    {
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 4);
        var cap = UiTheme.Caption(caption);
        cap.CustomMinimumSize = new Vector2(46, 0);
        cap.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        h.AddChild(cap);
        var group = new ButtonGroup();
        for (int i = 0; i < labels.Length; i++)
        {
            buttons[i] = new Button
            {
                Text = labels[i], ToggleMode = true, ButtonGroup = group, ButtonPressed = i == selected, ThemeTypeVariation = "TabButton",
                SizeFlagsHorizontal = SizeFlags.ExpandFill, FocusMode = FocusModeEnum.None, TooltipText = tips[i],
            };
            h.AddChild(buttons[i]);
        }
        return h;
    }

    public void SetPower(double power)
    {
        _power.SetValueNoSignal(Math.Round(Math.Clamp(power, 0.01, 1) * 100));
        ShowPower();
    }

    /// <summary>Power up or down by <paramref name="percent"/> points.</summary>
    public void NudgePower(int percent)
    {
        SetPower(Power + percent / 100.0);
        PowerChanged?.Invoke();
    }

    /// <summary>Back to a straight, medium-spin shot (after every shot, so a draw or fade is chosen on purpose each time).</summary>
    public void ResetShape()
    {
        _shape[(int)ShotShape.Straight].ButtonPressed = true;
        _spin[(int)SpinLevel.Medium].ButtonPressed = true;
    }

    /// <summary>Refreshes the text beside the slider (the club or shot changed what a power means).</summary>
    public void ShowPower()
    {
        string what = Describe?.Invoke(Power) ?? "";
        _powerValue.Text = what.Length > 0 ? $"{_power.Value:0}%  ·  {what}" : $"{_power.Value:0}%";
    }

    /// <summary>Putts: shape and spin don't apply.</summary>
    public void SetPutting(bool putting)
    {
        foreach (var b in _shape.Concat(_spin)) b.Disabled = putting;
        _shapeRow.Modulate = _spinRow.Modulate = new Color(1, 1, 1, putting ? 0.45f : 1);
    }

    /// <summary>The Ready / Not ready light: green when a shot can be hit now, with why not otherwise.</summary>
    public void SetReady(bool ready, string text)
    {
        if (_ready == ready && _readyShown == text) return;
        _ready = ready;
        _readyShown = text;
        _readyText.Text = text.ToUpperInvariant();
        _readyText.AddThemeColorOverride("font_color", ready ? new Color(0.03f, 0.08f, 0.05f) : UiTheme.Ink);
        _readyLight.AddThemeStyleboxOverride("panel", UiTheme.Box(ready ? UiTheme.Accent : new Color(0.55f, 0.22f, 0.2f, 0.9f), 8, 0, 4, 10));
    }
}
