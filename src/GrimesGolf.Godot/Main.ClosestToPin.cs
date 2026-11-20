using Godot;
using GrimesGolf.Core;
using GrimesGolf.Core.Audio;
using GrimesGolf.Core.Course;
using GrimesGolf.Core.Storage;

namespace GrimesGolf.Game;

/// <summary>
/// Closest to the Pin (Games on the main menu). The players hit at one par 3, a few shots each, and the closest shot wins
/// (<see cref="ClosestToPinGame"/> keeps the order and the standings). It's played through the same code as a round, a
/// bit like practice: each player has a one-hole <see cref="Match"/> of their own that goes back to the tee for every
/// shot (strict house rules, so a ball near the hole isn't tapped in), and what a shot scores is where its ball finished
/// (<see cref="PinShot"/>). The round that was loaded when the game began waits on the same shelf practice uses
/// (<see cref="_roundStash"/>) and comes back when the game ends (<see cref="LeaveClosestToPin"/>); nothing here is saved as a round.
/// </summary>
public partial class Main
{
    /// <summary>The game being played (null: not playing it).</summary>
    private PinState? _pin;

    /// <summary>How long after a shot the next player is put on the tee (seconds), when that's automatic.</summary>
    private const double PinNextSeconds = 5;

    /// <summary>Every shot is hit once, with nothing taken back, and nothing is tapped in.</summary>
    private static readonly HouseRules PinRules = new(Mulligans: 0, GimmeUnits: 0);

    private sealed class PinState(ClosestToPinSetup setup, Course course, Hole hole, ClosestToPinGame game, Match[] matches, int previousCourse)
    {
        public ClosestToPinSetup Setup { get; } = setup;
        /// <summary>The course with the hole moved to the tee chosen (what's loaded and drawn), and the hole as played.</summary>
        public Course Course { get; } = course;
        public Hole Hole { get; } = hole;
        public ClosestToPinGame Game { get; } = game;
        /// <summary>One match for each player, in the players' order: their ball on the tee, or where their last shot finished.</summary>
        public Match[] Matches { get; } = matches;
        /// <summary>The course that was selected before the game began.</summary>
        public int PreviousCourse { get; } = previousCourse;
        /// <summary>The last shot has finished and been counted; the next one isn't on the tee yet.</summary>
        public bool Pending;
    }

    // ---- starting and leaving ----------------------------------------------------------------

    /// <summary>Start Game on Closest to the Pin's setup: loads the course behind the loading screen, then the first player is up.</summary>
    private async void StartClosestToPin(ClosestToPinSetup setup)
    {
        if (_loading || _online != null) return;
        int index = CourseLibrary.All.ToList().FindIndex(e => e.Name == setup.Course);
        if (index < 0) { _hud.Toast("That course isn't installed any more", UiTheme.Warn); return; }
        if (_outcome != null) FinishShot();
        SaveSettings(_settings with { ClosestToPin = setup });
        await WithLoadingScreen(index, () =>
        {
            Course course;
            try { course = CourseLibrary.All[index].Load(); }
            catch (Exception ex)
            {
                Log($"Couldn't load {setup.Course}: {ex.Message}");
                _hud.Toast("Couldn't load that course (see the log)", UiTheme.Danger);
                return;
            }
            var hole = course.Holes.FirstOrDefault(h => h.Number == setup.HoleNumber && h.Par == 3) ?? course.Holes.FirstOrDefault(h => h.Par == 3);
            if (hole == null) { _hud.Toast("That course has no par 3", UiTheme.Warn); return; }
            var tees = TeeBoxes.For(hole, course.Holes);
            var tee = tees[setup.Tee >= 0 && setup.Tee < tees.Count ? setup.Tee : TeeBoxes.DefaultIndex(hole, tees)];
            var played = TeeBoxes.Play(hole, tee);
            BeginClosestToPin(setup, TeeBoxes.WithHole(course, hole, played), played, index, tee);
        });
    }

