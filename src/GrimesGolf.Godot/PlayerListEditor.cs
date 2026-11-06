using Godot;
using GrimesGolf.Core.Course;

namespace GrimesGolf.Game;

/// <summary>
/// Editing the players (1-4): a row each with their colour, name, handedness and Remove, "+ Add player" and a pick from
/// the saved players. Used by the settings screen, New Game's player list and Add / Remove Players during a round. Each
/// row remembers which of the players it was loaded from (<see cref="Origins"/>), so a round can carry their scores over.
/// </summary>
public partial class PlayerListEditor : VBoxContainer
{
    /// <summary>A row's Save was pressed, with the player as typed. A subscriber puts a Save button on each row (see <see cref="NotSavable"/>).</summary>
    public event Action<Golfer>? SaveGuest;
    /// <summary>The name that gets no Save button: the home player, who's saved already.</summary>
    public string? NotSavable { get; set; }
    /// <summary>Anything about the players was changed here (not by <see cref="Load"/>).</summary>
    public event Action? Changed;
    /// <summary>
    /// Set before it's added: one Add button in this container instead of the two under the list. It picks from the saved
    /// players (or "New player"), or adds a blank row straight away when there's nobody to pick.
    /// </summary>
    public Container? AddButtonHost { get; init; }

    /// <summary>The pick list's "New player" (saved players' ids are their place on the list).</summary>
    private const int NewPlayerId = 1000;

