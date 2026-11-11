using Godot;
using GrimesGolf.Core;
using GrimesGolf.Core.Course;

namespace GrimesGolf.Game;

/// <summary>A course in the New Game list.</summary>
public sealed record CourseCard(string Name, string Description);

/// <summary>
/// The screen the app opens on: New Game (pick a course, the players and the Game Setup, <c>MainMenu.Setup.cs</c>, and
/// start a round), Resume Game (when a round is unfinished), Driving Range, History, Settings and Quit, with the launch monitor's
/// status and the version. The range is visible behind it. Works with a mouse, keyboard or gamepad (arrows / D-pad move,
/// Enter / A picks, Esc / B goes back from New Game).
/// </summary>
public partial class MainMenu : Control
{
    public event Action? ResumePressed, RangePressed, HistoryPressed, SettingsPressed, QuitPressed, MonitorPressed, OnlinePressed;
    /// <summary>Start a new round on this course, set up like this.</summary>
    public event Action<int, GameSetup>? StartCourse;
    /// <summary>The course list wants this course's stats (answer with <see cref="SetCourseStats"/>).</summary>
    public event Action<int>? CourseStatsWanted;
    public event Action? GetCoursesPressed;
    /// <summary>The player list was edited (it applies straight away).</summary>
    public event Action<IReadOnlyList<Golfer>>? PlayersEdited;
    /// <summary>Save was pressed on a row of the player list.</summary>
    public event Action<Golfer>? GuestSaved;

    private VBoxContainer _column = null!, _home = null!, _courses = null!, _courseList = null!;
    private ScrollContainer _courseScroll = null!;
    private Button _update = null!;
    private string? _updatePage;
    private Button _newGame = null!, _resume = null!, _start = null!, _online = null!;

    /// <summary>Shows Play Online (only in development builds until it has been tried between two houses).</summary>
    public void SetOnlineAvailable(bool available) => _online.Visible = available;
    private Label _resumeNote = null!, _monitorText = null!, _courseName = null!, _courseDescription = null!, _courseStats = null!;
    private PanelContainer _monitorCard = null!;
    private VBoxContainer _newGameRight = null!;
    private PlayerListEditor _playerList = null!;
    private readonly List<Button> _courseButtons = new();
    private readonly Dictionary<int, string> _stats = new();
    private IReadOnlyList<CourseCard> _cards = Array.Empty<CourseCard>();
    private int _selected;
    private bool _scrollingToCourse;

