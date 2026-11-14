using System.Collections.Concurrent;
using Godot;
using GrimesGolf.Bluetooth;
using GrimesGolf.Core;
using GrimesGolf.Core.Audio;
using GrimesGolf.Core.Course;
using GrimesGolf.Core.Square;
using GrimesGolf.Core.Storage;
using GrimesGolf.Physics;

namespace GrimesGolf.Game;

/// <summary>Shots from every source (monitor, HIT SHOT on screen, demo, replay), and the driving range.</summary>
public partial class Main
{
    private readonly Random _rng = new();
    private int _shotCount;

    // Physics tuner
    private TunerWindow _tuner = null!;
    /// <summary>The last shot played, to replay with new physics: on a course, with that player's round as it was before it.</summary>
    private sealed record LastShot(RangeShot Shot, string Club, Match? Match, int Player, HoleRound.State? Before, double Heading);
    private LastShot? _last;

    /// <summary>Who range shots are saved under (the player chip in the top bar picks).</summary>
    private int _rangePlayer;
    private Golfer RangeGolfer => _settings.Golfers[_rangePlayer % _settings.Golfers.Count];

    /// <summary>Range: the next player (shots are saved under them; the monitor switches to their hand).</summary>
    private void NextRangePlayer()
    {
        if (_courseMode) return;
        _rangePlayer = (_rangePlayer + 1) % _settings.Golfers.Count;
        ShowRangePlayer();
    }

    private void ShowRangePlayer()
    {
        var g = RangeGolfer;
        _hud.SetActivePlayer(g.Name, UiTheme.PlayerColor(_rangePlayer), _settings.Golfers.Count > 1);
        _hud.SetPlayerClub(_hud.ClubCode, g.LeftHanded);
    }

    // ---- range aim ---------------------------------------------------------------------------

    /// <summary>Range aim (radians, 0 = straight downrange, positive = right) and the flag aimed at (−1 = none).</summary>
    private double _rangeAim;
    private int _rangeTarget = -1;

    /// <summary>A range point (Godot) as a course-style point: X downrange, Z right of the tee.</summary>
    private static P2 RangePoint(Vector3 v) => new(-v.Z, v.X);

    private static Vector3 RangeDirection(double heading) => new((float)Math.Sin(heading), 0, (float)-Math.Cos(heading));

    /// <summary>Aims at the next (+1) or previous (−1) flag; after the last, straight down the range again.</summary>
    private void CycleRangeTarget(int step)
    {
        if (_courseMode) return;
        int n = RangeWorld.TargetYards.Length + 1;   // the flags, plus "straight"
        _rangeTarget = ((_rangeTarget + 1 + step) % n + n) % n - 1;
        _rangeAim = _rangeTarget < 0 ? 0 : HoleRound.Heading(new P2(0, 0), RangePoint(RangeWorld.TargetPosition(_rangeTarget)));
        UpdateRangeAim();
    }

    private void AimRange(double degrees)
    {
        _rangeAim = Math.Clamp(_rangeAim + degrees * Math.PI / 180, -Math.PI / 4, Math.PI / 4);
        UpdateRangeAim();
    }

    /// <summary>
    /// The range's aim: the camera behind the tee looking along it, the line on the ground and on the overview map
    /// out to where the power set carries the ball, and what it's aimed at.
    /// </summary>
    private void UpdateRangeAim(bool snap = false)
    {
        if (_courseMode) return;
        var dir = RangeDirection(_rangeAim);
        double length = AimDistance(null);
        bool resting = !_player.IsPlaying && _rangeResult == null;
        _camera.SetHome(Vector3.Zero, dir, putting: false, snap, (float)length);
        if (resting) AimLandingView(dir * (float)length, dir); else EndLandingView();
        _rangeWorld.SetAim(_rangeAim, length, resting, _landingView);

        string target = "Straight", note;
        Vector3? flag = null;
        if (_rangeTarget >= 0)
        {
            flag = RangeWorld.TargetPosition(_rangeTarget);
            var at = RangePoint(flag.Value);
            target = $"{RangeWorld.TargetYards[_rangeTarget]} yd flag";
            double off = Math.IEEERemainder(_rangeAim - HoleRound.Heading(new P2(0, 0), at), 2 * Math.PI);
            double yd = Math.Sin(off) * at.Length * Units.MetresToYards;
            note = Math.Abs(yd) < 1 ? "At the flag" : $"{Math.Abs(yd):0} yd {(yd > 0 ? "right" : "left")} of the flag";
        }
        else
        {
            double deg = _rangeAim * 180 / Math.PI;
            note = Math.Abs(deg) < 0.1 ? "Down the middle" : $"{Math.Abs(deg):0.##}° {(deg > 0 ? "right" : "left")}";
        }
        _hud.SetRangeAim(target, note);
        if (!resting) { _hud.HideMapMarkers(); return; }
        _hud.SetMapMarkers(_mapCamera.UnprojectPosition(Vector3.Zero), _mapCamera.UnprojectPosition(dir * (float)length),
            flag is { } f ? _mapCamera.UnprojectPosition(f) : null,
            $"{length * Units.MetresToYards:0} yd", flag != null ? target.Replace(" flag", "") : "", UiTheme.PlayerColor(_rangePlayer));
    }

