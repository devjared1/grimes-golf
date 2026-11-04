using Godot;

namespace SquareGspro.Range3D;

/// <summary>One choice in a <see cref="MenuOverlay"/>. A null <see cref="Action"/> just closes the overlay.</summary>
public sealed record MenuItem(string Label, Action? Action, string Tooltip = "", bool Enabled = true, bool Primary = false);

/// <summary>
/// A modal panel over the game with a title, an optional message and big buttons: the in-game menu and "are you
/// sure?" questions. Works with a mouse, keyboard or gamepad (the first button has focus; Esc / B closes it).
/// Choosing a button closes the panel, then runs its action (which may open another one).
/// </summary>
public partial class MenuOverlay : Control
{
    private Label _title = null!, _message = null!;
    private GridContainer _buttons = null!;

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

        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 14);
        panel.AddChild(v);
        var head = new HBoxContainer();
        _title = UiTheme.Text("", 24, UiTheme.Ink, bold: true);
        _title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        head.AddChild(_title);
        var close = new Button { Text = "✕", ThemeTypeVariation = "FlatButton", TooltipText = "Close  [Esc, gamepad B]", FocusMode = FocusModeEnum.None };
        close.Pressed += Close;
        head.AddChild(close);
        v.AddChild(head);
        _message = UiTheme.Text("", 16, UiTheme.Muted);
        _message.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _message.CustomMinimumSize = new Vector2(460, 0);
        v.AddChild(_message);
        _buttons = new GridContainer();
        _buttons.AddThemeConstantOverride("h_separation", 10);
        _buttons.AddThemeConstantOverride("v_separation", 10);
        v.AddChild(_buttons);
    }

    public bool IsOpen => Visible;

    public void Open(string title, string message, IReadOnlyList<MenuItem> items, int columns = 1)
    {
        _title.Text = title;
        _message.Text = message;
        _message.Visible = message.Length > 0;
        foreach (var c in _buttons.GetChildren()) c.Free();
        _buttons.Columns = Math.Max(1, columns);
        Button? first = null;
        foreach (var item in items)
        {
            var b = new Button
            {
                Text = item.Label, Disabled = !item.Enabled, TooltipText = item.Tooltip,
                ThemeTypeVariation = item.Primary ? "PrimaryButton" : "", CustomMinimumSize = new Vector2(columns > 1 ? 260 : 360, 50),
            };
            b.AddThemeFontSizeOverride("font_size", 18);
            var action = item.Action;
            b.Pressed += () => { Close(); action?.Invoke(); };
            _buttons.AddChild(b);
            if (first == null && item.Enabled) first = b;
        }
        Visible = true;
        first?.GrabFocus();
    }

    public void Close()
    {
        if (!Visible) return;
        Visible = false;
        GetViewport().GuiReleaseFocus();
    }
}
