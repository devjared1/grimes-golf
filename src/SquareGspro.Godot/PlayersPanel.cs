using Godot;
using SquareGspro.Core.Course;

namespace SquareGspro.Range3D;

/// <summary>
/// Add / Remove Players during a round: the round's players, editable. Players kept carry on with their scores and
/// ball; anyone added starts on the current hole's tee (earlier holes don't count for them). Esc / B closes it
/// without changes.
/// </summary>
public partial class PlayersPanel : Control
{
    /// <summary>Saved: the players, and for each the index of the player they were in the round (null = joining).</summary>
    public event Action<IReadOnlyList<Golfer>, IReadOnlyList<int?>>? Applied;

    private PlayerListEditor _players = null!;
    private Button _save = null!;

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

        var v = new VBoxContainer { CustomMinimumSize = new Vector2(600, 0) };
        v.AddThemeConstantOverride("separation", 12);
        panel.AddChild(v);
        v.AddChild(UiTheme.Text("Players in this round", 24, UiTheme.Ink, bold: true));
        _players = new PlayerListEditor();
        v.AddChild(_players);
        var note = UiTheme.Text("Players you add start on this hole's tee; earlier holes don't count for them. Removed players' scores are dropped from this round.", 14, UiTheme.Muted);
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        v.AddChild(note);
        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 10);
        _save = new Button { Text = "Save", ThemeTypeVariation = "PrimaryButton", CustomMinimumSize = new Vector2(120, 44) };
        _save.Pressed += () => { Close(); Applied?.Invoke(_players.Golfers, _players.Origins); };
        var cancel = new Button { Text = "Cancel", ThemeTypeVariation = "FlatButton", CustomMinimumSize = new Vector2(0, 44) };
        cancel.Pressed += Close;
        buttons.AddChild(_save);
        buttons.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        buttons.AddChild(cancel);
        v.AddChild(buttons);
    }

    public bool IsOpen => Visible;

    public void Open(IReadOnlyList<Golfer> players)
    {
        _players.Load(players);
        Visible = true;
        _save.GrabFocus();
    }

    public void Close()
    {
        if (!Visible) return;
        Visible = false;
        GetViewport().GuiReleaseFocus();
    }
}
