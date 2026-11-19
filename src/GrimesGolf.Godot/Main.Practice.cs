using Godot;
using GrimesGolf.Core;
using GrimesGolf.Core.Audio;
using GrimesGolf.Core.Course;
using GrimesGolf.Core.Storage;

namespace GrimesGolf.Game;

/// <summary>
/// The putting and chipping greens (Practice on the main menu). A practice green is an ordinary course
/// (<see cref="PracticeGreen"/>) with one "hole" per cup, played through the same code as a round: one player, no
/// scoring, no tee. What's different is here: the ball is put down on a spot (one of the green's, or anywhere on the
/// overview map) instead of teed up; the hole being aimed at is picked with F / Shift+F or the panel; a ball holed out
/// is counted in the session's numbers and the next one is put down by itself (or not, as the player chooses); and the
/// shot can be retried as often as wanted. Nothing here is saved as a round, and the round that was being played is put
/// back when practice ends (<see cref="LeavePractice"/>).
/// </summary>
public partial class Main
{
    /// <summary>The practice green being played (null: not practising).</summary>
    private PracticeState? _practice;

    /// <summary>The round that was loaded when practice began, put back when it ends (its save on disk is untouched).</summary>
    private (Match? Match, Course? Def, GameSetup Game, DateTime Started, ShotRecord? Last)? _roundStash;

    /// <summary>How long after a ball is holed the next one is put down (seconds), when that's automatic.</summary>
    private const double PracticeNextSeconds = 3;

    private sealed class PracticeState(PracticeGreen green)
    {
        public PracticeGreen Green { get; } = green;
        public readonly PracticeStats Stats = new();
        /// <summary>The hole being aimed at (an index into <see cref="PracticeGreen.Holes"/>) and the preset spot last used.</summary>
        public int Target, Spot;
        public string SpotName = "";
        /// <summary>Where the ball in play was put down, how far that was from the hole, and how far the first stroke left it (null until then).</summary>
        public P2 Start;
        public double StartMetres;
        public double? LeaveMetres;
        /// <summary>The ball holed last is in <see cref="Stats"/> already (so taking its last stroke back must take it out again).</summary>
        public bool Recorded;
        public AfterHoleOut After = AfterHoleOut.NextSpot;
        /// <summary>The overview map puts the ball where it's clicked, instead of aiming there.</summary>
        public bool Placing;
        public P2? PendingPlace;
        public double PlaceCooldown, LastToast = -10;
    }

    // ---- starting and leaving ----------------------------------------------------------------

    /// <summary>Practice Options: the putting or chipping green, with the ball on its first spot and the hole most in front of it.</summary>
    private void StartPractice(PracticeKind kind)
    {
        if (_loading || _online != null) return;
        if (_outcome != null) FinishShot();
        if (_practice != null) LeavePractice();
        var green = PracticeGreen.Of(kind);
        // The round being played waits on the shelf: practice uses the match and the course while it lasts.
        _roundStash = (_match, _courseDef, _game, _roundStarted, _lastRecord);
        // Practice is in calm weather on fine ground, with the greens and fairways as the player likes them.
        _game = _settings.Setup with { Type = GameType.Stroke, Holes = HoleSet.Full18, Wind = WindStrength.Calm, Weather = Weather.Mild, Altitude = AltitudeSetting.SeaLevel };
        _courseDef = green.Course;
        _lastRecord = null;
        _roundStarted = DateTime.Now;

        const int spot = 1;   // the front centre of the putting green, a short fairway chip on the other
        var state = new PracticeState(green) { Spot = spot, SpotName = green.Spots[spot].Name };
        var ball = green.Spots[spot].At;
        // The hole nearest straight ahead of the ball.
        state.Target = Enumerable.Range(0, green.Holes.Count).MinBy(i => Math.Abs(Math.IEEERemainder(HoleRound.Heading(ball, green.Holes[i].Pin), 2 * Math.PI)));
        _practice = state;
        _match = new Match(green.Holes, green.Course.Terrain, new[] { RangeGolfer }, PracticeGreen.Rules, GameType.Stroke);
        _match.StartHole(state.Target);
        _match.Current.Place(ball);
        BeginAttempt(ball);

        _hud.ShowMainMenu(false);
        EnterCourse();
    }