    /// <summary>
    /// How far the shot set up goes (m): the power's share of the club's carry from <paramref name="stance"/>, or of
    /// the putt's roll. With a launch monitor the swing sets the power, so a full shot's aim is the club's carry (the
    /// player's own distances, or the game's), moved up or down the line with ↑ / ↓ (<see cref="_aimNudge"/>).
    /// </summary>
    private double AimDistance(LieCondition? stance)
    {
        if (Putter) return _hud.Shot.Power * _puttScale;
        double carry = Caddie.CarryFrom(_hud.ClubCode, stance, KnownYards);
        return _monitorLive ? Math.Clamp(carry + _aimNudge, MinAimMetres, Math.Max(carry * 1.5, MinAimMetres)) : _hud.Shot.Power * carry;
    }

    /// <summary>With a launch monitor: how far (m) ↑ / ↓ have moved a full shot's aim from the club's carry (back to 0 for each shot and club).</summary>
    private double _aimNudge;
    private const double MinAimMetres = 5 * Units.YardsToMetres;

    /// <summary>↑ / ↓ (Shift: 5 at a time): power by the percent on screen; with a monitor, the aim along the line by the yard.</summary>
    private void NudgeAim(int step)
    {
        if (!_monitorLive) { _hud.Shot.NudgePower(step); return; }
        if (Putter) { _hud.Shot.NudgePower(step); return; }   // the putt's line: how far it rolls
        if (_courseMode && (_round is not { Holed: false } || _outcome != null)) return;
        // From where the aim is now (it may be held at its limits), a yard at a time.
        var stance = _courseMode && _round is { } r ? r.StanceAt(r.Ball, _aimHeading, _hud.LeftHanded) : null;
        double carry = Caddie.CarryFrom(_hud.ClubCode, stance, KnownYards);
        _aimNudge = AimDistance(stance) - carry + step * Units.YardsToMetres;
        if (_courseMode) UpdateAim(); else UpdateRangeAim();
    }

    // ---- shots -------------------------------------------------------------------------------

    private bool Putter => _hud.ClubCode == "PT";

    private void Demo()
    {
        if (!_courseMode)
        {
            ShowShot(Putter ? Caddie.DemoShot(_hud.Club, 6, Lie.Green, _rng) : RangeShot.Demo(_hud.Club, _rng));
            return;
        }
        if (_online != null) { _hud.Toast("Online, shots come from your launch monitor", UiTheme.Ink); return; }
        if (CourseDemoShot() is { } shot) ShowShot(shot);
    }

    /// <summary>A demo shot sized for the shot set up on the course (null when there's no shot to play).</summary>
    private RangeShot? CourseDemoShot()
    {
        if (_round == null || _round.Holed) return null;
        // On the green, putt at the pace the read calls for (slopes included); elsewhere size the shot to the
        // distance it plays (uphill longer, downhill shorter).
        if (Putter && _round.Lie == Lie.Green) return Caddie.DemoPutt(_round.ReadPutt().Speed, _rng);
        double target = Putter ? _round.DistanceToPin : _round.PlaysLike(_round.DefaultTarget);
        return Caddie.DemoShot(_hud.Club, target, _round.Lie, _rng, _round.StanceAt(_round.Ball, _aimHeading, _hud.LeftHanded));
    }

    // ---- hitting from the screen -------------------------------------------------------------

    /// <summary>The putter's full scale (m of flat roll) for the putt coming up: 100% power rolls this far.</summary>
    private double _puttScale = Caddie.PuttScaleMetres(6);

