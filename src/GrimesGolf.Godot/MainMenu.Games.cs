using Godot;
using GrimesGolf.Core.Course;

namespace GrimesGolf.Game;

/// <summary>
/// The Games button on the main menu: a page listing the party games, and the setup page of Closest to the Pin. Long Drive
/// and Putt Putt are listed but not playable yet.
/// <para>
/// Closest to the Pin's setup is two columns like New Round's: on the left the course, which of its par 3s and which tee
/// box to play from; on the right up to six players, the shots each, the order they're hit in, and the weather and
/// course conditions. Start Game hands the whole choice to <see cref="ClosestToPinStarted"/>; what was picked is remembered
/// for next time by whoever listens (the app keeps it in its settings).
/// </para>
/// </summary>
public partial class MainMenu
{
    /// <summary>Start Game was pressed on Closest to the Pin's setup, with everything chosen.</summary>
    public event Action<ClosestToPinSetup>? ClosestToPinStarted;

    private VBoxContainer _gamesBody = null!;
    private MenuCard _games = null!;

    private VBoxContainer _pinLeft = null!, _pinRight = null!, _pinCourseList = null!, _pinHoleButtons = null!, _pinTeeButtons = null!;
    private ScrollContainer _pinCourseScroll = null!;
    private Label _pinHoleNote = null!, _pinPlayersNote = null!;
    private Button _pinStart = null!, _pinAltitude = null!;
    private PlayerListEditor _pinPlayerList = null!;
    private ClosestToPinSetup _pin = new();
    private IReadOnlyList<Golfer> _pinGolfers = Array.Empty<Golfer>();
    private int _pinCourse;
    private int _pinHole;
    private int _pinTee;
    /// <summary>The par 3s of the course picked, and where its holes were last read (so the lists are only rebuilt when they change).</summary>
    private List<Hole> _pinThrees = new();
    private IReadOnlyList<Hole>? _pinHolesFrom;

    // ---- the Games page ----