    /// <summary>Starts (or starts again, with <paramref name="course"/> already built) the game: the first player on the tee.</summary>
    private void BeginClosestToPin(ClosestToPinSetup setup, Course course, Hole hole, int courseIndex, TeeOption tee)
    {
        int previous = _pin?.PreviousCourse ?? _courseIndex;
        if (_practice != null) LeavePractice();
        // The round being played waits on the shelf (the same one practice uses); starting again keeps what's on it.
        if (_pin == null) _roundStash = (_match, _courseDef, _game, _roundStarted, _lastRecord);
        _pin = null;
        _courseIndex = courseIndex;
        _courseDef = course;
        _game = setup.Game with { Type = GameType.Stroke, Holes = HoleSet.Par3s };
        _roundStarted = DateTime.Now;
        _lastRecord = null;

        var golfers = setup.Golfers;
        var game = new ClosestToPinGame(golfers, setup.Shots, setup.Order);
        var matches = golfers.Select(g => new Match(new[] { hole }, course.Terrain, new[] { g }, PinRules, GameType.Stroke) { Trees = course.Obstacles }).ToArray();
        _pin = new PinState(setup, course, hole, game, matches, previous);
        _hud.PinResults.Close();
        SelectPinPlayer();
        Log($"Closest to the Pin: {string.Join(", ", golfers.Select(g => g.Name + (g.LeftHanded ? " (LH)" : "")))}; {setup.Summary}; " +
            $"hole {hole.Number} of {course.Name} from the {(tee.Name == "Tee" ? "tee" : tee.Name.ToLowerInvariant() + " tee")}, {tee.Yards:0} yd. {_game.Summary(golfers.Count)}");
        _hud.ShowMainMenu(false);
        EnterCourse();
    }

    /// <summary>The game is over (the range, the main menu or a round is next): the round that was loaded comes back off the shelf.</summary>
    private void LeaveClosestToPin()
    {
        if (_pin is not { } pin) return;
        _pin = null;
        _autoNextIn = -1;
        _hud.SetNextHoleCountdown(null);
        _hud.SetPinMode(false);
        if (_roundStash is { } stash)
        {
            (_match, _courseDef, _game, _roundStarted, _lastRecord) = stash;
            _roundStash = null;
        }
        else
        {
            _match = null;
            _courseDef = null;
        }
        _courseIndex = pin.PreviousCourse;
        _hud.SetCredit(_courseDef?.Credit);
    }

    /// <summary>The player whose shot is next goes on the tee (at the first shot of the game, and after each shot has been counted).</summary>
    private void SelectPinPlayer()
    {
        var pin = _pin!;
        pin.Pending = false;
        _autoNextIn = -1;
        _hud.SetNextHoleCountdown(null);
        _match = pin.Matches[pin.Game.Turn];
        _match.StartHole(0);
        ApplyHoleWind();
        if (!_courseMode) return;   // (the course isn't up yet: entering it sets everything else up)
        _hud.HideResult();
        EndLandingView();
        _player.Clear(RestingBall(_match.Current));
        PrepareNextShot(snapCamera: true);
    }

    // ---- during play ----------------------------------------------------------------------------

    /// <summary>
    /// A shot is about to be played. If the last one has been counted but the next player isn't up yet (a launch monitor
    /// doesn't wait for NEXT PLAYER), they go on the tee first. False if the game is over, so the shot doesn't count.
    /// </summary>
    private bool PinBeforeShot()
    {
        if (_pin is not { } pin) return true;
        if (pin.Game.Complete) return false;
        if (pin.Pending) SelectPinPlayer();
        return true;
    }

