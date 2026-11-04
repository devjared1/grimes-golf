using Godot;
using SquareGspro.Core;
using SquareGspro.Core.Course;

namespace SquareGspro.Range3D;

/// <summary>
/// The main menu (New Game, Resume Game, Driving Range, Settings, Quit) and the in-game menu (Settings, holes,
/// players, restart and end the game, camera, physics, monitor).
/// </summary>
public partial class Main
{
    private void SetupMainMenu()
    {
        var menu = _hud.MainMenu;
        menu.StartCourse += StartNewGame;
        menu.ResumePressed += ResumeGame;
        menu.RangePressed += () => { _hud.ShowMainMenu(false); EnterRange(); };
        menu.SettingsPressed += OpenSettings;
        menu.QuitPressed += Quit;
        menu.MonitorPressed += _hud.ToggleDrawer;
        menu.CourseStatsWanted += LoadCourseStats;
    }

    /// <summary>
    /// The main menu, over the range: a course being played is put away (its round is saved, for Resume Game).
    /// </summary>
    private void ShowMainMenu()
    {
        if (_outcome != null) FinishShot();
        if (_courseMode) { SaveRound(); EnterRange(); }
        _hud.ShowMainMenu(true);
        var courses = CourseLibrary.All.Select(e => new CourseCard(e.Name, e.Description)).ToList();
        _hud.MainMenu.Open(ResumeText(), courses, _courseIndex, PlayersText(), _settings.Golfers, _settings.Setup);
    }

    /// <summary>"Deer Run · hole 5 of 18 · Ann, Bob · Tue 14:05" for the round Resume Game would carry on (null = none).</summary>
    private string? ResumeText()
    {
        if (_match is { RoundComplete: false } m && _courseDef != null)
            return $"{_courseDef.Name}  ·  {GameSetup.Name(m.Type)}  ·  hole {m.Hole.Number} ({m.HoleIndex + 1} of {m.Holes.Count})  ·  {string.Join(", ", m.Players.Select(p => p.Name))}";
        if (_data?.LoadCurrentRound() is { Complete: false } saved && CourseLibrary.All.Any(e => e.Name == saved.Course))
        {
            int index = saved.Players[0].Current.HoleIndex;
            int hole = saved.HoleNumbers is { } numbers && index < numbers.Length ? numbers[index] : index + 1;
            var type = saved.Game.TypeFor(saved.Players.Length);
            return $"{saved.Course}  ·  {GameSetup.Name(type)}  ·  hole {hole} ({index + 1} of {saved.Pars.Length})  ·  " +
                   $"{string.Join(", ", saved.Players.Select(p => p.Name))}  ·  {saved.Updated:ddd HH:mm}";
        }
        return null;
    }

    private string PlayersText()
    {
        var g = _settings.Golfers;
        return $"Players ({g.Count}): {string.Join(", ", g.Select(p => p.Name + (p.LeftHanded ? " (LH)" : "")))}";
    }

    /// <summary>Resume Game: the round in memory if there is one, otherwise the one saved last time.</summary>
    private async void ResumeGame()
    {
        if (_loading) return;
        bool inMemory = _match is { RoundComplete: false } && _courseDef != null;
        if (!inMemory)
        {
            var saved = _data?.LoadCurrentRound();
            int index = saved == null ? -1 : CourseLibrary.All.ToList().FindIndex(e => e.Name == saved.Course);
            if (index < 0) { _hud.Toast("No unfinished round to resume", UiTheme.Warn); return; }
            _courseIndex = index;
        }
        // The loading screen either way: the course's 3D world is built again on the way back in.
        await WithLoadingScreen(_courseIndex, () =>
        {
            if (!inMemory && !LoadCourse(resume: true)) { _hud.Toast("Couldn't load that course (see the log)", UiTheme.Danger); return; }
            _hud.ShowMainMenu(false);
            EnterCourse();
        });
    }

