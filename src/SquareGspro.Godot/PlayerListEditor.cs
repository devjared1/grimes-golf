using Godot;
using SquareGspro.Core.Course;

namespace SquareGspro.Range3D;

/// <summary>
/// Editing the players (1-4): a row each with their colour, name, handedness and Remove, and "+ Add player". Used by
/// the settings screen and by Add / Remove Players during a round. Each row remembers which of the players it was
/// loaded from (<see cref="Origins"/>), so a round can carry their scores over.
/// </summary>
public partial class PlayerListEditor : VBoxContainer
{
    private VBoxContainer _rows = null!;
    private Button _add = null!;
    private readonly List<(LineEdit Name, CheckBox LeftHanded, int? Origin)> _players = new();

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 6);
        _rows = new VBoxContainer();
        _rows.AddThemeConstantOverride("separation", 6);
        AddChild(_rows);
        _add = new Button { Text = "+ Add player", ThemeTypeVariation = "FlatButton", SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
        _add.Pressed += () => { AddRow(new Golfer(""), null); UpdateButtons(); _players[^1].Name.GrabFocus(); };
        AddChild(_add);
    }

    /// <summary>The players as typed (blank names are filled in by whoever uses them).</summary>
    public IReadOnlyList<Golfer> Golfers => _players.Select(p => new Golfer(p.Name.Text.Trim(), p.LeftHanded.ButtonPressed)).ToList();

    /// <summary>For each row, the index of the player it was loaded as (null = added since).</summary>
    public IReadOnlyList<int?> Origins => _players.Select(p => p.Origin).ToList();

    public Button AddButton => _add;

    public void Load(IReadOnlyList<Golfer> golfers) => Rebuild(golfers.Select((g, i) => (g, (int?)i)).ToList());

    private void Rebuild(IReadOnlyList<(Golfer Golfer, int? Origin)> players)
    {
        foreach (var c in _rows.GetChildren()) c.Free();
        _players.Clear();
        foreach (var (g, origin) in players) AddRow(g, origin);
        UpdateButtons();
    }

    private void AddRow(Golfer g, int? origin)
    {
        int index = _players.Count;
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 10);
        var dot = UiTheme.Text("●", 20, UiTheme.PlayerColor(index));
        dot.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        h.AddChild(dot);
        var name = new LineEdit { Text = g.Name, PlaceholderText = $"Player {index + 1}", MaxLength = 16, CustomMinimumSize = new Vector2(240, 38) };
        name.AddThemeFontSizeOverride("font_size", 17);
        h.AddChild(name);
        var lh = new CheckBox { Text = "Left-handed", ButtonPressed = g.LeftHanded };
        h.AddChild(lh);
        h.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var remove = new Button { Text = "Remove", ThemeTypeVariation = "FlatButton" };
        var entry = (name, lh, origin);
        remove.Pressed += () =>
        {
            int i = _players.IndexOf(entry);
            if (i < 0 || _players.Count <= 1) return;
            // Rebuild so the colours stay in player order.
            Rebuild(_players.Where((_, k) => k != i).Select(p => (new Golfer(p.Name.Text, p.LeftHanded.ButtonPressed), p.Origin)).ToList());
            _add.GrabFocus();
        };
        h.AddChild(remove);
        _rows.AddChild(h);
        _players.Add(entry);
    }

    private void UpdateButtons()
    {
        _add.Disabled = _players.Count >= Match.MaxPlayers;
        foreach (var row in _rows.GetChildren().OfType<HBoxContainer>())
            row.GetChildren().OfType<Button>().Last().Disabled = _players.Count <= 1;
    }
}