    /// <summary>Practice is over (the range, the main menu or a round is next): the round that was loaded comes back off the shelf.</summary>
    private void LeavePractice()
    {
        if (_practice == null) return;
        _practice = null;
        _autoNextIn = -1;
        _hud.SetNextHoleCountdown(null);
        _hud.SetPracticeMode(false);
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
        _hud.SetCredit(_courseDef?.Credit);
    }

    /// <summary>A new ball is down at <paramref name="at"/>: where it started, and nothing played yet.</summary>
    private void BeginAttempt(P2 at)
    {
        var p = _practice!;
        p.Start = at;
        p.StartMetres = _match!.Current.DistanceToPin;
        p.LeaveMetres = null;
        p.Recorded = false;
    }

    // ---- moving the ball and the target --------------------------------------------------------

    /// <summary>Shots can't be taken back, moved or aimed elsewhere while the ball is on its way (<paramref name="force"/>: a new shot has come in, so it doesn't matter).</summary>
    private bool PracticeIdle(bool force = false) => _practice != null && _match != null && (force || (_outcome == null && !_player.IsPlaying));

    /// <summary>Puts a fresh ball down at <paramref name="at"/> (a spot of the green, or anywhere that's allowed) and gets the next shot ready.</summary>
    private void PlacePracticeBall(P2 at, string spotName, bool force = false)
    {
        if (!PracticeIdle(force)) return;
        var p = _practice!;
        if (!p.Green.CanPlace(at))
        {
            if (Time.GetTicksMsec() / 1000.0 - p.LastToast > 2)
            {
                p.LastToast = Time.GetTicksMsec() / 1000.0;
                _hud.Toast(p.Green.Kind == PracticeKind.Putting ? "Put the ball on the green" : "The ball can't go there", UiTheme.Warn);
            }
            return;
        }
        var round = _match!.Current;
        round.Place(at);
        p.SpotName = spotName;
        BeginAttempt(at);
        FreshBall();
    }

    /// <summary>The ball has just been put down: its last shot's trail and result go, the countdown stops, the next shot is set up.</summary>
    private void FreshBall()
    {
        var round = _match!.Current;
        _autoNextIn = -1;
        _hud.SetNextHoleCountdown(null);
        _hud.HideResult();
        EndLandingView();
        _player.Clear(RestingBall(round));
        PrepareNextShot(snapCamera: true);
    }

    /// <summary>The ball goes to the next (+1) or previous (−1) of the green's spots.</summary>
    private void StepPracticeSpot(int step, bool force = false)
    {
        if (!PracticeIdle(force)) return;
        PracticeToSpot(_practice!.Green.NextSpot(_practice.Spot, step), force);
    }

    private void PracticeToSpot(int index, bool force = false)
    {
        var p = _practice!;
        p.Spot = index;
        PlacePracticeBall(p.Green.Spots[index].At, p.Green.Spots[index].Name, force);
    }

    private void RandomPracticeSpot(bool force = false)
    {
        if (!PracticeIdle(force)) return;
        PracticeToSpot(_practice!.Green.RandomSpot(_practice.Spot, _rng), force);
    }

    /// <summary>Back on the spot the ball started from (a ball that's strayed, or a putt to try again from the start).</summary>
    private void ReplacePracticeBall(bool force = false)
    {
        if (!PracticeIdle(force)) return;
        var p = _practice!;
        PlacePracticeBall(p.Start, p.SpotName, force);
    }

    /// <summary>The next ball once one has been holed: where the player has chosen (the next spot, a random one, or the same).</summary>
    private void NextPracticeBall(bool force = false)
    {
        if (!PracticeIdle(force)) return;
        switch (_practice!.After)
        {
            case AfterHoleOut.SameSpot: ReplacePracticeBall(force); break;
            case AfterHoleOut.RandomSpot: RandomPracticeSpot(force); break;
            default: StepPracticeSpot(1, force); break;
        }
    }

    /// <summary>Aims at the hole to the right (+1) or left (−1) of the one aimed at; the ball stays where it lies, and starts again from there.</summary>
    private void StepPracticeHole(int step)
    {
        if (!PracticeIdle()) return;
        var p = _practice!;
        var round = _match!.Current;
        int next = p.Green.NextHole(round.Holed ? p.Start : round.Ball, p.Target, step);
        if (next != p.Target) SelectPracticeHole(next);
    }