    /// <summary>Loads a course in the background for its stats line in the New Game list (it's kept for playing it).</summary>
    private void LoadCourseStats(int index)
    {
        var entry = CourseLibrary.All[index];
        Task.Run(() =>
        {
            string stats;
            Course? c = null;
            try
            {
                c = entry.Load();
                stats = $"{c.Holes.Count} holes  ·  Par {c.Holes.Sum(h => h.Par)}  ·  {c.Holes.Sum(h => h.LengthYards):#,0} yd";
            }
            catch (Exception ex) { stats = $"Couldn't load: {ex.Message}"; }
            Post(() =>
            {
                _hud.MainMenu.SetCourseStats(index, stats);
                if (c != null) _hud.MainMenu.SetCourseHoles(index, c.Holes, CourseAltitudeMetres(c) / Units.FeetToMetres);
            });
        });
    }

    // ---- in-game menu ------------------------------------------------------------------------

    /// <summary>The in-game menu (Menu button, M, Esc with nothing else open, gamepad Start).</summary>
    private void OpenGameMenu()
    {
        if (_hud.MainMenu.IsOpen) return;
        bool moving = _outcome != null || _player.IsPlaying;
        var items = new List<MenuItem> { new("Resume", null, "Back to the game  [Esc]", Primary: true) };
        string subtitle = "";
        if (_courseMode && _match is { } m)
        {
            subtitle = _game.Summary(m.Players.Count);
            items.Add(new("Scorecard", () => _hud.ShowScorecard(true), "[S, gamepad Y]"));
            items.Add(new("Settings", OpenSettings, "Players, house rules, monitor, text size"));
            items.Add(new("Previous Hole", PreviousHole, "Everyone to the tee of the hole before", !moving));
            items.Add(new("Next Hole", NextHole, "Everyone to the next tee  [N]", !moving));
            items.Add(new("Restart Hole", RestartHole, "Everyone back to this tee  [R]", !moving));
            items.Add(new("Add / Remove Players", () => _hud.Players.Open(m.Players), "Players can join or leave part-way through", !moving));
            items.Add(new("Restart Game", ConfirmRestartGame, "A new round from the first tee", !moving));
            items.Add(new("End Game", ConfirmEndGame, "Back to the main menu"));
        }
        else
        {
            items.Add(new("Settings", OpenSettings, "Players, house rules, monitor, text size"));
            items.Add(new("Clear Session", ClearRange, "Clear the shots on the range"));
            items.Add(new("Main Menu", ShowMainMenu, "New game, resume, settings, quit"));
        }
        items.Add(new(_camera.Follow ? "Camera: Follow Ball" : "Camera: Stay Behind", ToggleCamera, "Follow the ball or stay behind it  [C]"));
        items.Add(new("Launch Monitor", _hud.ToggleDrawer, "Devices, connect / disconnect, battery, log  [L]"));
        items.Add(new("Physics Tuner", _tuner.Toggle, "Adjust the physics between shots, in its own window  [T]"));
        if (items.Count % 2 == 1) items.Add(new("Close", null));
        _hud.Overlay.Open(_courseMode ? $"Hole {_match?.Hole.Number}  ·  {_courseDef?.Name}" : "Driving range", subtitle, items, columns: 2);
    }

    private void ConfirmRestartGame()
    {
        if (_outcome != null) return;
        _hud.Overlay.Open("Restart the game?", "Everyone goes back to the first tee and this round's scores are dropped. Players and house rules come from the settings; the game and conditions stay as they are.",
            new MenuItem[] { new("Restart Game", NewRound, Primary: true), new("Cancel", null) });
    }

    private void ConfirmEndGame()
    {
        bool done = _match?.RoundComplete ?? true;
        _hud.Overlay.Open("End the game?",
            done ? "The round is finished and saved with your scorecards." : "Keep the round to carry on later with Resume Game on the main menu, or throw it away.",
            done ? new MenuItem[] { new("Main Menu", ShowMainMenu, Primary: true), new("Cancel", null) }
                 : new MenuItem[] { new("Keep It and Go to Menu", ShowMainMenu, Primary: true), new("Discard the Round", DiscardRound), new("Cancel", null) });
    }

    /// <summary>End Game without keeping the round.</summary>
    private void DiscardRound()
    {
        if (_outcome != null) FinishShot();
        _match = null;
        _data?.ClearCurrentRound();
        if (_courseMode) EnterRange();
        ShowMainMenu();
    }
}