    /// <summary>Space / HIT SHOT: the shot with the power, shape and spin on screen; next hole once holed out.</summary>
    private void HitPress()
    {
        if (_camera.Flying) { _camera.SkipFlyover(); return; }
        if (_courseMode)
        {
            if (_round == null) return;
            if (_round.Holed) { if (_outcome == null) NextHole(); return; }
            if (_outcome != null || _player.IsPlaying) return;   // wait for the ball to stop
        }
        // With a monitor a real ball is hit, as it always is online (development runs' fake monitor stands in with the swing on screen,
        // and a fake R10 or Square "hits" the shot set up).
        if (HitWithFakeMonitor()) return;
        if (_monitorLive || (_online != null && _courseMode && !_fakeMonitor)) { _hud.Toast("Hit on your launch monitor", UiTheme.Ink); return; }
        var swing = _hud.Shot.Swing;
        var lie = _courseMode && _round != null ? _round.Lie : Lie.Green;
        var shot = Putter ? Caddie.SwingPutt(swing, _puttScale, lie) : Caddie.SwingShot(_hud.Club, swing, _hud.LeftHanded, KnownYards);
        if (_online != null && _courseMode) shot = shot with { Source = ShotSource.Monitor };
        Log(Putter ? $"Putt: {swing.Power * 100:0}% ({DescribePower(swing.Power)})"
                   : $"Hit: {swing.Power * 100:0}% power ({DescribePower(swing.Power)}), {swing.Shape.ToString().ToLowerInvariant()}, {swing.Spin.ToString().ToLowerInvariant()} spin");
        ShowShot(shot);
    }

    /// <summary>
    /// Sets the power for the shot coming up: on a course, what carries the ball to the target from this lie (putts:
    /// what rolls it to the hole, with a putt scale that puts that at about 70%); on the range, the power is left as
    /// the player set it (putts there are scaled for 6 m). With a launch monitor a full shot is aimed at the club's
    /// carry instead (<see cref="AimDistance"/>).
    /// </summary>
    private void SuggestPower()
    {
        if (_outcome != null) return;   // the next shot is set up once this one has finished
        _aimNudge = 0;
        var round = _courseMode ? _round : null;
        _hud.Shot.SetPutting(Putter);
        if (Putter)
        {
            var lie = round?.Lie ?? Lie.Green;
            double need = round == null ? 6
                        : lie == Lie.Green ? Caddie.PuttRollMetres(round.ReadPutt().Speed, lie)
                        : round.DistanceToPin + 0.25;
            _puttScale = Caddie.PuttScaleMetres(need);
            if (round != null) _hud.Shot.SetPower(need / _puttScale);
        }
        else if (round != null && !round.Holed)
        {
            var stance = round.StanceAt(round.Ball, _aimHeading, _hud.LeftHanded);
            _hud.Shot.SetPower(Caddie.PowerFor(_hud.ClubCode, round.PlaysLike(round.DefaultTarget), stance, KnownYards));
        }
        _hud.Shot.ShowPower();
    }

    /// <summary>What a power means for the club in hand: "118 yd carry", or "12 ft roll" for a putt.</summary>
    private string DescribePower(double power)
    {
        if (Putter) return $"{power * _puttScale * Units.MetresToYards * 3:0} ft roll";
        var stance = _courseMode && _round is { Holed: false } r ? r.StanceAt(r.Ball, _aimHeading, _hud.LeftHanded) : null;
        return $"{power * Caddie.CarryFrom(_hud.ClubCode, stance, KnownYards) * Units.MetresToYards:0} yd carry";
    }

    /// <summary>Every shot (from the monitor, the screen, a demo or a replay) comes through here.</summary>
    private void ShowShot(RangeShot s, bool replay = false)
    {
        if (_loading) { Log("Shot ignored: a course is loading."); return; }
        // Online every course shot goes to the session: the host's PC plays it if it's this PC's turn.
        if (_online != null && _courseMode)
        {
            if (s.Source == ShotSource.Monitor && !replay) _online.MonitorShot(s, _hud.ClubCode, _aimHeading, Putter);
            else _hud.Toast("Online, shots come from your launch monitor", UiTheme.Ink);
            return;
        }
        _camera.SkipFlyover();
        EndLandingView();
        _hud.Shot.ResetShape();   // a draw, fade or spin choice is for one shot
        if (_courseMode) { PlayCourseShot(s, replay); return; }
        FlushRangeResult();
        var f = Putter ? BallFlight.Putt(s.BallMph, s.DirectionDeg) : s.Fly();
        // Along the aim (the numbers, offline included, stay relative to the aim line).
        f = f with { Points = HoleRound.Place(f.Points, new P2(0, 0), _rangeAim) };
        Sound(SoundSynth.ImpactFor(_hud.ClubCode));
        _player.Play(f);
        CountShot(s, f, replay);
        RaiseShotFinished(new ShotFinished(DateTime.Now, RangeGolfer, _hud.ClubCode, s, f, replay));
        string label = _shotCount + (replay ? "R" : s.IsDemo ? "*" : "");
        // The numbers wait until the ball has stopped.
        _rangeResult = (label, _hud.ClubCode, s, f);
        UpdateRangeAim();
        Remember(new LastShot(s, _hud.ClubCode, null, 0, null, 0), label, f);
    }

    /// <summary>The range shot in the air, shown once the ball stops (null = none).</summary>
    private (string Label, string Club, RangeShot Shot, FlightResult Flight)? _rangeResult;