    private void SelectPracticeHole(int index)
    {
        if (!PracticeIdle()) return;
        var p = _practice!;
        var round = _match!.Current;
        // A ball that's been holed goes back to where it started; one that's still out there is played on from where it lies.
        var ball = round.Holed ? p.Start : round.Ball;
        p.Target = index;
        _match.StartHole(index);
        _match.Current.Place(ball);
        BeginAttempt(ball);
        StartHole(index, fresh: false);
    }

    /// <summary>The overview map was clicked: puts the ball there, or aims there, or picks the hole that was clicked on.</summary>
    private void PracticeMapClick(P2 at)
    {
        var p = _practice!;
        if (p.Placing) { p.PendingPlace = at; return; }
        if (p.Green.HoleNear(at, 2.5) is { } near && near != p.Target) { SelectPracticeHole(near); return; }
        var round = _round;
        if (round == null || round.Holed || _outcome != null || round.Ball.DistanceTo(at) < 0.5) return;
        _aimHeading = HoleRound.Heading(round.Ball, at);
        UpdateAim();
    }

    private void TogglePracticePlacing()
    {
        if (_practice == null) return;
        _practice.Placing = !_practice.Placing;
        _hud.SetPracticePlacing(_practice.Placing);
        if (_practice.Placing) _hud.Toast("Click the overview map to put the ball there", UiTheme.Ink);
    }

    /// <summary>Dragging across the map moves the ball along, but the shot is set up (a putt read is a few dozen trial putts) at most every so often.</summary>
    private void ProcessPractice(double delta)
    {
        if (_practice is not { } p) return;
        p.PlaceCooldown -= delta;
        if (p.PendingPlace is not { } at || p.PlaceCooldown > 0) return;
        p.PendingPlace = null;
        p.PlaceCooldown = 0.12;
        PlacePracticeBall(at, "Your spot");
    }

    /// <summary>Hits it again: the ball goes back to where it was before the last shot (which stays in the saved shots, and out of the session's numbers).</summary>
    private void PracticeRetry()
    {
        if (!PracticeIdle()) return;
        var p = _practice!;
        var round = _match!.Current;
        if (round.BeforeLast is not { } before) { _hud.Toast("Nothing to take back yet", UiTheme.Ink); return; }
        if (round.Holed && p.Recorded) { p.Stats.UndoLast(); p.Recorded = false; }
        round.Restore(before);
        if (round.Strokes == 0) p.LeaveMetres = null;
        FreshBall();
    }

    private void ResetPracticeStats()
    {
        if (_practice == null) return;
        _practice.Stats.Clear();
        UpdatePracticeHud();
        _hud.Toast("Session numbers cleared", UiTheme.Ink);
    }

    private void CyclePracticeAfter()
    {
        if (_practice == null) return;
        _practice.After = (AfterHoleOut)(((int)_practice.After + 1) % 4);
        UpdatePracticeHud();
        _hud.Toast(AfterText(_practice.After), UiTheme.Ink);
    }

    private static string AfterText(AfterHoleOut a) => a switch
    {
        AfterHoleOut.NextSpot => "After a hole-out: next spot",
        AfterHoleOut.RandomSpot => "After a hole-out: random spot",
        AfterHoleOut.SameSpot => "After a hole-out: same spot",
        _ => "After a hole-out: wait for me",
    };

    // ---- during play ----------------------------------------------------------------------------

    /// <summary>
    /// A shot is about to be played. The overview map goes back to aiming; and if the last ball was already holed (a launch
    /// monitor doesn't wait for NEXT BALL) the next ball goes down first, and the shot is played from there.
    /// </summary>
    private void PracticeBeforeShot()
    {
        if (_practice is not { } p) return;
        if (p.Placing) { p.Placing = false; _hud.SetPracticePlacing(false); }
        p.PendingPlace = null;
        if (_match is { Current.Holed: true }) NextPracticeBall(force: true);
    }