    private VBoxContainer _rows = null!;
    private Button _add = null!;
    private MenuButton _pick = null!;
    private IReadOnlyList<Golfer> _saved = Array.Empty<Golfer>();
    /// <summary>What the pick list is offering (its item ids index this).</summary>
    private List<Golfer> _offered = new();
    private readonly List<(LineEdit Name, CheckBox LeftHanded, int? Origin)> _players = new();

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 6);
        _rows = new VBoxContainer();
        _rows.AddThemeConstantOverride("separation", 6);
        AddChild(_rows);
        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 10);
        _add = new Button { Text = "+ Add player", ThemeTypeVariation = "FlatButton", SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
        _add.Pressed += AddBlankPlayer;
        buttons.AddChild(_add);
        _pick = new MenuButton { Text = "+ Saved player ▾", ThemeTypeVariation = "FlatButton", SwitchOnHover = false, FocusMode = FocusModeEnum.All,
                                 TooltipText = "Add your home player or a saved guest" };
        _pick.AboutToPopup += FillPickList;
        _pick.GetPopup().IdPressed += id =>
        {
            if (_players.Count >= Match.MaxPlayers) return;
            if (id == NewPlayerId) { AddBlankPlayer(); return; }
            if (id < 0 || id >= _offered.Count) return;
            AddRow(_offered[(int)id], null);
            UpdateButtons();
            FocusAdd();
            Changed?.Invoke();
        };
        buttons.AddChild(_pick);
        AddChild(buttons);
        if (AddButtonHost != null)
        {
            RemoveChild(buttons);
            foreach (var b in new Button[] { _add, _pick })
            {
                buttons.RemoveChild(b);
                b.ThemeTypeVariation = "PrimaryButton";
                b.CustomMinimumSize = new Vector2(180, 40);
                b.SizeFlagsVertical = SizeFlags.ShrinkCenter;
                AddButtonHost.AddChild(b);
            }
            buttons.QueueFree();
            _add.Text = "Add";
            _pick.Text = "Add ▾";
            _pick.Flat = false;   // (a menu button draws no background otherwise)
        }
    }

    private void AddBlankPlayer()
    {
        AddRow(new Golfer(""), null);
        UpdateButtons();
        _players[^1].Name.GrabFocus();
        _players[^1].Name.Edit();
        Changed?.Invoke();
    }

    private void FocusAdd() => (_add.Visible ? _add : _pick).GrabFocus();

    /// <summary>The saved players not on the list already (as it's typed right now).</summary>
    private List<Golfer> Offered() =>
        _saved.Where(g => !_players.Any(p => p.Name.Text.Trim().Equals(g.Name.Trim(), StringComparison.OrdinalIgnoreCase))).ToList();

    /// <summary>Who can be picked rather than typed: the home player and the saved guests.</summary>
    public void SetSavedPlayers(IReadOnlyList<Golfer> saved)
    {
        _saved = saved.Where(g => !string.IsNullOrWhiteSpace(g.Name)).ToList();
        UpdateButtons();
    }

    private void FillPickList()
    {
        _offered = Offered();
        var popup = _pick.GetPopup();
        popup.Clear();
        for (int i = 0; i < _offered.Count; i++) popup.AddItem(_offered[i].Name + (_offered[i].LeftHanded ? " (LH)" : ""), i);
        if (AddButtonHost != null)
        {
            if (_offered.Count > 0) popup.AddSeparator();
            popup.AddItem("New player", NewPlayerId);
        }
        else if (_offered.Count == 0)
        {
            popup.AddItem("Everyone saved is already playing", _offered.Count);
            popup.SetItemDisabled(0, true);
        }
    }

    /// <summary>The players as typed (blank names are filled in by whoever uses them).</summary>
    public IReadOnlyList<Golfer> Golfers => _players.Select(p => new Golfer(p.Name.Text.Trim(), p.LeftHanded.ButtonPressed)).ToList();

    /// <summary>For each row, the index of the player it was loaded as (null = added since).</summary>
    public IReadOnlyList<int?> Origins => _players.Select(p => p.Origin).ToList();

    public Button AddButton => _add;

    public void Load(IReadOnlyList<Golfer> golfers) => Rebuild(golfers.Select((g, i) => (g, (int?)i)).ToList());

    private void Rebuild(IReadOnlyList<(Golfer Golfer, int? Origin)> players)
    {
        // A row's Remove button rebuilds the list from its own Pressed signal, so it can't be freed right now.
        foreach (var c in _rows.GetChildren()) { _rows.RemoveChild(c); c.QueueFree(); }
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
        name.TextChanged += _ => { UpdateButtons(); Changed?.Invoke(); };
        h.AddChild(name);
        var lh = new CheckBox { Text = "Left-handed", ButtonPressed = g.LeftHanded };
        lh.Toggled += _ => Changed?.Invoke();
        h.AddChild(lh);
        h.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        if (SaveGuest != null && !g.Name.Trim().Equals(NotSavable, StringComparison.OrdinalIgnoreCase))
        {
            var save = new Button { Text = "Save", ThemeTypeVariation = "FlatButton", TooltipText = "Keep this player under Saved guests (name and hand)" };
            save.Pressed += () =>
            {
                if (name.Text.Trim().Length > 0) SaveGuest?.Invoke(new Golfer(name.Text.Trim(), lh.ButtonPressed));
            };
            h.AddChild(save);
        }
        var remove = new Button { Text = "Remove", ThemeTypeVariation = "FlatButton" };
        var entry = (name, lh, origin);
        remove.Pressed += () =>
        {
            int i = _players.IndexOf(entry);
            if (i < 0 || _players.Count <= 1) return;
            // Rebuild so the colours stay in player order.
            Rebuild(_players.Where((_, k) => k != i).Select(p => (new Golfer(p.Name.Text, p.LeftHanded.ButtonPressed), p.Origin)).ToList());
            FocusAdd();
            Changed?.Invoke();
        };
        h.AddChild(remove);
        _rows.AddChild(h);
        _players.Add(entry);
    }

    private void UpdateButtons()
    {
        _add.Disabled = _players.Count >= Match.MaxPlayers;
        _pick.Disabled = _players.Count >= Match.MaxPlayers;
        // One Add button when it's the only one: the list if there's anyone to pick, otherwise straight to a new row.
        _pick.Visible = AddButtonHost == null ? _saved.Count > 0 : Offered().Count > 0;
        _add.Visible = AddButtonHost == null || !_pick.Visible;
        foreach (var row in _rows.GetChildren().OfType<HBoxContainer>())
            row.GetChildren().OfType<Button>().Last().Disabled = _players.Count <= 1;
    }
}