    /// <summary>The Games page's buttons (the page itself shares the home page's layout, see <c>BuildHomePage</c>).</summary>
    private void BuildGamesBody(VBoxContainer page)
    {
        _gamesBody = new VBoxContainer { Visible = false, SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
        _gamesBody.AddThemeConstantOverride("separation", ButtonGap);
        page.AddChild(_gamesBody);
        _gamesBody.AddChild(UiTheme.Text("Games", 26, UiTheme.Ink, bold: true));
        var pin = Card("Closest to the Pin", Glyph.ClosestToPin, "Up to six players take turns hitting at a par 3: the closest shot wins", ShowPinSetup);
        var drive = Card("Long Drive", Glyph.LongDrive, "A drive down a fairway grid: the longest one in the grid wins", null);
        drive.Disabled = true;
        drive.SetNote("Coming soon");
        var putt = Card("Putt Putt", Glyph.PuttPutt, "A kid-friendly 18-hole putt-putt course", null);
        putt.Disabled = true;
        putt.SetNote("Coming soon");
        foreach (var card in new[] { pin, drive, putt }) _gamesBody.AddChild(card);
        _pinCard = pin;
    }

    private MenuCard _pinCard = null!;

    /// <summary>Games: the list of games.</summary>
    public void ShowGames()
    {
        ShowCardPage(home: false, _gamesBody);
        _pinCard.GrabFocus();
    }

    /// <summary>Straight to the Games page (to check how it looks).</summary>
    public void OpenGamesPage()
    {
        if (Visible) ShowGames();
    }

    /// <summary>Straight to Closest to the Pin's setup (to check how it looks).</summary>
    public void OpenPinPage()
    {
        if (Visible) ShowPinSetup();
    }

    // ---- Closest to the Pin: setup ----

    /// <summary>What was chosen last time (null = nothing yet): the setup page opens with it.</summary>
    public void SetClosestToPin(ClosestToPinSetup? saved)
    {
        _pin = saved ?? new ClosestToPinSetup();
        _pinPlayerList?.Load(_pin.Players is { Count: > 0 } ? _pin.Golfers : _golfers);
        _pinGolfers = _pinPlayerList?.Golfers ?? _golfers;
        int course = _cards.ToList().FindIndex(c => c.Name == _pin.Course);
        _pinCourse = course >= 0 ? course : _selected;
        _pinHole = _pin.HoleNumber;
        _pinTee = _pin.Tee;
    }

    private void BuildPinPage(VBoxContainer column)
    {
        _pinLeft = new VBoxContainer { Visible = false };
        _pinLeft.AddThemeConstantOverride("separation", 10);
        column.AddChild(_pinLeft);
        _pinLeft.AddChild(UiTheme.Text("Closest to the Pin", 26, UiTheme.Ink, bold: true));
        _pinLeft.AddChild(UiTheme.Caption("Course"));
        _pinCourseScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true };
        _pinCourseList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _pinCourseList.AddThemeConstantOverride("separation", 6);
        _pinCourseScroll.AddChild(_pinCourseList);
        _pinLeft.AddChild(_pinCourseScroll);

        _pinLeft.AddChild(UiTheme.Caption("Par 3"));
        _pinHoleButtons = new VBoxContainer();
        _pinLeft.AddChild(_pinHoleButtons);
        _pinLeft.AddChild(UiTheme.Caption("Tee box"));
        _pinTeeButtons = new VBoxContainer();
        _pinLeft.AddChild(_pinTeeButtons);
        _pinHoleNote = Note(_pinLeft);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 10);
        _pinStart = new Button { Text = "Start Game", ThemeTypeVariation = "PrimaryButton", CustomMinimumSize = new Vector2(220, 54) };
        _pinStart.AddThemeFontSizeOverride("font_size", 20);
        _pinStart.Pressed += StartPin;
        buttons.AddChild(_pinStart);
        buttons.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var back = new Button { Text = "Back", ThemeTypeVariation = "FlatButton", CustomMinimumSize = new Vector2(0, 54), TooltipText = "Back to the games  [Esc, gamepad B]" };
        back.Pressed += ShowGames;
        buttons.AddChild(back);
        _pinLeft.AddChild(buttons);
    }

    private void BuildPinRight()
    {
        _pinRight = new VBoxContainer { Visible = false };
        _pinRight.AddThemeConstantOverride("separation", 10);
        _pinRight.AnchorLeft = 0.5075f;
        _pinRight.AnchorRight = 0.97f;
        _pinRight.AnchorBottom = 1;
        _pinRight.OffsetTop = 36;
        _pinRight.OffsetBottom = -40;
        AddChild(_pinRight);

        var playerPanel = new PanelContainer();
        playerPanel.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Card, 12, 1, 18, 14));
        _pinRight.AddChild(playerPanel);
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 10);
        playerPanel.AddChild(v);
        var head = new HBoxContainer();
        var title = UiTheme.Text($"Players (up to {ClosestToPinGame.MaxPlayers})", 24, UiTheme.Ink, bold: true);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        title.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        head.AddChild(title);
        v.AddChild(head);
        _pinPlayerList = new PlayerListEditor { AddButtonHost = head, MaxPlayers = ClosestToPinGame.MaxPlayers };
        _pinPlayerList.SaveGuest += g => GuestSaved?.Invoke(g);
        _pinPlayerList.Changed += () => { _pinGolfers = _pinPlayerList.Golfers; RefreshPinNotes(); };
        v.AddChild(_pinPlayerList);

        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true, SizeFlagsVertical = SizeFlags.ExpandFill };
        var card = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkBegin };
        card.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Card, 12, 1, 18, 14));
        var s = new VBoxContainer();
        s.AddThemeConstantOverride("separation", 5);
        card.AddChild(s);
        scroll.AddChild(card);
        _pinRight.AddChild(scroll);

        s.AddChild(UiTheme.Text("Game Setup", 24, UiTheme.Ink, bold: true));
        Row(s, "Shots each", ClosestToPinSetup.ShotChoices, n => n.ToString(), () => _pin.Shots, n => _pin = _pin with { ShotsEach = n },
            n => $"{n} shot{(n == 1 ? "" : "s")} for every player", RefreshPinNotes);
        Row(s, "Shot order", Enum.GetValues<ShotOrder>(), o => o == ShotOrder.TakeTurns ? "Take turns" : "All in a row", () => _pin.Order, o => _pin = _pin with { Order = o },
            o => o == ShotOrder.TakeTurns ? "Everyone hits once, then round again" : "A player hits all their shots, then the next player", RefreshPinNotes);
        _pinPlayersNote = Note(s);

        s.AddChild(new HSeparator());
        s.AddChild(UiTheme.Caption("Environment"));
        Row(s, "Wind", Enum.GetValues<WindStrength>(), w => w.ToString(), () => _pin.Game.Wind, w => PinConditions(_pin.Game with { Wind = w }),
            w => w switch
            {
                WindStrength.Light => "2-7 mph", WindStrength.Breezy => "7-13 mph", WindStrength.Windy => "13-21 mph", _ => "No wind",
            } + "; the same for every player", RefreshPinNotes);
        var altitude = Row(s, "Altitude", Enum.GetValues<AltitudeSetting>(), GameSetup.Name, () => _pin.Game.Altitude, a => PinConditions(_pin.Game with { Altitude = a }),
            _ => "Thinner air higher up: the ball flies further", RefreshPinNotes);
        _pinAltitude = altitude[0].Button;
        Row(s, "Weather", Enum.GetValues<Weather>(), w => w.ToString(), () => _pin.Game.Weather, w => PinConditions(_pin.Game with { Weather = w }),
            w => w switch
            {
                Weather.Hot => "95 °F: warm air is thinner, the ball carries further",
                Weather.Cold => "45 °F: cold air is denser, the ball carries less",
                Weather.Wet => "Rain: soft, wet ground; the ball plugs and rolls out less",
                _ => "Mild and dry",
            }, RefreshPinNotes);

        s.AddChild(new HSeparator());
        s.AddChild(UiTheme.Caption("Course"));
        Row(s, "Fairways", Enum.GetValues<FairwaySpeed>(), f => f.ToString(), () => _pin.Game.Fairways, f => PinConditions(_pin.Game with { Fairways = f }),
            f => f switch { FairwaySpeed.Slow => "Lush: the ball stops quickly", FairwaySpeed.Fast => "Firm and dry: lots of run-out", _ => "Normal run-out" }, RefreshPinNotes);
        Row(s, "Green firmness", Enum.GetValues<GreenFirmness>(), g => g.ToString(), () => _pin.Game.Greens, g => PinConditions(_pin.Game with { Greens = g }),
            g => g switch { GreenFirmness.Soft => "Approach shots stop and spin back", GreenFirmness.Firm => "Approach shots bounce and release", _ => "Normal" }, RefreshPinNotes);
        Row(s, "Green speed", GameSetup.Stimps, x => $"{x:0}", () => _pin.Game.Stimp, x => PinConditions(_pin.Game with { Stimp = x }),
            x => $"Stimp {x:0}: {(x <= 8 ? "slow" : x <= 10 ? "medium" : x <= 11 ? "quick" : "tournament fast")}", RefreshPinNotes);
    }

    private void PinConditions(GameSetup conditions) => _pin = _pin with { Conditions = conditions };

    private void ShowPinSetup()
    {
        _homePage.Visible = false;
        _courses.Visible = false;
        _newGameRight.Visible = false;
        _shade.Visible = true;
        _columnMargin.Visible = true;
        MoveChrome(toHomePage: false);
        _pinLeft.Visible = true;
        _pinRight.Visible = true;
        _pinCourse = Math.Clamp(_pinCourse, 0, Math.Max(0, _cards.Count - 1));
        foreach (var sync in _syncRows) sync();

        foreach (var c in _pinCourseList.GetChildren()) c.Free();
        var group = new ButtonGroup();
        for (int i = 0; i < _cards.Count; i++)
        {
            int index = i;
            var b = new Button { Text = _cards[i].Name, ToggleMode = true, ButtonGroup = group, ThemeTypeVariation = "TabButton",
                                 Alignment = HorizontalAlignment.Left, CustomMinimumSize = new Vector2(0, 48) };
            b.AddThemeFontSizeOverride("font_size", 19);
            b.Toggled += on => { if (on) SelectPinCourse(index); };
            _pinCourseList.AddChild(b);
        }
        _pinCourseScroll.CustomMinimumSize = new Vector2(0, Math.Min(Math.Max(_cards.Count, 1), 3) * 54);
        if (_cards.Count > 0) ((Button)_pinCourseList.GetChild(_pinCourse)).SetPressedNoSignal(true);
        _pinHolesFrom = null;
        SelectPinCourse(_pinCourse);
        _pinStart.GrabFocus();
    }

    private void SelectPinCourse(int index)
    {
        if (index >= _cards.Count) { RefreshPinHoles(); return; }
        if (index != _pinCourse) { _pinCourse = index; _pinHole = 0; _pinTee = -1; }
        if (!_stats.ContainsKey(index)) CourseStatsWanted?.Invoke(index);
        if (_pinCourseList.GetChildCount() > index) _pinCourseScroll.EnsureControlVisible((Control)_pinCourseList.GetChild(index));
        RefreshPinHoles();
    }

    /// <summary>The course's par 3s and the picked one's tees, once the course has loaded (and again if it loads later).</summary>
    private void RefreshPinHoles()
    {
        _holes.TryGetValue(_pinCourse, out var all);
        if (!ReferenceEquals(all, _pinHolesFrom))
        {
            _pinHolesFrom = all;
            _pinThrees = all?.Where(h => h.Par == 3).ToList() ?? new List<Hole>();
            foreach (var c in _pinHoleButtons.GetChildren()) c.Free();
            if (_pinThrees.Count > 0 && !_pinThrees.Any(h => h.Number == _pinHole)) { _pinHole = _pinThrees[0].Number; _pinTee = -1; }
            var group = new ButtonGroup();
            var flow = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            flow.AddThemeConstantOverride("h_separation", 6);
            flow.AddThemeConstantOverride("v_separation", 6);
            _pinHoleButtons.AddChild(flow);
            foreach (var hole in _pinThrees)
            {
                var h = hole;
                var b = new Button { Text = $"Hole {hole.Number} · {hole.LengthYards:0} yd", ToggleMode = true, ButtonGroup = group, ThemeTypeVariation = "TabButton",
                                     CustomMinimumSize = new Vector2(66, 38), TooltipText = hole.Name };
                b.AddThemeFontSizeOverride("font_size", 16);
                b.SetPressedNoSignal(hole.Number == _pinHole);
                b.Toggled += on => { if (on) { _pinHole = h.Number; _pinTee = -1; RefreshPinTees(); } };
                flow.AddChild(b);
            }
        }
        RefreshPinTees();
    }

    private void RefreshPinTees()
    {
        foreach (var c in _pinTeeButtons.GetChildren()) c.Free();
        var hole = _pinThrees.FirstOrDefault(h => h.Number == _pinHole);
        if (hole == null)
        {
            _pinHoleNote.Text = _holes.ContainsKey(_pinCourse) ? "This course has no par 3s." : "Loading the course…";
            _pinStart.Disabled = true;
            RefreshPinNotes();
            return;
        }
        var tees = TeeBoxes.For(hole, _holes[_pinCourse]);
        if (_pinTee < 0 || _pinTee >= tees.Count) _pinTee = TeeBoxes.DefaultIndex(hole, tees);
        var group = new ButtonGroup();
        var flow = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        flow.AddThemeConstantOverride("h_separation", 6);
        flow.AddThemeConstantOverride("v_separation", 6);
        _pinTeeButtons.AddChild(flow);
        for (int i = 0; i < tees.Count; i++)
        {
            int index = i;
            var b = new Button { Text = tees[i].Label, ToggleMode = true, ButtonGroup = group, ThemeTypeVariation = "TabButton", CustomMinimumSize = new Vector2(66, 38) };
            b.AddThemeFontSizeOverride("font_size", 16);
            b.SetPressedNoSignal(i == _pinTee);
            b.Toggled += on => { if (on) { _pinTee = index; RefreshPinNotes(); } };
            flow.AddChild(b);
        }
        _pinStart.Disabled = false;
        RefreshPinNotes();
    }

    /// <summary>The line under the tees and the one under the order: what's about to be played.</summary>
    private void RefreshPinNotes()
    {
        if (_pinLeft == null || !_pinLeft.Visible) return;
        var hole = _pinThrees.FirstOrDefault(h => h.Number == _pinHole);
        if (hole != null && _holes.TryGetValue(_pinCourse, out var all))
        {
            var tees = TeeBoxes.For(hole, all);
            var tee = tees[Math.Clamp(_pinTee, 0, tees.Count - 1)];
            _pinHoleNote.Text = $"Hole {hole.Number}{(hole.Name.Length > 0 ? $", {hole.Name}" : "")}: {tee.Yards:0} yd from the {(tee.Name == "Tee" ? "tee" : tee.Name.ToLowerInvariant() + " tee")}.";
        }
        var golfers = _pinGolfers.Count > 0 ? _pinGolfers : _golfers;
        int n = Math.Clamp(golfers.Count, 1, ClosestToPinGame.MaxPlayers);
        var setup = _pin with { Players = golfers };
        _pinPlayersNote.Text = $"{n} player{(n == 1 ? "" : "s")}: {setup.Summary}, {n * setup.Shots} shots in all.";
        _pinAltitude.Text = _altitudeFeet.TryGetValue(_pinCourse, out var ft) ? $"Course ({ft:#,0} ft)" : "Course";
    }

    private void StartPin()
    {
        if (_pinCourse >= _cards.Count || _pinThrees.FirstOrDefault(h => h.Number == _pinHole) == null) return;
        var setup = _pin with
        {
            Players = _pinPlayerList.Golfers, Course = _cards[_pinCourse].Name, HoleNumber = _pinHole, Tee = _pinTee,
        };
        _pin = setup;
        ClosestToPinStarted?.Invoke(setup);
    }
}