    /// <summary>
    /// The result of the shot that's just stopped, for practice: whether it holed (and so counts), where it finished
    /// against the hole, and the next ball in a few seconds if that's how the player has it.
    /// </summary>
    private void FinishPracticeShot(ShotOutcome o)
    {
        var p = _practice!;
        var match = _match!;
        var round = match.Current;
        _outcome = null;
        _player.HideCarryLabel();
        if (o.Penalty > 0 || (!o.Holed && o.NextBall != o.Rest)) _player.PlaceBall(BallAt(o.NextBall));   // water, out of bounds or the cart path: back to where it was played from
        else if (o.Holed) _player.PlaceBall(CupBall(round.Hole));

        var green = p.Green;
        bool putting = green.Kind == PracticeKind.Putting;
        string noun = o.Strokes == 1 ? (putting ? "putt" : "stroke") : (putting ? "putts" : "strokes");
        string headline, note = "";
        if (p.LeaveMetres == null) p.LeaveMetres = o.Holed ? 0 : round.DistanceToPin;
        if (o.Holed)
        {
            p.Stats.Record(new PracticeAttempt(p.StartMetres, round.Strokes, p.LeaveMetres ?? 0));
            p.Recorded = true;
            string from = PinDistance(p.StartMetres, putting || p.StartMetres * Units.MetresToYards < 20);
            headline = round.Strokes == 1 ? (putting ? $"Holed! One putt from {from}" : $"Chipped in from {from}!") : $"Holed in {round.Strokes} {noun} from {from}";
            Sound(round.Strokes == 1 ? SoundEffect.Cheer : SoundEffect.Applause, round.Strokes == 1 ? 0 : -4);
            if (p.After != AfterHoleOut.Wait)
            {
                _autoNextIn = PracticeNextSeconds;
                note = $"Next ball in {PracticeNextSeconds:0} seconds (Space to go now).";
            }
            else note = "Space or N for the next ball.";
        }
        else
        {
            headline = o.Penalty > 0 ? o.Message : "";
            if (o.Penalty == 0) note = MissText(o, round.Hole.Pin);
        }
        match.AdvanceTurn();
        PrepareNextShot();
        _hud.ShowResult(headline, note);
        _hud.ShowShotNumbers(_shotLaunch, ShotResultStats(o.Flight));
        Log($"{headline} {note}".Trim());
    }

    /// <summary>"3 ft past, 1 ft right of the hole" for a ball that finished near the hole (empty when it's well away from it).</summary>
    private static string MissText(ShotOutcome o, P2 pin)
    {
        var rest = o.Rest;
        if (rest.DistanceTo(pin) > 6) return "";
        var line = pin - o.Start;
        double length = Math.Max(line.Length, 1e-6);
        var ahead = new P2(line.X / length, line.Z / length);
        var off = rest - pin;
        double past = off.X * ahead.X + off.Z * ahead.Z, right = off.X * -ahead.Z + off.Z * ahead.X;
        string Ft(double m) => $"{Math.Abs(m) * Units.MetresToYards * 3:0} ft";
        string along = Math.Abs(past) * Units.MetresToYards * 3 < 1 ? "" : $"{Ft(past)} {(past > 0 ? "past" : "short")}";
        string across = Math.Abs(right) * Units.MetresToYards * 3 < 1 ? "" : $"{Ft(right)} {(right > 0 ? "right" : "left")}";
        string both = string.Join(", ", new[] { along, across }.Where(s => s.Length > 0));
        return both.Length == 0 ? "Right at the hole" : $"{both} of the hole";
    }

    /// <summary>The practice countdown to the next ball, shown on the NEXT BALL button; it stops if the ball is no longer holed (a retry) or something has taken over the screen.</summary>
    private void CountDownPractice(double delta)
    {
        if (_match is not { Current.Holed: true } || _outcome != null)
        {
            _autoNextIn = -1;
            _hud.SetNextHoleCountdown(null);
            return;
        }
        if (_hud.ModalOpen || _hud.SettingsOpen) return;
        _autoNextIn -= delta;
        if (_autoNextIn < 0) { _hud.SetNextHoleCountdown(null); NextPracticeBall(); }
        else _hud.SetNextHoleCountdown((int)Math.Ceiling(_autoNextIn));
    }