    /// <summary>The ball has stopped: what the shot scored, where that leaves everyone, and the next player in a few seconds.</summary>
    private void FinishPinShot(ShotOutcome o)
    {
        var pin = _pin!;
        var game = pin.Game;
        var round = _match!.Current;
        _outcome = null;
        _player.HideCarryLabel();
        if (o.Penalty > 0 || (!o.Holed && o.NextBall != o.Rest)) _player.PlaceBall(BallAt(o.NextBall));   // water, out of bounds or the cart path: back to where it was played from
        else if (o.Holed) _player.PlaceBall(CupBall(round.Hole));

        int player = game.Turn;
        string name = game.Players[player].Name;
        var shot = PinShot.From(o, pin.Hole);
        var before = game.Standings()[0];
        game.Record(shot);
        pin.Pending = true;
        var standings = game.Standings();
        var leader = standings[0];

        string headline = shot.Distance == null ? $"{(game.Players.Count > 1 ? name + ": " : "")}{o.Message.Split('.')[0]}"
                        : shot.Holed ? $"{(game.Players.Count > 1 ? name + ": " : "")}Holed it!"
                        : $"{(game.Players.Count > 1 ? name + ": " : "")}{shot.Text} from the pin";
        string note = "";
        if (game.Players.Count > 1 && leader.Best != null)
        {
            bool tied = standings.Count(s => s.Place == 1) > 1;
            note = leader.Player == player && !tied ? $"{name} leads"
                 : tied ? $"{string.Join(" & ", standings.Where(s => s.Place == 1).Select(s => game.Players[s.Player].Name))} lead with {leader.Best.Text}"
                 : $"{game.Players[leader.Player].Name} leads with {leader.Best.Text}";
        }
        else if (game.Players.Count == 1 && game.BestOf(player) is { } best && game.ShotsOf(player).Count > 1) note = $"Closest so far: {best.Text}";
        if (game.Complete) note = note.Length > 0 ? note + "  ·  That's the last shot" : "That's the last shot";
        else
        {
            _autoNextIn = PinNextSeconds;
            string up = game.Players[game.Turn].Name + (game.Players.Count > 1 ? $" is up (shot {game.ShotNumber} of {game.ShotsEach})" : $": shot {game.ShotNumber} of {game.ShotsEach}");
            note = note.Length > 0 ? $"{note}   ·   {up}" : up;
        }

        if (shot.Holed)
        {
            _hud.CelebrateScore("HOLE IN ONE!", $"{name} holed it from {pin.Hole.LengthYards:0} yd", new Color(1.0f, 0.82f, 0.25f), sparks: true);
            Sound(SoundEffect.Cheer);
        }
        else if (shot.Distance != null && game.Players.Count > 1 && leader.Player == player && standings.Count(s => s.Place == 1) == 1 && before.Best != null && before.Player != player) Sound(SoundEffect.Applause, -4);

        _hud.ShowResult(headline, note);
        _hud.ShowShotNumbers(_shotLaunch, ShotResultStats(o.Flight));
        Log($"{name}: {shot.Text}{(shot.Distance != null && !shot.Holed ? $" from the pin ({shot.Note.ToLowerInvariant()})" : "")}. {note}");
        UpdatePinHud();
        RefreshPlan();
        if (!game.Complete) return;
        // The standings come up once the last shot has had its moment (if nothing has moved on since).
        GetTree().CreateTimer(2.6).Timeout += () =>
        {
            if (_pin == pin && pin.Game.Complete && _outcome == null && !_hud.PinResults.IsOpen) ShowPinResults(final: true);
        };
    }

    /// <summary>Space, N or the HIT button after a shot: the next player goes on the tee, or the standings come up after the last shot.</summary>
    private void AdvancePin()
    {
        if (_pin is not { Pending: true } pin || _outcome != null) return;
        if (pin.Game.Complete) ShowPinResults(final: true);
        else SelectPinPlayer();
    }

    /// <summary>Counts down to the next player (shown on the NEXT PLAYER button); paused while a menu is open.</summary>
    private void CountDownPin(double delta)
    {
        if (_pin is not { Pending: true } pin || pin.Game.Complete || _outcome != null)
        {
            _autoNextIn = -1;
            _hud.SetNextHoleCountdown(null);
            return;
        }
        if (_hud.ModalOpen || _hud.SettingsOpen) return;
        _autoNextIn -= delta;
        if (_autoNextIn < 0) AdvancePin();
        else _hud.SetNextHoleCountdown((int)Math.Ceiling(_autoNextIn));
    }

    // ---- the screen -------------------------------------------------------------------------

    /// <summary>The hole badge, the player up and the leaderboard (what <see cref="PrepareNextShot"/> shows in a round).</summary>
    private void UpdatePinHud()
    {
        var pin = _pin!;
        var game = pin.Game;
        var hole = pin.Hole;
        int turn = game.Turn;
        var golfer = game.Players[turn];
        bool several = game.Players.Count > 1;
        var best = game.BestOf(turn);
        string shot = game.Complete ? "Done" : $"{game.ShotNumber} of {game.ShotsEach}";
        string closest = best == null ? (game.ShotsOf(turn).Count == 0 ? "-" : "No score") : best.Text;
        string note = best == null ? "" : game.ShotsOf(turn).Count == 1 ? "so far" : $"of {game.ShotsOf(turn).Count} shots";
        _hud.SetHoleCard(hole.Number, hole.Name, hole.Par, hole.LengthYards, shot, closest, note);
        _hud.SetPinDone(game.Complete);
        _hud.SetActivePlayer(golfer.Name, UiTheme.PlayerColor(turn), several);
        _hud.SetTurn(several && !game.Complete ? golfer.Name : null, UiTheme.PlayerColor(turn), "");
        _hud.SetLeaderboard("Closest to the pin", PinLeaderboard(game, turn));
    }

    private static List<LeaderboardRow> PinLeaderboard(ClosestToPinGame game, int turn) =>
        game.Standings().Select(s => new LeaderboardRow(game.Players[s.Player].Name, s.Best?.Text ?? "-", $"{s.ShotsHit}/{game.ShotsEach}",
                                                        UiTheme.PlayerColor(s.Player), s.Player == turn && !game.Complete)).ToList();