    /// <summary>Shows the range shot's numbers and history row (once the ball has stopped, or a new shot comes in first).</summary>
    private void FlushRangeResult()
    {
        if (_rangeResult is not { } r) return;
        _rangeResult = null;
        _hud.AddRangeShot(r.Label, r.Club, r.Shot, r.Flight);
        UpdateRangeAim();
    }

    /// <summary>No replay across a switch between the range and a course (it would play somewhere else).</summary>
    private void ForgetLastShot()
    {
        _last = null;
        _tuner.SetReplay(null);
    }

    /// <summary>Keeps the shot for Replay and adds its numbers to the tuner's comparison.</summary>
    private void Remember(LastShot last, string label, FlightResult f)
    {
        _last = last;
        var s = last.Shot;
        _tuner.SetReplay($"{last.Club}, {s.BallMph:0.0} mph, {s.LaunchDeg:0.0}° launch, {s.SpinRpm:0} rpm");
        _tuner.AddResult(new TunerResult(label, last.Club, f.LandingIndex == 0, f.CarryYards, f.TotalYards, f.ApexYards, f.DescentDeg,
                                         f.LateralYards, ActivePhysics.Tuning));
    }

    /// <summary>
    /// Plays the last shot again with the current physics: on the range from the tee; on a course from where it was
    /// struck, with the round put back as it was (so the replay replaces it rather than counting another stroke).
    /// </summary>
    private void Replay()
    {
        if (_online != null) return;   // no replays online
        var last = _last;
        if (last == null) { Log("Nothing to replay yet: hit a shot first."); return; }
        _hud.SetClub(last.Club);
        if (last.Match == null) { ShowShot(last.Shot, replay: true); return; }

        if (!_courseMode || last.Match != _match) { ForgetLastShot(); return; }   // a round that's been replaced
        var match = _match;
        var before = last.Before!;
        _outcome = null;
        if (match.HoleIndex != before.HoleIndex) StartHole(before.HoleIndex);
        match.TakeTurn(last.Player);   // the player who hit it, whoever is up now
        var round = match.Current;
        round.Restore(before);
        _hud.HideResult();
        _hud.ShowScorecard(false);
        _player.PlaceBall(BallAt(before.Ball));
        _aimHeading = last.Heading;
        UpdateAim();
        ShowShot(last.Shot, replay: true);
    }

    private void CountShot(RangeShot s, FlightResult f, bool replay = false)
    {
        _shotCount++;
        _hud.SetStat("Shots", _shotCount.ToString());
        Log($"{(replay ? "Replay" : s.IsDemo ? "Demo shot" : "Shot")} {_shotCount} ({_hud.ClubCode}): {s.BallMph:0.0} mph, {s.LaunchDeg:0.0}° launch, {s.SpinRpm:0} rpm → carry {f.CarryYards:0} yd, total {f.TotalYards:0} yd");
    }

    private void ClearRange()
    {
        _rangeResult = null;
        _player.Clear();
        _hud.ClearShots();
        _shotCount = 0;
        _hud.SetStat("Shots", "0");
        UpdateRangeAim();
    }

    /// <summary>The camera is looking at where the shot set up lands (until it's switched off or the ball is hit).</summary>
    private bool _landingView;

    /// <summary>Landing view on or off: before a shot only, and not for a putt.</summary>
    private void ToggleLandingView()
    {
        bool waiting = _courseMode ? _outcome != null || _round is not { Holed: false } : _player.IsPlaying || _rangeResult != null;
        if (!_landingView && (waiting || Putter || _camera.Flying))
        {
            _hud.SetLandingView(false);
            if (Putter && !waiting) _hud.Toast("Zoom is for full shots, not putts", UiTheme.Ink);
            return;
        }
        _landingView = !_landingView;
        _hud.SetLandingView(_landingView);
        if (_courseMode) UpdateAim(); else UpdateRangeAim();
    }

    /// <summary>Back behind the ball (a shot, a new hole, the range or a course).</summary>
    private void EndLandingView()
    {
        _landingView = false;
        _hud.SetLandingView(false);
        _camera.SetLandingView(null, Vector3.Forward);
    }

    /// <summary>Points the landing view at <paramref name="at"/> while it's on (and the shot isn't a putt), else back home.</summary>
    private void AimLandingView(Vector3 at, Vector3 dir)
    {
        if (_landingView && Putter) EndLandingView();   // switched to the putter
        _camera.SetLandingView(_landingView ? at : null, dir);
    }

    private void ToggleCamera()
    {
        _camera.Follow = !_camera.Follow;
        _hud.Toast(_camera.Follow ? "Camera: follows the ball" : "Camera: stays behind the ball", UiTheme.Ink);
        SaveSettings(_settings with { CameraFollow = _camera.Follow });
    }
}
