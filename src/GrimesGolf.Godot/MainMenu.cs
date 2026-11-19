using Godot;
using GrimesGolf.Core;
using GrimesGolf.Core.Course;

namespace GrimesGolf.Game;

/// <summary>A course in the New Round list.</summary>
public sealed record CourseCard(string Name, string Description);

/// <summary>
/// The screen the app opens on: a list of buttons down the left, for Resume Round (when a round is unfinished), New Round (pick a course,
/// the players and the Game Setup, <c>MainMenu.Setup.cs</c>, and start a round), Practice (the Practice Options page:
/// the Range, the Putting Green and the Chipping Green), Online Round, History and Settings; Quit at the bottom left and
/// the launch monitor's status at the bottom right, with the version under the logo. The range is visible behind it.
/// Works with a mouse, keyboard or gamepad (arrows / D-pad move, Enter / A picks, Esc / B goes back from a page).
/// </summary>
public partial class MainMenu : Control
{
    public event Action? ResumePressed, RangePressed, HistoryPressed, SettingsPressed, QuitPressed, MonitorPressed, OnlinePressed;
    /// <summary>A practice green was picked on Practice Options.</summary>
    public event Action<PracticeKind>? PracticePressed;
    /// <summary>Start a new round on this course, set up like this.</summary>
    public event Action<int, GameSetup>? StartCourse;
    /// <summary>The course list wants this course's stats (answer with <see cref="SetCourseStats"/>).</summary>
    public event Action<int>? CourseStatsWanted;
    public event Action? GetCoursesPressed;
    /// <summary>The player list was edited (it applies straight away).</summary>
    public event Action<IReadOnlyList<Golfer>>? PlayersEdited;
    /// <summary>Save was pressed on a row of the player list.</summary>
    public event Action<Golfer>? GuestSaved;

    // The pages: the home page (the cards) and Practice Options share one layout (_homePage); New Round has its own (_columnMargin
    // with the course list, and _newGameRight). The logo block and the monitor card are moved to whichever page is showing.
    private VBoxContainer _column = null!, _courses = null!, _courseList = null!, _header = null!;
    private MarginContainer _columnMargin = null!, _homePage = null!;
    private VBoxContainer _homeTop = null!, _homeBody = null!, _practiceBody = null!;
    private HBoxContainer _homeBottom = null!;
    private Control _cardStash = null!;
    private TextureRect _shade = null!;
    private GlyphButton _quit = null!, _back = null!;
    private ScrollContainer _courseScroll = null!;
    private Button _update = null!, _start = null!;
    private string? _updatePage;
    private MenuCard _newGame = null!, _resume = null!, _online = null!, _practice = null!, _range = null!;
    private readonly List<MenuCard> _homeCards = new();