    public override void _Ready()
    {
        Visible = false;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;

        // Dark on the left, fading out over the range on the right.
        var shade = new TextureRect { Texture = Fade(), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(shade);

        var margin = new MarginContainer();
        margin.SetAnchorsAndOffsetsPreset(LayoutPreset.LeftWide);
        margin.OffsetRight = MenuWidth;
        margin.AddThemeConstantOverride("margin_left", 64);
        margin.AddThemeConstantOverride("margin_top", 36);
        margin.AddThemeConstantOverride("margin_bottom", 40);
        AddChild(margin);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 10);
        margin.AddChild(column);
        _column = column;
        column.MinimumSizeChanged += FitCourseList;
        Resized += FitCourseList;

        if (Brand.Logo is { } logo)
        {
            var logoRect = Brand.LogoRect(logo, new Vector2(440, 110), TextureRect.StretchModeEnum.KeepAspect);
            logoRect.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
            logoRect.TooltipText = Hud.Version;
            column.AddChild(logoRect);
        }
        else column.AddChild(UiTheme.Text($"{Brand.Name.ToUpperInvariant()} SIMULATOR", 44, UiTheme.Accent, bold: true));
        var version = UiTheme.Text(Hud.Version, 12, UiTheme.Muted);
        column.AddChild(version);
        _update = new Button { ThemeTypeVariation = "FlatButton", Visible = false, SizeFlagsHorizontal = SizeFlags.ShrinkBegin, TooltipText = "Open the download page" };
        _update.Pressed += () => { if (_updatePage != null) OS.ShellOpen(_updatePage); };
        column.AddChild(_update);
        column.AddChild(new Control { CustomMinimumSize = new Vector2(0, 10) });

        _home = new VBoxContainer();
        _home.AddThemeConstantOverride("separation", 10);
        column.AddChild(_home);
        _home.AddChild(UiTheme.Text("Main Menu", 38, UiTheme.Ink, bold: true));
        _home.AddChild(new ColorRect { Color = UiTheme.Accent, CustomMinimumSize = new Vector2(120, 3), SizeFlagsHorizontal = SizeFlags.ShrinkBegin });
        _home.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });
        _newGame = BigButton("New Game", "Pick a course and start a round", primary: true);
        _newGame.Pressed += ShowCourses;
        _home.AddChild(_newGame);
        _resume = BigButton("Resume Game", "Carry on with the unfinished round");
        _resume.Pressed += () => ResumePressed?.Invoke();
        _home.AddChild(_resume);
        _resumeNote = UiTheme.Text("", 14, UiTheme.Muted);
        _resumeNote.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _home.AddChild(_resumeNote);
        var range = BigButton("Driving Range", "Hit balls on the range");
        range.Pressed += () => RangePressed?.Invoke();
        _home.AddChild(range);
        _online = BigButton("Play Online", "Play a round with friends at their own simulators");
        _online.Pressed += () => OnlinePressed?.Invoke();
        _online.Visible = false;
        _home.AddChild(_online);
        // History and Settings share a row, so the menu still fits above the monitor card at the largest text size.
        var pair = new HBoxContainer();
        pair.AddThemeConstantOverride("separation", 10);
        var history = BigButton("History", "Finished rounds and practice, with averages per club");
        history.Pressed += () => HistoryPressed?.Invoke();
        var settings = BigButton("Settings", "Players, house rules, launch monitor, text size");
        settings.Pressed += () => SettingsPressed?.Invoke();
        foreach (var b in new[] { history, settings })
        {
            b.CustomMinimumSize = new Vector2(0, b.CustomMinimumSize.Y);
            b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            pair.AddChild(b);
        }
        _home.AddChild(pair);
        var quit = BigButton("Quit", $"Close {Brand.Name}");
        quit.Pressed += () => QuitPressed?.Invoke();
        _home.AddChild(quit);

        BuildCoursePage(column);

        column.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
        _monitorCard = new PanelContainer();
        var mv = new HBoxContainer();
        mv.AddThemeConstantOverride("separation", 12);
        var mt = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        mt.AddThemeConstantOverride("separation", 2);
        mt.AddChild(UiTheme.Caption("Launch monitor"));
        _monitorText = UiTheme.Text("Not connected", 18, UiTheme.Ink, bold: true);
        mt.AddChild(_monitorText);
        mv.AddChild(mt);
        var details = new Button { Text = "Connection…", ThemeTypeVariation = "FlatButton", SizeFlagsVertical = SizeFlags.ShrinkCenter, TooltipText = "Devices, connect / disconnect, battery, log  [L]" };
        details.Pressed += () => MonitorPressed?.Invoke();
        mv.AddChild(details);
        _monitorCard.AddChild(mv);
        column.AddChild(_monitorCard);
        SetMonitorStatus("Not connected", UiTheme.Muted);
        BuildNewGameRight();
    }

    /// <summary>New Game's right side, over the range: the player list and Game Setup under it (a share of the width, so large text leaves them room).</summary>
    private void BuildNewGameRight()
    {
        _newGameRight = new VBoxContainer { Visible = false };
        _newGameRight.AddThemeConstantOverride("separation", 10);
        _newGameRight.AnchorLeft = 0.5075f;
        _newGameRight.AnchorRight = 0.97f;
        _newGameRight.AnchorBottom = 1;
        _newGameRight.OffsetTop = 36;
        _newGameRight.OffsetBottom = -40;
        AddChild(_newGameRight);
        var playerPanel = new PanelContainer();
        playerPanel.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Card, 12, 1, 18, 14));
        _newGameRight.AddChild(playerPanel);
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 10);
        playerPanel.AddChild(v);
        var head = new HBoxContainer();
        var title = UiTheme.Text("Player List", 24, UiTheme.Ink, bold: true);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        title.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        head.AddChild(title);
        v.AddChild(head);
        _playerList = new PlayerListEditor { AddButtonHost = head };
        _playerList.SaveGuest += g => GuestSaved?.Invoke(g);
        _playerList.Changed += () => PlayersEdited?.Invoke(_playerList.Golfers);
        v.AddChild(_playerList);
        BuildSetupCard(_newGameRight);
    }

    private void BuildCoursePage(VBoxContainer column)
    {
        _courses = new VBoxContainer { Visible = false };
        _courses.AddThemeConstantOverride("separation", 10);
        column.AddChild(_courses);
        _courses.AddChild(UiTheme.Text("New Game", 26, UiTheme.Ink, bold: true));
        _courses.AddChild(UiTheme.Caption("Course"));
        // Scrolls once there are more courses than fit above the card.
        _courseScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true };
        _courseList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _courseList.AddThemeConstantOverride("separation", 6);
        _courseScroll.AddChild(_courseList);
        _courses.AddChild(_courseScroll);

        var card = new PanelContainer();
        card.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Card, 12, 1, 18));
        var cv = new VBoxContainer();
        cv.AddThemeConstantOverride("separation", 8);
        _courseName = UiTheme.Text("", 22, UiTheme.Ink, bold: true);
        cv.AddChild(_courseName);
        _courseStats = UiTheme.Text("", 17, UiTheme.Accent, bold: true);
        cv.AddChild(_courseStats);
        _courseDescription = UiTheme.Text("", 15, UiTheme.Muted);
        _courseDescription.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _courseDescription.CustomMinimumSize = new Vector2(420, 0);
        cv.AddChild(_courseDescription);
        card.AddChild(cv);
        _courses.AddChild(card);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 10);
        _start = new Button { Text = "Start Round", ThemeTypeVariation = "PrimaryButton", CustomMinimumSize = new Vector2(220, 54) };
        _start.AddThemeFontSizeOverride("font_size", 20);
        _start.Pressed += () => StartCourse?.Invoke(_selected, _setup);
        buttons.AddChild(_start);
        var more = new Button { Text = "Get More Courses", ThemeTypeVariation = "FlatButton", CustomMinimumSize = new Vector2(0, 54), TooltipText = "Download other courses" };
        more.Pressed += () => GetCoursesPressed?.Invoke();
        buttons.AddChild(more);
        buttons.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var back = new Button { Text = "Back", ThemeTypeVariation = "FlatButton", CustomMinimumSize = new Vector2(0, 54), TooltipText = "Back to the menu  [Esc, gamepad B]" };
        back.Pressed += ShowHome;
        buttons.AddChild(back);
        _courses.AddChild(buttons);
    }

    private static Button BigButton(string text, string tooltip, bool primary = false)
    {
        var b = new Button { Text = text, TooltipText = tooltip, CustomMinimumSize = new Vector2(380, 54), Alignment = HorizontalAlignment.Left,
                             ThemeTypeVariation = primary ? "PrimaryButton" : "" };
        b.AddThemeFontSizeOverride("font_size", 21);
        b.AddThemeFontOverride("font", UiTheme.Bold);
        return b;
    }

    /// <summary>A horizontal fade from nearly opaque on the left to clear on the right.</summary>
    private static Texture2D Fade()
    {
        var g = new Gradient();
        g.SetColor(0, new Color(0.04f, 0.05f, 0.06f, 0.96f));
        g.SetColor(1, new Color(0.04f, 0.05f, 0.06f, 0.0f));
        g.AddPoint(0.42f, new Color(0.04f, 0.05f, 0.06f, 0.88f));
        return new GradientTexture2D { Gradient = g, Width = 256, Height = 4, FillFrom = Vector2.Zero, FillTo = new Vector2(1, 0) };
    }

    public bool IsOpen => Visible;
    /// <summary>On New Game (Esc / B goes back from there, see <see cref="Back"/>).</summary>
    public bool OnSubPage => Visible && _courses.Visible;

    /// <summary>
    /// Shows the menu. <paramref name="resume"/> describes the unfinished round (null = none, Resume is hidden);
    /// <paramref name="golfers"/> is who a new round would start with, <paramref name="saved"/> who can be picked for
    /// it, <paramref name="home"/> the home player's name, and <paramref name="setup"/> the game setup New Game starts from.
    /// </summary>
    public void Open(string? resume, IReadOnlyList<CourseCard> courses, int selected, IReadOnlyList<Golfer> golfers, IReadOnlyList<Golfer> saved, string? home, GameSetup setup)
    {
        // What's known about each course goes by its place on the list, which downloading a course can change.
        if (!_cards.SequenceEqual(courses))
        {
            _stats.Clear();
            _holes.Clear();
            _altitudeFeet.Clear();
        }
        _cards = courses;
        SetPlayers(golfers, saved, home);
        _setup = setup;
        _selected = Math.Clamp(selected, 0, Math.Max(0, courses.Count - 1));
        _resume.Visible = resume != null;
        _resumeNote.Text = resume ?? "";
        _resumeNote.Visible = resume != null;
        Visible = true;
        ShowHome();
    }

    public void Close()
    {
        Visible = false;
        GetViewport().GuiReleaseFocus();
    }

    /// <summary>Updates the players and the player list (after the settings change).</summary>
    public void SetPlayers(IReadOnlyList<Golfer> golfers, IReadOnlyList<Golfer> saved, string? home)
    {
        _playerList.NotSavable = home;
        _playerList.SetSavedPlayers(saved);
        _playerList.Load(golfers);
        SetGolfers(golfers);
    }

    /// <summary>Who's playing, after the player list was edited here (its rows are left alone: someone may be typing in one).</summary>
    public void SetGolfers(IReadOnlyList<Golfer> golfers)
    {
        _golfers = golfers;
        if (OnSubPage) RefreshSetupNotes();
    }

    /// <summary>Who can be picked for the player list.</summary>
    public void SetSavedPlayers(IReadOnlyList<Golfer> saved) => _playerList.SetSavedPlayers(saved);

    /// <summary>A newer version of the app can be downloaded: a button for it under the version number.</summary>
    public void ShowUpdate(Releases.Update update)
    {
        _update.Text = $"Version {update.Version} is available";
        _update.Visible = true;
        _updatePage = update.Page;
    }

    /// <summary>Straight to New Game (back from Get More Courses, or to check how it looks).</summary>
    public void OpenCoursePage()
    {
        if (Visible) ShowCourses();
    }

    public void ShowHome()
    {
        _courses.Visible = false;
        _newGameRight.Visible = false;
        _home.Visible = true;
        (_resume.Visible ? _resume : _newGame).GrabFocus();
    }

    private void ShowCourses()
    {
        _home.Visible = false;
        _courses.Visible = true;
        _newGameRight.Visible = true;
        foreach (var sync in _syncRows) sync();
        RefreshSetupNotes();
        foreach (var c in _courseList.GetChildren()) c.Free();
        _courseButtons.Clear();
        var group = new ButtonGroup();
        for (int i = 0; i < _cards.Count; i++)
        {
            int index = i;
            var b = new Button { Text = _cards[i].Name, ToggleMode = true, ButtonGroup = group, ThemeTypeVariation = "TabButton",
                                 Alignment = HorizontalAlignment.Left, CustomMinimumSize = new Vector2(0, 48) };
            b.AddThemeFontSizeOverride("font_size", 19);
            b.Toggled += on => { if (on) SelectCourse(index); };
            _courseList.AddChild(b);
            _courseButtons.Add(b);
        }
        FitCourseList();
        if (_courseButtons.Count > 0) _courseButtons[_selected].ButtonPressed = true;
        SelectCourse(_selected);
        _start.Disabled = _cards.Count == 0;
        if (_cards.Count == 0)
        {
            _courseName.Text = "No courses found";
            _courseStats.Text = "";
            _courseDescription.Text = "The app's courses folder is missing or empty. Reinstalling will put it back, or get one with Get More Courses.";
            return;
        }
        _start.GrabFocus();
    }

    /// <summary>
    /// Room for up to four courses (48 high and 6 apart), less if that would push the launch monitor card off the screen
    /// (large text on a low window): the list scrolls instead, and always shows at least one course.
    /// </summary>
    private void FitCourseList()
    {
        if (_courseScroll == null || !_courses.Visible) return;
        const float margins = 36 + 40, row = 54;
        float rest = _column.GetCombinedMinimumSize().Y - _courseScroll.CustomMinimumSize.Y;
        float height = Mathf.Clamp(Size.Y - margins - rest, row, Math.Min(_cards.Count, 4) * row);
        if (_cards.Count == 0) height = 0;
        if (!Mathf.IsEqualApprox(height, _courseScroll.CustomMinimumSize.Y)) _courseScroll.CustomMinimumSize = new Vector2(0, height);
        ShowSelectedCourse();
    }

    /// <summary>Scrolls the list to the selected course, once the list's new size has been laid out.</summary>
    private async void ShowSelectedCourse()
    {
        // One scroll per frame: each is worked out from where the list was, so a second one would scroll it twice as far.
        if (_scrollingToCourse) return;
        _scrollingToCourse = true;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        _scrollingToCourse = false;
        if (_courses.Visible && _selected < _courseButtons.Count) _courseScroll.EnsureControlVisible(_courseButtons[_selected]);
    }

    private void SelectCourse(int index)
    {
        if (index >= _cards.Count) return;
        _selected = index;
        ShowSelectedCourse();
        _courseName.Text = _cards[index].Name;
        _courseDescription.Text = _cards[index].Description;
        _courseStats.Text = _stats.GetValueOrDefault(index, "Loading course…");
        if (!_stats.ContainsKey(index)) CourseStatsWanted?.Invoke(index);
        RefreshSetupNotes();
    }

    /// <summary>A course's stats line ("18 holes · Par 72 · 6,412 yd"), once it's loaded.</summary>
    public void SetCourseStats(int index, string stats)
    {
        _stats[index] = stats;
        if (index == _selected) _courseStats.Text = stats;
    }

    /// <summary>Monitor status as in the top bar ("Ready — hit!", "Not connected", ...) with its colour.</summary>
    public void SetMonitorStatus(string text, Color accent)
    {
        _monitorText.Text = text;
        var box = UiTheme.Box(UiTheme.Card, 12, 1, 16);
        box.BorderWidthLeft = 6;
        box.BorderColor = accent;
        _monitorCard.AddThemeStyleboxOverride("panel", box);
    }
}