    /// <summary>
    /// --practice-spot=N and --practice-hole=N (from 1): the ball on that spot, aimed at that hole; --practice-ball=X,Z: the ball
    /// at that point (metres from the green's centre, X toward the back). To check how it looks.
    /// </summary>
    private void PracticeDevSetup(string[] args)
    {
        string? Arg(string name) => args.FirstOrDefault(a => a.StartsWith(name + "="))?[(name.Length + 1)..];
        var green = _practice!.Green;
        if (int.TryParse(Arg("--practice-spot"), out int spot) && spot >= 1 && spot <= green.Spots.Count) PracticeToSpot(spot - 1);
        if (int.TryParse(Arg("--practice-hole"), out int hole) && hole >= 1 && hole <= green.Holes.Count) SelectPracticeHole(hole - 1);
        if (Arg("--practice-ball")?.Split(',') is { Length: 2 } xz && double.TryParse(xz[0], System.Globalization.CultureInfo.InvariantCulture, out double x)
            && double.TryParse(xz[1], System.Globalization.CultureInfo.InvariantCulture, out double z))
            PlacePracticeBall(new P2(x, z), "Your spot");
    }

    // ---- the screen -----------------------------------------------------------------------------

    /// <summary>The hole badge, the panel, the Retry button and the overview map's cups, for the ball and hole as they stand.</summary>
    private void UpdatePracticeHud()
    {
        if (_practice is not { } p || _round is not { } round) return;
        var green = p.Green;
        var stats = p.Stats;
        bool putting = green.Kind == PracticeKind.Putting;
        string stroke = round.Holed ? $"In {round.Strokes}" : $"{round.Strokes + 1}";
        // (With several players saved, the card says whose practice it is: the player the range's chip had picked.)
        string spot = _settings.Golfers.Count > 1 ? $"{p.SpotName}  ·  {_match!.Golfer.Name}" : p.SpotName;
        _hud.SetPracticeCard(putting ? "PUTTING" : "CHIPPING", $"Hole {round.Hole.Number} of {green.Holes.Count}", spot, stroke,
                             stats.Balls.ToString(), stats.Balls == 0 ? "" : $"avg {stats.AverageStrokes:0.0#}");
        _hud.SetPracticePanel($"{round.Hole.Number} of {green.Holes.Count}", p.SpotName, AfterText(p.After), stats.Lines(green.Kind));
        _hud.SetRetry(Putter ? "Retry putt" : "Retry shot", round.BeforeLast != null && _outcome == null);
        _hud.SetPracticePlacing(p.Placing);
        _hud.SetMapHoles(green.Holes.Select(h => _mapCamera.UnprojectPosition(BallAt(h.Pin))).ToList(), p.Target);
    }

    /// <summary>The overview map looks straight down on the whole green (and, on the chipping green, the spots out on the fairway), the north end at the top.</summary>
    private void FramePracticeMinimap()
    {
        var green = _practice!.Green;
        var hole = green.Holes[0];
        var shapes = hole.Greens.Concat(hole.Bunkers).Select(s => s.Box).Concat(green.Spots.Select(s => Bounds.Around(s.At, 1)));
        var box = shapes.Aggregate((a, b) => a.Union(b)).Expand(4);
        var px = _hud.MinimapSize;
        double along = box.Max.X - box.Min.X, wide = box.Max.Z - box.Min.Z;
        _mapCamera.Size = (float)Math.Max(along, wide * px.Y / px.X) * 1.03f;
        _mapCamera.Position = Scenery.ToGodot(box.Center) + new Vector3(0, 400, 0);
        _mapCamera.Rotation = new Vector3(-Mathf.Pi / 2, 0, 0);
    }

    /// <summary>The practice game menu: the ball, the retry, the session, and the way out.</summary>
    private void AddPracticeMenuItems(List<MenuItem> items, bool moving)
    {
        var p = _practice!;
        items.Add(new("Next Ball", () => StepPracticeSpot(1), "The next spot  [N]", !moving));
        items.Add(new("Retry", PracticeRetry, "Hit it again from where it was  [U]", !moving && _match?.Current.BeforeLast != null));
        items.Add(new("Ball Back on Its Spot", () => ReplacePracticeBall(), "Put the ball back where this one started  [R]", !moving));
        items.Add(new("Reset Session Numbers", ResetPracticeStats, $"Clear the {p.Stats.Balls} balls counted so far"));
        items.Add(new("Settings", OpenSettings, "Players, monitor, text size, green speed"));
        items.Add(new("Main Menu", ShowMainMenu, "Practice Options, New Round, quit"));
    }
}
