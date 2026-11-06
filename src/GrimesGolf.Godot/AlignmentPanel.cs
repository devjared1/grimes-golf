using Godot;
using GrimesGolf.Core.Square;

namespace GrimesGolf.Game;

/// <summary>
/// Aligning the monitor with its white stick, over the game: the stick's angle against the target line as the monitor
/// reads it, live, and the choice to keep it or cancel. The monitor only takes angles within 5° of straight.
/// </summary>
public partial class AlignmentPanel : Control
{
    /// <summary>The monitor accepts this much either way (Square's software rounds 5.1° up to too far).</summary>
    public const double MaxAngle = 5.05;
    /// <summary>With no reading for this long, the panel says what to check (seconds).</summary>
    private const double NoReadingAfter = 6;

    public event Action<double>? Confirmed;
    public event Action? Cancelled;

    private Label _angle = null!, _message = null!;
    private Control _gauge = null!;
    private Button _use = null!, _cancel = null!;
    private double _sinceReading, _reading;
    private bool _haveReading;

    public bool IsOpen => Visible;

    /// <summary>The last steady angle read (0 if none).</summary>
    public double LastReading => _reading;

    /// <summary>"R1.2°", "L0.4°" or "0.0°".</summary>
    public static string AngleText(double angle) =>
        Math.Abs(angle) < 0.05 ? "0.0°" : $"{(angle < 0 ? "L" : "R")}{Math.Abs(angle):0.0}°";

    public override void _Ready()
    {
        Visible = false;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.55f), MouseFilter = MouseFilterEnum.Ignore };
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(dim);

        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(UiTheme.Bar, 1), 16, 1, 24));
        panel.SetAnchorsPreset(LayoutPreset.Center);
        panel.GrowHorizontal = GrowDirection.Both;
        panel.GrowVertical = GrowDirection.Both;
        AddChild(panel);

        var v = new VBoxContainer { CustomMinimumSize = new Vector2(460, 0) };
        v.AddThemeConstantOverride("separation", 12);
        panel.AddChild(v);
        v.AddChild(UiTheme.Text("Align with the stick", 24, UiTheme.Ink, bold: true));
        var how = UiTheme.Text("Lay the white stick on the mat in front of the monitor, pointing at your target. Keep it still until the angle settles.", 15, UiTheme.Muted);
        how.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        v.AddChild(how);

        _gauge = new Control { CustomMinimumSize = new Vector2(0, 220), MouseFilter = MouseFilterEnum.Ignore };
        _gauge.Draw += DrawGauge;
        v.AddChild(_gauge);

        _angle = UiTheme.Text("—", 40, UiTheme.Ink, bold: true);
        _angle.HorizontalAlignment = HorizontalAlignment.Center;
        v.AddChild(_angle);
        _message = UiTheme.Text("", 15, UiTheme.Warn);
        _message.HorizontalAlignment = HorizontalAlignment.Center;
        _message.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _message.CustomMinimumSize = new Vector2(0, 44);
        v.AddChild(_message);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 10);
        _use = new Button { Text = "Use This Alignment", ThemeTypeVariation = "PrimaryButton", SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 50), Disabled = true };
        _use.AddThemeFontSizeOverride("font_size", 18);
        _use.Pressed += () => { double angle = _reading; Close(); Confirmed?.Invoke(angle); };
        _cancel = new Button { Text = "Cancel", SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 50), TooltipText = "Keep the alignment as it was  [Esc]" };
        _cancel.AddThemeFontSizeOverride("font_size", 18);
        _cancel.Pressed += Cancel;
        buttons.AddChild(_use);
        buttons.AddChild(_cancel);
        v.AddChild(buttons);
        SetProcess(false);
    }

    public void Open()
    {
        _haveReading = false;
        _sinceReading = 0;
        _reading = 0;
        _angle.Text = "—";
        _angle.RemoveThemeColorOverride("font_color");
        _message.Text = "Waiting for the monitor…";
        _use.Disabled = true;
        Visible = true;
        SetProcess(true);
        _cancel.GrabFocus();
        _gauge.QueueRedraw();
    }

    /// <summary>Closes without telling anyone (the monitor went away, or a choice was made).</summary>
    public void Close()
    {
        if (!Visible) return;
        Visible = false;
        SetProcess(false);
        GetViewport().GuiReleaseFocus();
    }

    /// <summary>As if Cancel was pressed: the alignment stays as it was.</summary>
    public void Cancel()
    {
        if (!Visible) return;
        Close();
        Cancelled?.Invoke();
    }

    /// <summary>A new reading from the monitor.</summary>
    public void ShowReading(StickEvent stick)
    {
        if (stick.Status == StickStatus.Finished) return;
        _sinceReading = 0;
        bool stable = stick.Status == StickStatus.Stable;
        bool inRange = Math.Abs(stick.AngleDeg) <= MaxAngle;
        _haveReading = stable;
        if (stable) _reading = stick.AngleDeg;
        _angle.Text = stable ? AngleText(stick.AngleDeg) : "—";
        _angle.AddThemeColorOverride("font_color", stable && !inRange ? UiTheme.Warn : UiTheme.Accent);
        _message.Text = !stable ? "Looking for the stick. Keep it still."
                      : !inRange ? "The stick has to point within 5° of straight ahead of the monitor."
                      : "";
        bool wasDisabled = _use.Disabled;
        _use.Disabled = !stable || !inRange;
        if (wasDisabled && !_use.Disabled) _use.GrabFocus();
        _gauge.QueueRedraw();
    }

    public override void _Process(double delta)
    {
        if ((_sinceReading += delta) < NoReadingAfter) return;
        _haveReading = false;
        _angle.Text = "—";
        _use.Disabled = true;
        _message.Text = "Nothing from the monitor yet. Check that the stick is in its view, and that nothing else is on the mat.";
        _gauge.QueueRedraw();
    }

    /// <summary>Seen from above: the monitor at the bottom, the target line straight up, the stick at its angle (exaggerated, so a small angle shows).</summary>
    private void DrawGauge()
    {
        const float Exaggerate = 3f;
        var size = _gauge.Size;
        var bottom = new Vector2(size.X / 2, size.Y - 26);
        float reach = size.Y - 44;

        _gauge.DrawRect(new Rect2(bottom + new Vector2(-46, 4), new Vector2(92, 18)), UiTheme.Control);
        var limit = Vector2.Up.Rotated(Mathf.DegToRad((float)(MaxAngle * Exaggerate))) * reach;
        var side = new Color(UiTheme.Muted, 0.35f);
        _gauge.DrawLine(bottom, bottom + limit, side, 1.5f, true);
        _gauge.DrawLine(bottom, bottom + new Vector2(-limit.X, limit.Y), side, 1.5f, true);
        _gauge.DrawDashedLine(bottom, bottom + Vector2.Up * reach, new Color(UiTheme.Ink, 0.7f), 2f, 8f);
        if (!_haveReading) return;

        bool inRange = Math.Abs(_reading) <= MaxAngle;
        float shown = Mathf.Clamp((float)_reading, -8f, 8f) * Exaggerate;
        _gauge.DrawLine(bottom, bottom + Vector2.Up.Rotated(Mathf.DegToRad(shown)) * reach, inRange ? UiTheme.Accent : UiTheme.Warn, 5f, true);
    }
}
