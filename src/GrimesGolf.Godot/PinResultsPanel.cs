using Godot;

namespace GrimesGolf.Game;

/// <summary>One line of Closest to the Pin's standings: place, player, each shot's distance (or why it didn't count) and the best one.</summary>
public sealed record PinRow(string Place, string Name, Color Color, IReadOnlyList<string> Shots, string Best, bool Leader);

/// <summary>
/// Closest to the Pin's standings over the game: the players best first, with each shot's distance across the row, and
/// buttons under it (Play Again and Main Menu at the end; just Close in the middle of a game). Works with a mouse,
/// keyboard or gamepad like <see cref="MenuOverlay"/>, and closes the same way (Esc / B).
/// </summary>
public partial class PinResultsPanel : Control
{
    private Label _title = null!, _message = null!;
    private GridContainer _table = null!;
    private HBoxContainer _buttons = null!;

    public bool IsOpen => Visible;

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
        _title = UiTheme.Text("", 28, UiTheme.Ink, bold: true);
        v.AddChild(_title);
        _message = UiTheme.Text("", 17, UiTheme.Muted);
        _message.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _message.CustomMinimumSize = new Vector2(520, 0);
        v.AddChild(_message);
        _table = new GridContainer();
        _table.AddThemeConstantOverride("h_separation", 22);
        _table.AddThemeConstantOverride("v_separation", 8);
        v.AddChild(_table);
        _buttons = new HBoxContainer();
        _buttons.AddThemeConstantOverride("separation", 10);
        v.AddChild(_buttons);
    }

    public void Open(string title, string message, IReadOnlyList<PinRow> rows, IReadOnlyList<MenuItem> items)
    {
        _title.Text = title;
        _message.Text = message;
        _message.Visible = message.Length > 0;
        foreach (var c in _table.GetChildren()) { _table.RemoveChild(c); c.QueueFree(); }
        int shots = rows.Count == 0 ? 0 : rows.Max(r => r.Shots.Count);
        _table.Columns = 3 + shots;
        _table.AddChild(UiTheme.Caption(""));
        _table.AddChild(UiTheme.Caption("Player"));
        for (int i = 0; i < shots; i++) _table.AddChild(Right(UiTheme.Caption($"Shot {i + 1}")));
        _table.AddChild(Right(UiTheme.Caption("Closest")));
        foreach (var r in rows)
        {
            _table.AddChild(UiTheme.Text(r.Place, 20, r.Leader ? UiTheme.Accent : UiTheme.Muted, bold: true));
            var name = UiTheme.Text(r.Name, 20, r.Color, bold: r.Leader);
            name.CustomMinimumSize = new Vector2(150, 0);
            _table.AddChild(name);
            for (int i = 0; i < shots; i++)
                _table.AddChild(Right(UiTheme.Text(i < r.Shots.Count ? r.Shots[i] : "", 17, UiTheme.Muted)));
            _table.AddChild(Right(UiTheme.Text(r.Best, 20, r.Leader ? UiTheme.Accent : UiTheme.Ink, bold: true)));
        }
        foreach (var c in _buttons.GetChildren()) { _buttons.RemoveChild(c); c.QueueFree(); }
        Button? first = null;
        foreach (var item in items)
        {
            var b = new Button
            {
                Text = item.Label, Disabled = !item.Enabled, TooltipText = item.Tooltip,
                ThemeTypeVariation = item.Primary ? "PrimaryButton" : "", CustomMinimumSize = new Vector2(220, 50),
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

    private static Label Right(Label l)
    {
        l.HorizontalAlignment = HorizontalAlignment.Right;
        return l;
    }

    public void Close()
    {
        if (!Visible) return;
        Visible = false;
        GetViewport().GuiReleaseFocus();
    }
}