    /// <summary>The standings table: at the end of the game (with Play Again), or on request in the middle of it.</summary>
    private void ShowPinResults(bool final)
    {
        if (_pin is not { } pin) return;
        var game = pin.Game;
        var standings = game.Standings();
        var rows = standings.Select(s => new PinRow(
            standings.Count(x => x.Place == s.Place) > 1 ? $"T{s.Place}" : s.Place.ToString(),
            game.Players[s.Player].Name, UiTheme.PlayerColor(s.Player),
            game.ShotsOf(s.Player).Select(x => x.Text).ToList(), s.Best?.Text ?? "-",
            Leader: final && s.Place == 1 && s.Best != null)).ToList();

        string title, message;
        var winners = standings.Where(s => s.Place == 1 && s.Best != null).ToList();
        if (!final)
        {
            title = "Standings";
            message = $"{game.ShotsPlayed} of {game.TotalShots} shots played  ·  {pin.Setup.Summary}";
        }
        else if (winners.Count == 0)
        {
            title = "No winner";
            message = "No ball finished on the course.";
        }
        else if (winners.Count == 1)
        {
            title = $"{game.Players[winners[0].Player].Name} wins!";
            message = $"Closest shot: {winners[0].Best!.Text} from the pin.";
        }
        else
        {
            title = "It's a tie!";
            message = $"{string.Join(" and ", winners.Select(w => game.Players[w.Player].Name))} can't be told apart: {winners[0].Best!.Text} from the pin.";
        }
        var items = final
            ? new MenuItem[]
            {
                new("Play Again", RestartClosestToPin, "The same players, hole and tee", Primary: true),
                new("Main Menu", ShowMainMenu, "Back to the menu"),
            }
            : new MenuItem[] { new("Close", null, Primary: true) };
        _hud.PinResults.Open(title, message, rows, items);
    }

    /// <summary>--closest-to-pin: a game on the first course's first par 3 (<c>--pin-players=N</c>, <c>--pin-shots=N</c>, <c>--pin-row</c> for all shots in a row), for a screenshot or a demo-shot run.</summary>
    private ClosestToPinSetup DevPinSetup(string[] args)
    {
        string? Arg(string name) => args.FirstOrDefault(a => a.StartsWith(name + "="))?[(name.Length + 1)..];
        int players = int.TryParse(Arg("--pin-players"), out var p) ? Math.Clamp(p, 1, ClosestToPinGame.MaxPlayers) : 3;
        int shots = int.TryParse(Arg("--pin-shots"), out var s) ? Math.Clamp(s, 1, ClosestToPinSetup.MaxShots) : 2;
        string[] names = { "Ann", "Bob", "Cat", "Dee", "Eli", "Fay" };
        return new ClosestToPinSetup(names.Take(players).Select(n => new Golfer(n)).ToList(), shots,
                                     args.Contains("--pin-row") ? ShotOrder.AllInARow : ShotOrder.TakeTurns, CourseLibrary.All[0].Name, Conditions: _settings.Setup);
    }

    private void RestartClosestToPin()
    {
        if (_pin is not { } pin || _outcome != null) return;
        var tees = TeeBoxes.For(pin.Hole, pin.Course.Holes);
        var tee = tees.MinBy(t => t.At.DistanceTo(pin.Hole.Tee))!;
        BeginClosestToPin(pin.Setup, pin.Course, pin.Hole, _courseIndex, tee);
    }

    private void ConfirmRestartClosestToPin()
    {
        if (_outcome != null) return;
        _hud.Overlay.Open("Start the game again?", "Everyone's shots are cleared and the first player is back on the tee. The players, hole and tee stay as they are.",
            new MenuItem[] { new("Start Again", RestartClosestToPin, Primary: true), new("Cancel", null) });
    }

    private void ConfirmEndClosestToPin()
    {
        _hud.Overlay.Open("End the game?", "The standings are lost.",
            new MenuItem[] { new("Main Menu", ShowMainMenu, Primary: true), new("Cancel", null) });
    }

    /// <summary>The in-game menu while playing Closest to the Pin.</summary>
    private void AddPinMenuItems(List<MenuItem> items, bool moving)
    {
        items.Add(new("Standings", () => ShowPinResults(final: _pin?.Game.Complete == true), "Everyone's shots so far  [S, gamepad Y]"));
        items.Add(new("Settings", OpenSettings, "Players, monitor, text size"));
        items.Add(new("Restart Game", ConfirmRestartClosestToPin, "Clear the shots and start again", !moving));
        items.Add(new("End Game", ConfirmEndClosestToPin, "Back to the main menu"));
    }
}