    /// <summary>Shows Online Round (only in development builds until it has been tried between two houses).</summary>
    public void SetOnlineAvailable(bool available)
    {
        _online.Visible = available;
        ArrangeCards(_homeBody, _homeCards);
    }
    private Label _monitorText = null!, _courseName = null!, _courseDescription = null!, _courseStats = null!;
    private PanelContainer _monitorCard = null!, _monitorDot = null!;
    private Button _monitorDetails = null!;
    private bool _monitorConnected;
    private Color _monitorAccent = UiTheme.Muted;
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
        _shade = new TextureRect { Texture = Fade(), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore };
        _shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_shade);
        // Buttons that aren't shown (no round to resume, no online play) wait here.
        _cardStash = new Control { Visible = false };
        AddChild(_cardStash);

        var margin = new MarginContainer();
        margin.SetAnchorsAndOffsetsPreset(LayoutPreset.LeftWide);
        margin.OffsetRight = MenuWidth;
        margin.AddThemeConstantOverride("margin_left", 64);
        margin.AddThemeConstantOverride("margin_top", 36);
        margin.AddThemeConstantOverride("margin_bottom", 40);
        AddChild(margin);
        _columnMargin = margin;
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 10);
        margin.AddChild(column);
        _column = column;
        column.MinimumSizeChanged += FitCourseList;
        Resized += FitCourseList;

        // The logo, the version and the update button: top left of whichever page is showing.
        _header = new VBoxContainer();
        _header.AddThemeConstantOverride("separation", 10);
        column.AddChild(_header);
        if (Brand.Logo is { } logo)
        {
            var logoRect = Brand.LogoRect(logo, new Vector2(440, 110), TextureRect.StretchModeEnum.KeepAspect);
            logoRect.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
            logoRect.TooltipText = Hud.Version;
            _header.AddChild(logoRect);
        }
        else _header.AddChild(UiTheme.Text($"{Brand.Name.ToUpperInvariant()} SIMULATOR", 44, UiTheme.Accent, bold: true));
        var version = UiTheme.Text(Hud.Version, 12, UiTheme.Muted);
        _header.AddChild(version);
        _update = new Button { ThemeTypeVariation = "FlatButton", Visible = false, SizeFlagsHorizontal = SizeFlags.ShrinkBegin, TooltipText = "Open the download page" };
        _update.Pressed += () => { if (_updatePage != null) OS.ShellOpen(_updatePage); };
        _header.AddChild(_update);
        _header.AddChild(new Control { CustomMinimumSize = new Vector2(0, 10) });

        BuildCoursePage(column);
        BuildPinPage(column);

        column.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
        _monitorCard = BuildMonitorCard();
        column.AddChild(_monitorCard);
        SetMonitorStatus("Not connected", UiTheme.Muted);
        BuildHomePage();
        BuildNewGameRight();
        BuildPinRight();
    }

    /// <summary>
    /// The home page (a button for each part of the app) and Practice Options, in one layout: the logo at the top left, the
    /// buttons in a list under it, Quit (Back on Practice Options) at the bottom left and the launch monitor at the bottom right.
    /// </summary>
    private void BuildHomePage()
    {
        _homePage = new MarginContainer { Visible = false };
        _homePage.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        // The same margins as the New Round page, so the logo stays put from one page to the next.
        _homePage.AddThemeConstantOverride("margin_left", 64);
        _homePage.AddThemeConstantOverride("margin_right", 56);
        _homePage.AddThemeConstantOverride("margin_top", 36);
        _homePage.AddThemeConstantOverride("margin_bottom", 40);
        AddChild(_homePage);
        var page = new VBoxContainer();
        _homePage.AddChild(page);
        _homeTop = new VBoxContainer();
        page.AddChild(_homeTop);

        // ---- the home page: a list of buttons down the left
        _homeBody = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
        _homeBody.AddThemeConstantOverride("separation", ButtonGap);
        page.AddChild(_homeBody);
        _resume = Card("Resume Round", Glyph.Resume, "Carry on with the unfinished round", () => ResumePressed?.Invoke());
        _newGame = Card("New Round", Glyph.NewRound, "Pick a course and start a round", ShowCourses);
        _practice = Card("Practice", Glyph.Practice, "Warm up: the range, and the putting and chipping greens", ShowPractice);
        _games = Card("Games", Glyph.Games, "Party games: closest to the pin, and more to come", ShowGames);
        _online = Card("Online Round", Glyph.Online, "Play a round with friends at their own simulators", () => OnlinePressed?.Invoke());
        _online.Visible = false;
        var history = Card("History", Glyph.History, "Finished rounds and practice, with averages per club", () => HistoryPressed?.Invoke());
        var settings = Card("Settings", Glyph.Settings, "Players, house rules, launch monitor, text size", () => SettingsPressed?.Invoke());
        _homeCards.AddRange(new[] { _resume, _newGame, _practice, _games, _online, history, settings });
        foreach (var card in _homeCards) _cardStash.AddChild(card);

        // ---- Practice Options
        _practiceBody = new VBoxContainer { Visible = false, SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
        _practiceBody.AddThemeConstantOverride("separation", ButtonGap);
        page.AddChild(_practiceBody);
        _practiceBody.AddChild(UiTheme.Text("Practice Options", 26, UiTheme.Ink, bold: true));
        _range = Card("Range", Glyph.Range, "Hit balls on the driving range", () => RangePressed?.Invoke());
        var putting = Card("Putting Green", Glyph.Putting, "A big practice green with six holes: put the ball anywhere on it and aim at any hole", () => PracticePressed?.Invoke(PracticeKind.Putting));
        var chipping = Card("Chipping Green", Glyph.Chipping, "Chips, pitches and bunker shots from spots around a green with four holes", () => PracticePressed?.Invoke(PracticeKind.Chipping));
        foreach (var card in new[] { _range, putting, chipping }) _practiceBody.AddChild(card);

        // ---- Games
        BuildGamesBody(page);

        // ---- the bottom of the page: Quit (or Back) on the left, the launch monitor on the right
        page.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
        _homeBottom = new HBoxContainer();
        page.AddChild(_homeBottom);
        _quit = new GlyphButton("Quit", Glyph.Quit, $"Close {Brand.Name}") { SizeFlagsVertical = SizeFlags.ShrinkEnd };
        _quit.Pressed += () => QuitPressed?.Invoke();
        _homeBottom.AddChild(_quit);
        _back = new GlyphButton("Back", Glyph.Back, "Back to the menu  [Esc, gamepad B]") { Visible = false, SizeFlagsVertical = SizeFlags.ShrinkEnd };
        _back.Pressed += ShowHome;
        _homeBottom.AddChild(_back);
        _homeBottom.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        ArrangeCards(_homeBody, _homeCards);
    }

    private const int ButtonGap = 12;

    private static MenuCard Card(string label, Glyph glyph, string tooltip, Action? pressed)
    {
        var card = new MenuCard(label, glyph, tooltip);
        if (pressed != null) card.Pressed += pressed;
        return card;
    }

    /// <summary>Puts the buttons that are shown (<see cref="CanvasItem.Visible"/>) in the list in order; the others wait in the stash.</summary>
    private void ArrangeCards(VBoxContainer list, IReadOnlyList<MenuCard> cards)
    {
        foreach (var card in cards) card.GetParent()?.RemoveChild(card);
        foreach (var card in cards) (card.Visible ? list : _cardStash).AddChild(card);
    }

    /// <summary>The launch monitor's card: its status, and a button for the monitor drawer ("Connected" once it is).</summary>
    private PanelContainer BuildMonitorCard()
    {
        var card = new PanelContainer { CustomMinimumSize = new Vector2(380, 0), SizeFlagsVertical = SizeFlags.ShrinkEnd };
        var mv = new HBoxContainer();
        mv.AddThemeConstantOverride("separation", 12);
        _monitorDot = new PanelContainer { CustomMinimumSize = new Vector2(12, 12), SizeFlagsVertical = SizeFlags.ShrinkCenter };
        mv.AddChild(_monitorDot);
        var mt = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        mt.AddThemeConstantOverride("separation", 2);
        _monitorCaption = UiTheme.Caption("Launch monitor");
        mt.AddChild(_monitorCaption);
        _monitorText = UiTheme.Text("Not connected", 18, UiTheme.Ink, bold: true);
        mt.AddChild(_monitorText);
        mv.AddChild(mt);
        _monitorDetails = new Button { Text = "Connection…", ThemeTypeVariation = "FlatButton", SizeFlagsVertical = SizeFlags.ShrinkCenter, TooltipText = "Devices, connect / disconnect, battery, log  [L]" };
        _monitorDetails.Pressed += () => MonitorPressed?.Invoke();
        mv.AddChild(_monitorDetails);
        card.AddChild(mv);
        return card;
    }

    /// <summary>Takes the logo block and the monitor card to the page that is showing (they are one set of controls, so their state is shared).</summary>
    private void MoveChrome(bool toHomePage)
    {
        if (toHomePage)
        {
            if (_header.GetParent() != _homeTop) _header.Reparent(_homeTop, false);
            if (_monitorCard.GetParent() != _homeBottom) _monitorCard.Reparent(_homeBottom, false);
        }
        else
        {
            if (_header.GetParent() != _column) { _header.Reparent(_column, false); _column.MoveChild(_header, 0); }
            if (_monitorCard.GetParent() != _column) _monitorCard.Reparent(_column, false);
        }
    }

    /// <summary>New Round's right side, over the range: the player list and Game Setup under it (a share of the width, so large text leaves them room).</summary>
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
        _courses.AddChild(UiTheme.Text("New Round", 26, UiTheme.Ink, bold: true));
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
    /// <summary>On New Round, Practice Options, Games or a game's setup (Esc / B goes back from there, see <see cref="Back"/>).</summary>
    public bool OnSubPage => Visible && (_courses.Visible || _practiceBody.Visible || _gamesBody.Visible || _pinLeft.Visible);

    /// <summary>
    /// Shows the menu. <paramref name="resume"/> describes the unfinished round (null = none, Resume is hidden);
    /// <paramref name="golfers"/> is who a new round would start with, <paramref name="saved"/> who can be picked for
    /// it, <paramref name="home"/> the home player's name, and <paramref name="setup"/> the game setup New Round starts from.
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
        _resume.SetNote(resume);
        _resume.TooltipText = resume == null ? "Carry on with the unfinished round" : $"Carry on with the unfinished round: {resume}";
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
        _pinPlayerList.NotSavable = home;
        _pinPlayerList.SetSavedPlayers(saved);
        SetGolfers(golfers);
    }

    /// <summary>Who's playing, after the player list was edited here (its rows are left alone: someone may be typing in one).</summary>
    public void SetGolfers(IReadOnlyList<Golfer> golfers)
    {
        _golfers = golfers;
        if (OnSubPage) RefreshSetupNotes();
    }

    /// <summary>Who can be picked for the player list.</summary>
    public void SetSavedPlayers(IReadOnlyList<Golfer> saved)
    {
        _playerList.SetSavedPlayers(saved);
        _pinPlayerList.SetSavedPlayers(saved);
    }

    /// <summary>A newer version of the app can be downloaded: a button for it under the version number.</summary>
    public void ShowUpdate(Releases.Update update)
    {
        _update.Text = $"Version {update.Version} is available";
        _update.Visible = true;
        _updatePage = update.Page;
    }

    /// <summary>Straight to Practice Options (to check how it looks).</summary>
    public void OpenPracticePage()
    {
        if (Visible) ShowPractice();
    }

    /// <summary>Straight to New Round (back from Get More Courses, or to check how it looks).</summary>
    public void OpenCoursePage()
    {
        if (Visible) ShowCourses();
    }

    /// <summary>The home page: the cards. (Also where Back goes from the other pages.)</summary>
    public void ShowHome()
    {
        ShowCardPage(home: true);
        ArrangeCards(_homeBody, _homeCards);
        (_resume.Visible ? _resume : _newGame).GrabFocus();
    }

    /// <summary>Practice Options: the range, the putting green and the chipping green.</summary>
    private void ShowPractice()
    {
        ShowCardPage(home: false);
        _range.GrabFocus();
    }

    /// <summary>
    /// Shows the home page, Practice Options or Games (they share the page: the title and the cards change, and Quit becomes
    /// Back). <paramref name="sub"/> is the page other than home (Practice Options if none is given).
    /// </summary>
    private void ShowCardPage(bool home, Control? sub = null)
    {
        _courses.Visible = false;
        _newGameRight.Visible = false;
        _pinLeft.Visible = false;
        _pinRight.Visible = false;
        _columnMargin.Visible = false;
        _shade.Visible = true;
        _homePage.Visible = true;
        var page = home ? null : sub ?? _practiceBody;
        _homeBody.Visible = home;
        _practiceBody.Visible = page == _practiceBody;
        _gamesBody.Visible = page == _gamesBody;
        _quit.Visible = home;
        _back.Visible = !home;
        MoveChrome(toHomePage: true);
    }

    private void ShowCourses()
    {
        _homePage.Visible = false;
        _shade.Visible = true;
        _columnMargin.Visible = true;
        MoveChrome(toHomePage: false);
        _pinLeft.Visible = false;
        _pinRight.Visible = false;
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

    private Label _monitorCaption = null!;

    /// <summary>Which monitor is connected ("Garmin Approach R10"), over its status (null: none).</summary>
    public void SetMonitorName(string? name) => _monitorCaption.Text = name == null ? "Launch monitor" : $"Launch monitor  ·  {name}";

    /// <summary>Monitor status as in the top bar ("Ready — hit!", "Not connected", ...) with its colour (the dot beside it).</summary>
    public void SetMonitorStatus(string text, Color accent)
    {
        _monitorText.Text = text;
        _monitorAccent = accent;
        StyleMonitorCard();
    }

    /// <summary>A launch monitor is connected: the button says so and the card gets a green border.</summary>
    public void SetMonitorConnected(bool connected)
    {
        _monitorConnected = connected;
        _monitorDetails.Text = connected ? "Connected" : "Connection…";
        StyleMonitorCard();
    }

    private void StyleMonitorCard()
    {
        var box = UiTheme.Box(UiTheme.Card, 12, 2, 16);
        box.BorderColor = _monitorConnected ? UiTheme.Accent : new Color(1, 1, 1, 0.14f);
        _monitorCard.AddThemeStyleboxOverride("panel", box);
        var dot = UiTheme.Box(_monitorAccent, 6, 0, 0);
        _monitorDot.AddThemeStyleboxOverride("panel", dot);
    }
}
