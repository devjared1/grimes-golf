using Godot;

namespace GrimesGolf.Game;

/// <summary>
/// The frame rate, in the top-left corner of the view when Settings → Display has it turned on: for finding the graphics
/// quality a device can keep up with. While it shows, the average and the slowest second go into Godot's log every
/// half minute too, so the numbers come back with a log sent in.
/// </summary>
public partial class FrameRateMeter : Label
{
    private const double LogEvery = 30;

    /// <summary>The graphics quality in use, shown beside the number and written with it.</summary>
    public string Quality { get; set; } = "";

    private double _second, _logged, _sum;
    private int _frames, _seconds, _slowest = int.MaxValue;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        Position = new Vector2(12, 72);   // just under the top bar, over the 3D view
        AddThemeFontSizeOverride("font_size", 16);
        AddThemeColorOverride("font_color", Colors.White);
        AddThemeColorOverride("font_outline_color", Colors.Black);
        AddThemeConstantOverride("outline_size", 4);
    }

    /// <summary>One line about the device and how it's drawing, for the top of a log sent in.</summary>
    public static string DeviceLine(Viewport viewport) =>
        $"Device: {OS.GetName()} {OS.GetVersion()}, {OS.GetModelName()}; {RenderingServer.GetVideoAdapterName()}; " +
        $"renderer {ProjectSettings.GetSetting("rendering/renderer/rendering_method")} " +
        $"({RenderingServer.GetCurrentRenderingMethod()}/{RenderingServer.GetCurrentRenderingDriverName()}); " +
        $"screen {DisplayServer.ScreenGetSize().X}x{DisplayServer.ScreenGetSize().Y}, window {viewport.GetWindow().Size.X}x{viewport.GetWindow().Size.Y}";

    public override void _Process(double delta)
    {
        if (!Visible) { Reset(); return; }
        _frames++;
        if ((_second += delta) < 1) return;

        int fps = (int)Math.Round(_frames / _second);
        Text = $"{fps} fps · {Quality}";
        _sum += fps;
        _seconds++;
        _slowest = Math.Min(_slowest, fps);
        _logged += _second;
        _second = 0;
        _frames = 0;
        if (_logged < LogEvery) return;
        GD.Print($"{DateTime.Now:HH:mm:ss}  Frame rate ({Quality}): average {_sum / _seconds:F0} fps, slowest second {_slowest} fps");
        Reset();
    }

    private void Reset()
    {
        _second = _logged = _sum = 0;
        _frames = _seconds = 0;
        _slowest = int.MaxValue;
    }
}
