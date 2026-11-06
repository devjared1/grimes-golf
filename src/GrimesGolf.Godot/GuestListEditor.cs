using Godot;
using GrimesGolf.Core.Course;

namespace GrimesGolf.Game;

/// <summary>
/// Editing the saved guests on the settings screen: a row each with their name, handedness and Delete, and "+ New guest".
/// </summary>
public partial class GuestListEditor : VBoxContainer
{
    /// <summary>A guest was added, changed or deleted.</summary>
    public event Action? Changed;

    private VBoxContainer _rows = null!;
    private Label _empty = null!;
    private Button _add = null!;
    private readonly List<(LineEdit Name, CheckBox LeftHanded)> _guests = new();

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 6);
        _empty = UiTheme.Text("None saved yet.", 14, UiTheme.Muted);
        AddChild(_empty);
        _rows = new VBoxContainer();
        _rows.AddThemeConstantOverride("separation", 6);
        AddChild(_rows);
        _add = new Button { Text = "+ New guest", ThemeTypeVariation = "FlatButton", SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
        _add.Pressed += () =>
        {
            AddRow(new Golfer(""));
            _guests[^1].Name.GrabFocus();
            _guests[^1].Name.Edit();
            Changed?.Invoke();
        };
        AddChild(_add);
    }

    /// <summary>The guests as typed (<c>AppSettings.WithGuests</c> drops blank and repeated names).</summary>
    public IReadOnlyList<Golfer> Golfers => _guests.Select(g => new Golfer(g.Name.Text.Trim(), g.LeftHanded.ButtonPressed)).ToList();

    public void Load(IReadOnlyList<Golfer> guests)
    {
        foreach (var c in _rows.GetChildren()) { _rows.RemoveChild(c); c.QueueFree(); }
        _guests.Clear();
        foreach (var g in guests) AddRow(g);
    }

    /// <summary>Adds a guest, or changes the hand of the one already here with that name.</summary>
    public void AddOrUpdate(Golfer guest)
    {
        int at = _guests.FindIndex(g => g.Name.Text.Trim().Equals(guest.Name, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) _guests[at].LeftHanded.ButtonPressed = guest.LeftHanded;
        else AddRow(guest);
        Changed?.Invoke();
    }

    private void AddRow(Golfer g)
    {
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 10);
        var name = new LineEdit { Text = g.Name, PlaceholderText = "Guest's name", MaxLength = 16, CustomMinimumSize = new Vector2(240, 38) };
        name.AddThemeFontSizeOverride("font_size", 17);
        name.TextChanged += _ => Changed?.Invoke();
        h.AddChild(name);
        var lh = new CheckBox { Text = "Left-handed", ButtonPressed = g.LeftHanded };
        h.AddChild(lh);
        h.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var delete = new Button { Text = "Delete", ThemeTypeVariation = "FlatButton" };
        var entry = (name, lh);
        lh.Toggled += _ => Changed?.Invoke();
        delete.Pressed += () =>
        {
            _guests.Remove(entry);
            _rows.RemoveChild(h);
            h.QueueFree();
            UpdateEmpty();
            _add.GrabFocus();
            Changed?.Invoke();
        };
        h.AddChild(delete);
        _rows.AddChild(h);
        _guests.Add(entry);
        UpdateEmpty();
    }

    private void UpdateEmpty() => _empty.Visible = _guests.Count == 0;
}
