using System.Collections.Concurrent;
using Godot;
using GrimesGolf.Bluetooth;
using GrimesGolf.Core;
using GrimesGolf.Core.Audio;
using GrimesGolf.Core.Course;
using GrimesGolf.Core.Square;
using GrimesGolf.Core.Storage;
using GrimesGolf.Online.Session;
using GrimesGolf.Physics;

namespace GrimesGolf.Game;

/// <summary>Course play: loading a course, holes, playing and finishing shots, the shot plan and aiming.</summary>
public partial class Main
{
    // Course mode
    private bool _courseMode;
    private int _courseIndex;
    private Course? _courseDef;
    /// <summary>The round being played: 1-4 players, each with their own ball.</summary>
    private Match? _match;
    /// <summary>How the round is played: the game, the holes and the conditions (Game Setup).</summary>
    private GameSetup _game = GameSetup.Default;
    /// <summary>The player whose shot it is (their round: ball, lie, strokes).</summary>
    private HoleRound? _round => _match?.Current;
    private CourseWorld? _course;
    private double _aimHeading;
    private bool _flagWhenPutting;
    /// <summary>Green-reading aids on the green being played (kept from hole to hole).</summary>
    private bool _heatMap, _grid;
    /// <summary>The last course shot, applied to the HUD/ball once its animation finishes (so the result isn't spoiled).</summary>
    private ShotOutcome? _outcome;
    private DateTime _roundStarted = DateTime.Now;
    /// <summary>The last course shot as saved, to mark if it's taken back with a mulligan.</summary>
    private ShotRecord? _lastRecord;

    // ---- course play -------------------------------------------------------------------------

    /// <summary>The driving range: the course (if one was being played) is put away; its round stays to resume.</summary>
    private void EnterRange()
    {
        bool wasCourse = _courseMode;
        _camera.SkipFlyover();
        EndLandingView();
        _courseMode = false;
        _autoNextIn = -1;
        // The range plays the physics as tuned, in fine weather.
        ActivePhysics.SetConditions(null);
        _weather?.Set(Weather.Mild);
        if (wasCourse) ForgetLastShot();
        _hud.SetCourseMode(false);
        _hud.SetWhere("Driving range");
        UpdateInsets();
        _rangeWorld.Visible = true;
        _player.GroundTop = 0.115f;   // the range mat
        _camera.GroundAt = null;
        _course?.QueueFree();
        _course = null;
        _outcome = null;
        _player.CupAt = null;
        _player.LieAt = null;
        _player.SetOtherBalls(Array.Empty<ShotPlayer.OtherBall>());
        _player.Clear();
        _rangeResult = null;
        FrameMinimap(null);
        ShowRangePlayer();
        SuggestPower();
        UpdateRangeAim(snap: true);
    }

    /// <summary>Course play, carrying on where the loaded round (<see cref="LoadCourse"/>) stands.</summary>
    private void EnterCourse()
    {
        if (_match == null || _courseDef == null) return;
        if (!_courseMode) ForgetLastShot();
        _courseMode = true;
        ApplyConditions();
        _hud.SetCourseMode(true);
        _hud.SetWhere(_courseDef.Name);
        _hud.SetCourseLogo(Brand.CourseLogo(_courseDef.Name));
        UpdateInsets();
        _rangeWorld.Visible = false;
        StartHole(_match.HoleIndex, fresh: false);
        Log("Course: ←/→ aim (Shift = 2°) or click the map, ↑/↓ power, Z look at the landing spot, Space hit, D random demo shot, S scorecard, U mulligan, " +
            "F flag in/out when putting, H green heat map, G green grid, M or Esc menu. Gamepad and numpad work too.");
    }

    private string CourseName => CourseLibrary.All[_courseIndex].Name;

    /// <summary>The course's height above sea level (m), from its elevation data (sea level without).</summary>
    private static double CourseAltitudeMetres(Course c) => c.Terrain?.Datum ?? 0;

    /// <summary>The round's altitude, weather, fairways and greens on top of the physics tuning, and the weather's look.</summary>
    private void ApplyConditions()
    {
        var game = _game;
        double altitude = _courseDef == null ? 0 : CourseAltitudeMetres(_courseDef);
        ActivePhysics.SetConditions(t => game.ApplyTo(t, altitude));
        _weather?.Set(game.Weather);
        Log($"Conditions: {game.Summary(_match?.Players.Count ?? 1)}; air at {game.AltitudeMetres(altitude) / Units.FeetToMetres:#,0} ft.");
    }

    /// <summary>
    /// Loads the selected course and gets a round ready on it (false if it couldn't be loaded). With
    /// <paramref name="resume"/>, the one left unfinished on this course if there's a save (with its players);
    /// otherwise (or with no save) a new one with the players from the settings.
    /// </summary>
    private bool LoadCourse(bool resume)
    {
        if (_courseIndex >= CourseLibrary.All.Count) { Log("There's no course to load: the courses folder is missing or empty."); return false; }
        var entry = CourseLibrary.All[_courseIndex];
        try
        {
            _courseDef = entry.Load();
        }
        catch (Exception ex)
        {
            Log($"Couldn't load {entry.Name}: {ex.Message}");
            return false;
        }
        _course?.QueueFree();
        _course = null;
        _hud.SetCredit(_courseDef.Credit);
        Log($"{_courseDef.Name}: {_courseDef.Holes.Count} holes, par {_courseDef.Holes.Sum(h => h.Par)}, {_courseDef.Holes.Sum(h => h.LengthYards):0} yd.");
        foreach (var note in _courseDef.Notes) Log("  " + note);

        if (resume && _data?.LoadCurrentRound() is { } saved && saved.Course == entry.Name && saved.HolesOf(_courseDef.Holes) is var holes && saved.Fits(holes))
        {
            _game = saved.Game;
            _match = new Match(holes, _courseDef.Terrain, saved.Golfers, saved.Rules, _game.Type) { Trees = _courseDef.Obstacles };
            saved.ApplyTo(_match);
            _roundStarted = saved.Started;
            int played = saved.Players[0].Scores.Count(s => s != null);
            Log($"Carrying on the round started {saved.Started:ddd HH:mm} ({string.Join(", ", saved.Players.Select(p => p.Name))}): " +
                $"hole {_match.Hole.Number}, {played} hole{(played == 1 ? "" : "s")} played.");
        }
        else NewMatchState();
        return true;
    }

    /// <summary>A new round on the loaded course with the players and house rules from the settings, set up as <see cref="_game"/>.</summary>
    private void NewMatchState()
    {
        _match = new Match(_game.Pick(_courseDef!.Holes), _courseDef.Terrain, _settings.Golfers, _settings.Rules, _game.Type) { Trees = _courseDef.Obstacles };
        _match.StartHole(0);
        _roundStarted = DateTime.Now;
        _lastRecord = null;
        Log($"New round ({_game.Summary(_match.Players.Count)}): {string.Join(", ", _match.Players.Select(p => p.Name + (p.LeftHanded ? " (LH)" : "")))}. " +
            $"House rules: {_settings.Rules.Summary()}.");
    }

    /// <summary>
    /// Abandons the round in progress and starts again from the first tee, with the players and rules in the
    /// settings (away from the course: the round is dropped, and New Game starts the next one).
    /// </summary>
    private void NewRound()
    {
        if (_outcome != null || _online != null) return;
        if (!_courseMode || _courseDef == null)
        {
            _match = null;
            _data?.ClearCurrentRound();
            return;
        }
        NewMatchState();
        ForgetLastShot();
        StartHole(0, fresh: false);
    }

    /// <summary>New Game: a new round on course <paramref name="index"/> with the players in the settings, set up as <paramref name="setup"/>.</summary>
    private async void StartNewGame(int index, GameSetup setup)
    {
        if (_loading || index >= CourseLibrary.All.Count) return;
        int previous = _courseIndex;
        var previousGame = _game;
        _courseIndex = index;
        _game = setup;
        await WithLoadingScreen(index, () =>
        {
            if (!LoadCourse(resume: false))
            {
                _courseIndex = previous;
                _game = previousGame;
                _hud.Toast("Couldn't load that course (see the log)", UiTheme.Danger);
                return;
            }
            SaveSettings(_settings with { Course = CourseName, Game = setup });
            _hud.ShowMainMenu(false);
            EnterCourse();
        });
    }

    /// <summary>A course is being loaded behind the loading screen: input waits until it's done.</summary>
    private bool _loading;

    /// <summary>
    /// Puts the loading screen up for course <paramref name="index"/>, reads its map and elevation on a worker thread
    /// (so the screen stays live), then runs <paramref name="build"/> (the round and the 3D world, which must be made
    /// on the main thread) and fades the screen away.
    /// </summary>
    private async Task WithLoadingScreen(int index, Action build)
    {
        var entry = CourseLibrary.All[index];
        _loading = true;
        _hud.Loading.Open(entry.Name, Brand.CourseLogo(entry.Name.Split(" (")[0]), "Loading the course map and elevation…");
        try
        {
            await NextFrames(2);   // the screen is drawn before the work starts
            try { await Task.Run(entry.Load); }
            catch (Exception) { /* LoadCourse tries again and reports it */ }
            _hud.Loading.SetStep("Building the course…");
            await NextFrames(2);
            build();
            await NextFrames(1);   // the first frame of the course is drawn under the screen
        }
        finally
        {
            _loading = false;
            _hud.Loading.Close();
        }
    }

    private async Task NextFrames(int frames)
    {
        for (int i = 0; i < frames; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    /// <summary>
    /// Players added or removed during the round (Add / Remove Players): players kept carry on, new ones start on
    /// this hole's tee. The settings take the new line-up too, for the next round.
    /// </summary>
    private void RegroupPlayers(IReadOnlyList<Golfer> golfers, IReadOnlyList<int?> from)
    {
        var match = _match;
        if (match == null || _outcome != null || _online != null) return;
        var named = golfers.Select((g, i) => string.IsNullOrWhiteSpace(g.Name) ? g with { Name = $"Player {i + 1}" } : g).ToList();
        _match = match.Regroup(named, from);
        _match.SetWind(match.Wind);
        SaveSettingsAlways(_settings with { Players = named });
        _lastRecord = null;
        ForgetLastShot();
        _hud.HideResult();
        _player.PlaceBall(RestingBall(_match.Current));
        PrepareNextShot(snapCamera: true);
        int joined = from.Count(f => f == null), left = match.Players.Count - from.Count(f => f != null);
        _hud.Toast(joined + left == 0 ? "Players updated"
                   : string.Join(", ", new[] { joined > 0 ? $"{joined} joined" : "", left > 0 ? $"{left} left" : "" }.Where(s => s.Length > 0)), UiTheme.Accent);
        Log($"Players now: {string.Join(", ", named.Select(g => g.Name + (g.LeftHanded ? " (LH)" : "")))}.");
    }

    /// <summary>Shows hole <paramref name="index"/>: everyone to its tee if <paramref name="fresh"/>, otherwise as the round left it.</summary>
    private void StartHole(int index, bool fresh = true)
    {
        var match = _match!;
        var course = _courseDef!;
        if (fresh) match.StartHole(index);
        ApplyHoleWind();
        var round = match.Current;
        _outcome = null;
        _autoNextIn = -1;
        _hud.SetNextHoleCountdown(null);
        _camera.SkipFlyover();
        EndLandingView();
        // A mapped course is built once and shared by all its holes; test holes are each their own world.
        if (_course != null && course.SharedLayout && _course.Course == course) _course.ShowHole(round.Hole);
        else
        {
            _course?.QueueFree();
            _course = new CourseWorld(course, round.Hole);
            AddChild(_course);
        }
        SetGreenOverlay(_heatMap, _grid);
        // The gimme circle round the hole, and auto-putting's second circle (twice as wide).
        var rules = match.Rules;
        _course?.SetGimmeCircles(rules.GimmeMetres, rules.AutoPutt ? rules.SecondCircleMetres : 0);
        _course?.SetFlagWind(match.Wind.IsCalm ? null : match.Wind.FromHeading + Math.PI);
        // With real ground the ball sits on it; on a flat course it sits on the raised green layer.
        var terrain = course.Terrain;
        _player.GroundTop = terrain != null ? 0f : Scenery.GreenTop;
        _camera.GroundAt = terrain == null ? null : v => (float)terrain.HeightAt(new P2(-v.Z, v.X));
        _player.CupAt = BallAt(round.Hole.Pin);
        _player.LieAt = v => round.Hole.LieAt(new P2(-v.Z, v.X));
        _player.Clear(RestingBall(round));
        FrameMinimap(round.Hole);
        _hud.HideResult();
        _hud.ShowScorecard(false);
        PrepareNextShot(snapCamera: true);
        Log($"{HoleTitle(round.Hole)}");
        // Nobody has hit yet: fly down the hole first (not in development runs that hit shots by themselves).
        if (_settings.HoleFlyover && match.Rounds.All(r => r.Strokes == 0 && !r.Holed) && _demoShotsLeft == 0 && _hitPresses == null)
            StartFlyover(round.Hole);
    }

    /// <summary>The camera flies down the hole (along its route, or tee, landing area and pin), with a line saying how to skip it.</summary>
    private void StartFlyover(Hole hole)
    {
        var route = hole.Route is { Count: >= 2 } r ? r : new[] { hole.Tee, hole.TeeAim, hole.Pin };
        _camera.Flyover(route.Select(p => BallAt(p)).ToList());
        _hud.SetHint($"Hole {hole.Number}  ·  Par {hole.Par}  ·  {hole.LengthYards:0} yd      Space to skip");
    }

    // ---- next hole by itself -----------------------------------------------------------------

    /// <summary>Seconds until the next hole starts by itself once everyone has finished this one (negative = not counting).</summary>
    private double _autoNextIn = -1;
    private const double AutoNextSeconds = 5;

    private string NextHoleNote => _online is { IsHost: false } ? "On to the next hole in a moment."
                                 : $"Next hole in {AutoNextSeconds:0} seconds (Space to go now).";

    /// <summary>
    /// Counts down to the next hole (shown on the NEXT HOLE button); paused while a menu or the settings are open,
    /// and dropped if the hole is no longer finished (a mulligan) or play leaves the course.
    /// </summary>
    private void CountDownToNextHole(double delta)
    {
        if (_autoNextIn < 0) return;
        if (!_courseMode || _match is not { HoleComplete: true, RoundComplete: false } || _outcome != null)
        {
            _autoNextIn = -1;
            _hud.SetNextHoleCountdown(null);
            return;
        }
        // Online the host's PC moves everyone on, menus or not.
        if ((_hud.ModalOpen || _hud.SettingsOpen) && _online == null) return;
        _autoNextIn -= delta;
        if (_autoNextIn < 0) { _hud.SetNextHoleCountdown(null); NextHole(); }
        else _hud.SetNextHoleCountdown((int)Math.Ceiling(_autoNextIn));
    }

    private void NextHole()
    {
        var match = _match;
        if (match == null || _outcome != null || !_courseMode) return;
        if (_online is { IsHost: false }) { _hud.Toast("The host's PC moves everyone to the next hole", UiTheme.Ink); return; }
        if (match.RoundComplete) { if (_online != null) return; NewMatchState(); StartHole(0, fresh: false); return; }
        StartHole((match.HoleIndex + 1) % match.Holes.Count);
        OnlineHost?.HoleStarted();
    }

    /// <summary>Back to the hole before (everyone to its tee; the score there is played again).</summary>
    private void PreviousHole()
    {
        var match = _match;
        if (match == null || _outcome != null || !_courseMode || _online != null) return;
        StartHole((match.HoleIndex - 1 + match.Holes.Count) % match.Holes.Count);
    }

    /// <summary>
    /// Plays a course shot and starts animating it; null if it didn't count. Online, <paramref name="online"/> gives
    /// the player, club, aim and putt (the host's PC plays a friend's shot with them), and on a friend's PC the
    /// outcome too: the match is put as the host's snapshot at impact instead of the shot being worked out here.
    /// </summary>
    private ShotOutcome? PlayCourseShot(RangeShot s, bool replay = false, OnlineShot? online = null)
    {
        var match = _match;
        if (match == null) return null;
        if (_outcome != null) FinishShot();       // a new shot arrived before the last one finished animating
        if (online != null) match.TakeTurn(online.Player);
        var round = match.Current;
        if (round.Holed && online?.Result == null) { Log("Hole finished. Press Space (NEXT HOLE) for the next hole."); return null; }

        int player = match.Turn;
        var golfer = match.Golfer;
        int strokesBefore = round.Strokes;
        var before = round.Save();
        double heading = online?.Heading ?? _aimHeading;
        string club = online?.Club ?? _hud.ClubCode;
        bool putt = online?.Putt ?? Putter;
        var stance = round.StanceAt(round.Ball, heading, golfer.LeftHanded);
        double toPin = round.DistanceToPin, playsLike = round.PlaysLike(round.DefaultTarget);
        ShotOutcome o;
        if (online?.Result is { } remote)
        {
            match.Restore(remote.Snapshot);
            o = remote.Outcome.ToOutcome();
        }
        else o = match.Play(s, putt, heading, club);
        if (round.Strokes == strokesBefore && !o.HoleOver)
        {
            // The engine couldn't simulate it (impossible numbers from the monitor): nothing happened.
            Log(o.Message);
            _hud.ShowResult("Shot not counted", Array.Empty<Stat>(), o.Message);
            return null;
        }
        _outcome = o;
        Sound(SoundSynth.ImpactFor(club));
        _player.Play(o.Flight);
        _course?.SetAim(round.Ball, heading, 0, visible: false);
        CountShot(s, o.Flight, replay);
        // Nothing about where it went (or the next shot) until the ball has stopped: FinishShot shows it all.
        _hud.HideMapMarkers();
        _hud.HideResult();
        _hud.LockHit();
        RaiseShotFinished(new ShotFinished(DateTime.Now, golfer, club, s, o.Flight, replay)
        {
            Course = CourseName, Hole = round.Hole, Stroke = strokesBefore + 1, Lie = before.Lie, Stance = stance,
            ToPinYards = toPin * Units.MetresToYards, PlaysLikeYards = playsLike * Units.MetresToYards, Outcome = o,
            Match = Core.Course.ShotFinished.Standings(match),
        });
        SaveRound();   // where it finished, in case the app doesn't make it to the end of the animation
        // No replays online (or after a scramble's pick, which moved the whole team: replaying one player's shot would put them out of step).
        if (online != null || match.LastPick != null) { ForgetLastShot(); return o; }
        Remember(new LastShot(s, club, match, player, before, heading),
                 $"{(match.Players.Count > 1 && golfer.Name.Length > 0 ? golfer.Name[..1] : "")}{round.Hole.Number}.{strokesBefore + 1}{(replay ? "R" : "")}", o.Flight);
        return o;
    }

    /// <summary>
    /// Applies the last shot once the ball has stopped: penalties, holing out, the result card; then whoever is
    /// farthest from the hole is up (the monitor switches to their hand and club).
    /// </summary>
    private void FinishShot()
    {
        var o = _outcome!;
        _outcome = null;
        var match = _match!;
        var round = match.Current;
        string who = match.Players.Count > 1 ? match.Golfer.Name + ": " : "";
        var pick = match.LastPick;
        _player.HideCarryLabel();
        if (o.Penalty > 0 || (!o.Holed && o.NextBall != o.Rest)) _player.PlaceBall(BallAt(o.NextBall));   // penalty or cart-path relief
        // Holed (or tapped in): the ball rests at the bottom of the cup, against the flagstick.
        else if (o.Holed) _player.PlaceBall(CupBall(round.Hole));
        if (o.Holed) CelebrateScore(match, round, o);
        string headline = who + o.Message, note = o.LieText;
        if (pick is { Message.Length: > 0 } p && !o.HoleOver) note = note.Length == 0 ? p.Message : $"{p.Message}   ·   {note}";
        if (match.HoleComplete && match.RoundComplete)
        {
            note = $"Round complete: {match.Summary()}. NEXT HOLE plays again; Menu > End Game to finish.";
        }
        else if (match.HoleComplete && match.Type == GameType.MatchPlay)
        {
            note = $"{HoleResult(match)}  ·  {match.MatchText()}.  {NextHoleNote}";
            if (_online is not { IsHost: false }) _autoNextIn = AutoNextSeconds;
        }
        else if (match.HoleComplete)
        {
            note = NextHoleNote;
            if (_online is not { IsHost: false }) _autoNextIn = AutoNextSeconds;
        }
        else if (match.CanMulligan && o.Penalty > 0) note = $"Mulligan? Press U ({round.MulligansLeft} left).";

        var stats = new List<Stat>();
        if (o.Flight.LandingIndex == 0)
        {
            stats.Add(Distance("Rolled", o.Flight.TotalYards * Units.YardsToMetres, short_: true));
            if (!o.Holed && o.Penalty == 0) stats.Add(Distance("To the hole", o.NextBall.DistanceTo(round.Hole.Pin), short_: true));
        }
        else
        {
            stats.Add(new Stat("Carry", $"{o.Flight.CarryYards:0}", "yd"));
            stats.Add(new Stat("Total", $"{o.Flight.TotalYards:0}", "yd"));
            stats.Add(new Stat("Offline", RangeShot.Side(o.Flight.LateralYards, "0"), "yd"));
            if (!o.Holed && o.Penalty == 0) stats.Add(Distance("To the pin", o.NextBall.DistanceTo(round.Hole.Pin), short_: round.Lie == Lie.Green));
        }

        // Next player up (the same one if they're still farthest away).
        int hitter = match.Turn;
        match.AdvanceTurn();
        if (match.Turn != hitter || pick != null)
        {
            _player.PlaceBall(RestingBall(match.Current));
            string up = $"{match.Golfer.Name} to play" + (match.Current.Strokes == 0 || match.Type == GameType.Scramble ? "" : ", farthest from the hole");
            note = note.Length == 0 ? up : $"{note}   ·   {up}";
        }
        _hud.ShowResult(headline, stats, note);
        Log(headline + (note.Length > 0 ? "  " + note : ""));
        PrepareNextShot(snapCamera: match.Turn != hitter);
        _online?.ShotShown();   // after the turn has moved on here: the host's next turn is taken from this match
        if (!match.HoleComplete) return;
        // The scorecard comes up once the score announcement has had its moment (if nothing has moved on since).
        if (!o.Holed) { _hud.ShowScorecard(true); return; }
        int holeIndex = match.HoleIndex;
        GetTree().CreateTimer(2.6).Timeout += () =>
        {
            if (_courseMode && _match == match && match.HoleIndex == holeIndex && match.HoleComplete && _outcome == null) _hud.ShowScorecard(true);
        };
    }

    /// <summary>
    /// House rules: takes the last shot of the round back (stroke, any penalty) and puts that player's ball where it
    /// was played from; they play again. Only once the ball has stopped, before anyone else hits, and not after holing out.
    /// </summary>
    private void Mulligan()
    {
        // Online the host's PC decides (only the PC whose player hit it can ask).
        if (_online != null) { if (_courseMode && _outcome == null) _online.RequestMulligan(); return; }
        TryMulligan();
    }

    /// <summary>The mulligan itself; false if there's nothing to take back.</summary>
    private bool TryMulligan()
    {
        var match = _match;
        if (!_courseMode || match == null || _outcome != null) return false;
        if (!match.CanMulligan)
        {
            var r = match.LastToPlay is { } p ? match.Rounds[p] : match.Current;
            Log(match.Rules.Mulligans == 0 ? "No mulligans in the house rules." : r.MulligansLeft == 0 ? "No mulligans left this round." : "Nothing to take back.");
            return false;
        }
        match.Mulligan();
        var round = match.Current;
        if (_lastRecord != null) _data?.AppendShot(_lastRecord with { Mulligan = true });
        _lastRecord = null;
        ForgetLastShot();   // a replay would restore the round to before the shot that was taken back
        _hud.HideResult();
        _hud.ShowScorecard(false);
        _player.PlaceBall(RestingBall(round));
        PrepareNextShot(snapCamera: true);
        string who = match.Players.Count > 1 ? match.Golfer.Name + ", play" : "Play";
        _hud.Toast($"Mulligan! {who} it again ({round.MulligansLeft} left)", UiTheme.Warn);
        Log($"Mulligan: {who.ToLowerInvariant()} it again from there. {round.MulligansLeft} left this round.");
        return true;
    }

    /// <summary>Saves the round as it stands (and files it once it's finished), to resume after a restart or crash.</summary>
    private void SaveRound()
    {
        // Online rounds never touch the offline save: the host keeps online-round.json (see Main.Online.cs).
        if (_data == null || _match == null || _online != null) return;
        _data.SaveRound(SavedRound.From(_match, CourseName, _roundStarted, _game));
    }

    /// <summary>A distance in yards, or feet when short (putts, around the green).</summary>
    private static Stat Distance(string caption, double metres, bool short_)
    {
        double yd = metres * Units.MetresToYards;
        return short_ || yd < 20 ? new Stat(caption, $"{yd * 3:0}", "ft") : new Stat(caption, $"{yd:0}", "yd");
    }

    /// <summary>
    /// The player who's up: their hand and suggested club on the monitor, default aim, camera behind their ball, the
    /// other players' balls, hole card, scorecard and shot plan.
    /// </summary>
    private void PrepareNextShot(bool snapCamera = false)
    {
        var match = _match!;
        var round = match.Current;
        var golfer = match.Golfer;
        // On the green the aim follows the read only with auto-aligned putts (house rules); otherwise it's at the hole.
        _aimHeading = round.Lie == Lie.Green && !round.Holed && !match.Rules.AutoAlignPutts
            ? HoleRound.Heading(round.Ball, round.Hole.Pin) : round.DefaultAimHeading();
        string club = round.Holed ? _hud.ClubCode : Caddie.Suggest(round.PlaysLike(round.DefaultTarget), round.Lie, round.StanceAt(round.Ball, _aimHeading, golfer.LeftHanded), KnownYards);
        _hud.SetPlayerClub(club, golfer.LeftHanded);
        SuggestPower();
        UpdateAim(snapCamera);
        ShowOtherBalls();

        var hole = round.Hole;
        bool several = match.Players.Count > 1;
        int side = match.SideOf(match.Turn);
        string stroke = round.PickedUp ? $"Picked up ({round.Strokes})" : round.Holed ? $"In {round.Strokes}" : $"{round.Strokes + 1}";
        string score, scoreNote;
        if (match.Type == GameType.MatchPlay)
        {
            // The match from the side of whoever's up: "2 UP", "AS", "1 DN".
            var status = match.MatchStatus();
            score = match.MatchTextFor(side);
            scoreNote = status.Decided ? "match over" : status.Played == 0 ? "" : $"thru {status.Played}";
        }
        else
        {
            var (_, toPar, thru) = match.Teams ? match.SideStanding(side) : match.Standing(match.Turn);
            score = thru == 0 ? "E" : Hud.ToPar(toPar);
            scoreNote = round.Holed && !round.PickedUp ? HoleRound.ScoreName(round.Strokes, hole.Par) : thru == 0 ? "" : $"thru {thru}";
            if (match.Teams) scoreNote = scoreNote.Length == 0 ? "team" : $"team, {scoreNote}";
        }
        _hud.SetHoleCard(hole.Number, hole.Name, hole.Par, hole.LengthYards, stroke, score, scoreNote);
        _hud.SetActivePlayer(golfer.Name, UiTheme.PlayerColor(match.Turn), several);
        _hud.SetTurn(several && !match.HoleComplete ? golfer.Name : null, UiTheme.PlayerColor(match.Turn), several ? Standings(match) : "");
        _hud.SetLeaderboard(match.Type == GameType.MatchPlay ? match.MatchText() : "Leaderboard", LeaderboardRows(match));
        var mulliganFor = match.LastToPlay is { } lp ? match.Rounds[lp] : round;
        _hud.SetMulligans(match.Rules.Mulligans, mulliganFor.MulligansLeft, match.CanMulligan && _outcome == null);
        _hud.SetScorecard($"{_courseDef!.Name}  ·  {GameSetup.Name(match.Type)}", match.Holes.Select(h => h.Number).ToList(),
            match.Holes.Select(h => h.Par).ToList(), ScorecardRows(match), match.HoleIndex);
        SaveRound();
    }

    /// <summary>The small leaderboard: teams or players, best score first (match play keeps its order, its caption says who's ahead).</summary>
    private static List<LeaderboardRow> LeaderboardRows(Match match)
    {
        static string Thru(int thru) => thru == 0 ? "" : $"thru {thru}";
        var rows = match.Teams
            ? Enumerable.Range(0, match.Sides.Count).Select(s =>
            {
                var (_, toPar, thru) = match.SideStanding(s);
                return (Row: new LeaderboardRow(match.SideName(s), thru == 0 ? "E" : Hud.ToPar(toPar), Thru(thru),
                    UiTheme.PlayerColor(match.Sides[s][0]), match.Sides[s].Contains(match.Turn)), ToPar: thru == 0 ? 0 : toPar);
            })
            : match.Players.Select((g, i) =>
            {
                var (_, toPar, thru) = match.Standing(i);
                return (Row: new LeaderboardRow(g.Name, thru == 0 ? "E" : Hud.ToPar(toPar), Thru(thru), UiTheme.PlayerColor(i), i == match.Turn),
                    ToPar: thru == 0 ? 0 : toPar);
            });
        return (match.Type == GameType.MatchPlay ? rows : rows.OrderBy(r => r.ToPar)).Select(r => r.Row).ToList();
    }

    /// <summary>Everyone's standing under the hole badge: per player, per team, or the match, with this hole so far.</summary>
    private static string Standings(Match match)
    {
        string Now(HoleRound r) => r.Holed ? $", in {r.Strokes}" : r.Strokes > 0 ? $", {r.Strokes} so far" : "";
        if (match.Type == GameType.Scramble && match.Teams)
            return string.Join("  ·  ", Enumerable.Range(0, match.Sides.Count).Select(s =>
            {
                var (_, tp, th) = match.SideStanding(s);
                return $"{match.SideName(s)} {(th == 0 ? "E" : Hud.ToPar(tp))}{Now(match.Rounds[match.Sides[s][0]])}";
            }));
        string players = string.Join("  ·  ", match.Players.Select((g, i) =>
        {
            var (_, tp, th) = match.Standing(i);
            return $"{g.Name} {(th == 0 ? "E" : Hud.ToPar(tp))}{Now(match.Rounds[i])}";
        }));
        if (match.Type == GameType.MatchPlay) return $"{match.MatchText()}  ·  {players}";
        if (match.Teams)
            return string.Join("  ·  ", Enumerable.Range(0, match.Sides.Count).Select(s =>
            {
                var (_, tp, th) = match.SideStanding(s);
                return $"{match.SideName(s)} {(th == 0 ? "E" : Hud.ToPar(tp))}";
            })) + "  ·  " + players;
        return players;
    }

    /// <summary>
    /// The scorecard's rows: a row per player; per team for a scramble (its players share one ball), and a team's
    /// best ball under its players; for match play, a row showing who won each hole.
    /// </summary>
    private static List<ScorecardRow> ScorecardRows(Match match)
    {
        var sides = Enumerable.Range(0, match.Sides.Count).ToList();
        ScorecardRow Team(int s) => new(match.SideName(s), Enumerable.Range(0, match.Holes.Count).Select(i => match.SideScore(s, i)).ToList(),
                                        UiTheme.PlayerColor(match.Sides[s][0])) { Team = true };
        if (match.Type == GameType.Scramble && match.Teams) return sides.Select(Team).ToList();
        var rows = match.Players.Select((g, i) => new ScorecardRow(g.Name, match.Rounds[i].Scores, UiTheme.PlayerColor(i))).ToList();
        if (match.Teams) rows.AddRange(sides.Where(s => match.Sides[s].Count > 1).Select(Team));
        if (match.Type == GameType.MatchPlay)
        {
            string First(int p) => match.Players[p].Name is { Length: > 0 } n ? n[..1] : "?";
            string Initials(int s) => string.Concat(match.Sides[s].Select(First));
            var marks = Enumerable.Range(0, match.Holes.Count).Select(i => match.HoleWinner(i) switch
            {
                null => ((string, Color)?)null,
                -1 => ("½", UiTheme.Muted),
                var w => (Initials(w.Value), UiTheme.PlayerColor(match.Sides[w.Value][0])),
            }).ToList();
            rows.Add(new ScorecardRow("Match", match.Holes.Select(_ => (int?)null).ToList(), UiTheme.Ink) { Marks = marks, TotalText = match.MatchText() });
        }
        return rows;
    }

    /// <summary>Match play: "Ann wins the hole", "Hole halved".</summary>
    private static string HoleResult(Match match) => match.HoleWinner(match.HoleIndex) switch
    {
        int w and >= 0 => $"{match.SideName(w)} {(match.Sides[w].Count > 1 ? "win" : "wins")} the hole",
        _ => "Hole halved",
    };

    /// <summary>The other players' balls on the course, in their colours.</summary>
    private void ShowOtherBalls()
    {
        var match = _match;
        if (match == null || !_courseMode) return;
        _player.SetOtherBalls(Enumerable.Range(0, match.Players.Count)
            // Not holed, and not sitting where the current ball is (everyone on the same tee).
            .Where(i => i != match.Turn && !match.Rounds[i].Holed && match.Rounds[i].Ball.DistanceTo(match.Current.Ball) > 1.5)
            .Select(i => new ShotPlayer.OtherBall(BallAt(match.Rounds[i].Ball), UiTheme.PlayerColor(i), match.Players[i].Name))
            .ToList());
    }

    /// <summary>"153 yds", or "18 ft" on and around the green.</summary>
    private static string PinDistance(double metres, bool green)
    {
        double yd = metres * Units.MetresToYards;
        return green || yd < 20 ? $"{yd * 3:0} ft" : $"{yd:0} yds";
    }

    /// <summary>
    /// Everything the player needs for the next shot: distance to the pin with the rise or fall to it, the lie (a
    /// picture, the slopes and how much distance it costs), or the green read, the club's expected carry from here,
    /// and where the aim is.
    /// </summary>
    private void RefreshPlan()
    {
        var round = _round;
        if (round == null || !_courseMode || _outcome != null) return;   // the plan for the next shot waits until this one has finished
        var pin = round.Hole.Pin;
        bool green = round.Lie == Lie.Green, putting = Putter;
        double toPinM = round.DistanceToPin, rise = round.GroundAt(pin) - round.GroundAt(round.Ball);
        string toPinText = round.PickedUp ? "Picked up" : round.Holed ? "Holed" : PinDistance(toPinM, green);
        string elevation = "", playsLike = "";
        if (!round.Holed && round.Terrain != null)
        {
            double feet = rise * 3.281;
            if (green) elevation = Math.Abs(feet) < 1.0 / 12 ? "" : Math.Abs(feet) < 1 ? $"{(rise > 0 ? "up" : "down")} {Math.Abs(feet) * 12:0}\"" : $"{(rise > 0 ? "up" : "down")} {Math.Abs(feet):0}'";
            else if (Math.Abs(feet) >= 1) elevation = $"{(rise > 0 ? "up" : "down")} {Math.Abs(feet):0}'";
        }
        // What it plays, with the rise and the wind, when that's a couple of yards or more off the distance.
        if (!round.Holed && !green && !putting && Math.Abs(round.PlaysLike(pin) - toPinM) * Units.MetresToYards >= 2)
            playsLike = $"Plays {round.PlaysLike(pin) * Units.MetresToYards:0} yds";

        var stance = round.StanceAt(round.Ball, _aimHeading, _hud.LeftHanded);
        var preset = ClubPresets.Find(_hud.ClubCode) ?? ClubPresets.All[ClubPresets.IndexOf("I7")];
        var typical = LaunchConditions.FromDisplayUnits(preset.BallMph, preset.LaunchDeg, 0, preset.SpinRpm, 0);
        string lieEffect = putting ? "" : ActivePhysics.LieModel.Apply(typical, stance, preset.LoftDeg).Summary;
        string lieNote = round.Holed ? "" : putting ? (green ? "" : "Putting from off the green") : lieEffect.Length > 0 ? lieEffect : "Clean lie";
        // Shot penalty: what the lie and stance take off the club's carry.
        double clean = Caddie.Carry(preset.Code, KnownYards), fromHere = Caddie.CarryFrom(preset.Code, stance, KnownYards);
        double penalty = putting || round.Holed || clean <= 0 ? 0 : Math.Max(0, 1 - fromHere / clean);

        string read = "", readNote = "";
        if (green && !round.Holed)
        {
            double straight = HoleRound.Heading(round.Ball, pin);
            double offset = Math.Sin(Math.IEEERemainder(round.ReadPutt().Heading - straight, 2 * Math.PI)) * toPinM * 39.37;
            read = Math.Abs(offset) < 2 ? "Straight" : Math.Abs(offset) < 24 ? $"{Math.Abs(offset):0} in {(offset > 0 ? "right" : "left")}"
                 : $"{Math.Abs(offset) / 12:0.#} ft {(offset > 0 ? "right" : "left")}";
            double inches = rise * 39.37;
            readNote = (Math.Abs(offset) < 2 ? "" : "Aim outside the hole. ") + (Math.Abs(inches) < 1 ? "Level putt" : $"{Math.Abs(inches):0} in {(inches > 0 ? "uphill" : "downhill")}");
        }

        string clubNote;
        if (putting) clubNote = green ? "" : "Rolling it from off the green";
        else
        {
            double carry = fromHere * Units.MetresToYards, cleanYd = clean * Units.MetresToYards;
            clubNote = Math.Abs(carry - cleanYd) >= 3 ? $"Carry ≈ {carry:0} yd from here ({cleanYd:0} clean)" : $"Carry ≈ {carry:0} yd";
        }

        // Aim relative to the target in play: the landing area off the tee of a longer hole, otherwise the pin.
        var target = round.DefaultTarget;
        string targetName = target == pin ? (green || putting ? "the hole" : "the pin") : "the target";
        double off = Math.IEEERemainder(_aimHeading - HoleRound.Heading(round.Ball, target), 2 * Math.PI);
        double lateral = Math.Sin(off) * round.Ball.DistanceTo(target);
        string aim = round.Holed ? "" : green || putting
            ? (Math.Abs(lateral) < 0.05 ? $"At {targetName}" : $"{Math.Abs(lateral) * 39.37:0} in {(lateral > 0 ? "right" : "left")} of {targetName}")
            : (Math.Abs(lateral) < 0.9 ? $"At {targetName}" : $"{Math.Abs(lateral) * Units.MetresToYards:0} yd {(lateral > 0 ? "right" : "left")} of {targetName}");

        _hud.Shot.ShowPower();
        bool level = round.Lie == Lie.Tee || round.Holed;
        _hud.SetShotPlan(new ShotPlan(toPinText, elevation, playsLike,
            round.Lie, HoleRound.LieName(round.Lie), LieColor(round.Lie), level ? 0 : stance.UphillDegrees, level ? 0 : stance.BallAboveFeetDegrees,
            penalty, lieNote, green && !round.Holed, read, readNote, clubNote, aim, _flagWhenPutting,
            CanHit: !round.Holed && _outcome == null, HoleDone: round.Holed && _outcome == null));
    }

    private static Color LieColor(Lie lie) => lie switch
    {
        Lie.Tee or Lie.Fairway => new Color(0.45f, 0.78f, 0.36f),
        Lie.Fringe => new Color(0.58f, 0.84f, 0.44f),
        Lie.Green => new Color(0.66f, 0.92f, 0.50f),
        Lie.Rough => new Color(0.70f, 0.74f, 0.36f),
        Lie.DeepRough => new Color(0.88f, 0.64f, 0.30f),
        Lie.Bunker => new Color(0.94f, 0.85f, 0.62f),
        Lie.Water => new Color(0.42f, 0.66f, 0.96f),
        Lie.CartPath => new Color(0.80f, 0.80f, 0.78f),
        _ => UiTheme.Danger,
    };

    /// <summary>Where the ball sits at a course point (on the ground; the player adds the surface layer).</summary>
    private Vector3 BallAt(P2 p) => Scenery.ToGodot(p, (float)(_round?.GroundAt(p) ?? 0));

    /// <summary>Where a player's ball is shown at rest: in the bottom of the cup once they've holed out, else where it lies.</summary>
    private Vector3 RestingBall(HoleRound r) => r.Holed && !r.PickedUp ? CupBall(r.Hole) : BallAt(r.Ball);

    /// <summary>
    /// A holed ball: on the bottom of the cup, resting against the flagstick on the side facing the tee (so it isn't
    /// inside the pole, and shows from the fairway). The player adds the surface layer and the ball's radius.
    /// </summary>
    private Vector3 CupBall(Hole hole)
    {
        var towardTee = hole.Tee - hole.Pin;
        double len = Math.Max(towardTee.Length, 1e-6);
        const double againstPole = 0.012 + ShotPlayer.BallRadius;   // the pole's radius and the ball's
        var at = hole.Pin + towardTee * (againstPole / len);
        return Scenery.ToGodot(at, (float)((_round?.GroundAt(hole.Pin) ?? 0) - HoleRound.CupDepth));
    }

    /// <summary>The wind on the hole being played, from the setting and the round (the same round always has the same wind).</summary>
    private void ApplyHoleWind()
    {
        // Online, a friend's PC has the host's wind in the match already (the seed would differ from PC to PC).
        if (_match == null || _online is { IsHost: false }) return;
        int seed = unchecked((int)(_roundStarted.Ticks / TimeSpan.TicksPerSecond));
        // By the hole's number: a front or back nine gets the same wind on a hole as a full round started then would.
        _match.SetWind(CourseWind.ForHole(_game.Wind, seed, _match.Hole.Number - 1));
    }

    /// <summary>"BIRDIE!" across the screen when a player holes out, in a colour for how good it was.</summary>
    private void CelebrateScore(Match match, HoleRound round, ShotOutcome o)
    {
        int strokes = o.Strokes, par = round.Hole.Par, diff = strokes - par;
        string name = HoleRound.ScoreName(strokes, par).ToUpperInvariant();
        string text = strokes == 1 ? "HOLE IN ONE!" : diff < 0 ? name + "!" : name;
        var color = strokes == 1 || diff <= -2 ? new Color(1.0f, 0.82f, 0.25f)
                  : diff == -1 ? UiTheme.Accent
                  : diff == 0 ? UiTheme.Ink
                  : diff == 1 ? UiTheme.Warn : UiTheme.Danger;
        string who = match.Players.Count == 1 ? "" : match.Type == GameType.Scramble ? match.SideName(match.SideOf(match.Turn)) + "  ·  " : match.Golfer.Name + "  ·  ";
        string sub =$"{who}{strokes} stroke{(strokes == 1 ? "" : "s")} on hole {round.Hole.Number}";
        _hud.CelebrateScore(text, sub, color, sparks: diff < 0 || strokes == 1);
        // The crowd goes wild for under par and claps a par; anything worse gets silence.
        if (diff < 0 || strokes == 1) Sound(SoundEffect.Cheer);
        else if (diff == 0) Sound(SoundEffect.Applause, -4);
    }

    /// <summary>Flag in or out while putting (it's pulled by default once the ball is on the green).</summary>
    private void ToggleFlag()
    {
        _flagWhenPutting = !_flagWhenPutting;
        UpdateAim();
    }

    /// <summary>Turns the green's heat map and rolling-dot grid on or off.</summary>
    private void SetGreenOverlay(bool heatMap, bool grid)
    {
        _heatMap = heatMap;
        _grid = grid;
        _course?.SetGreenOverlay(heatMap, grid);
        _hud.SetGreenOverlays(heatMap, grid);
    }

    private void Aim(double degrees)
    {
        if (_round == null || _round.Holed || _outcome != null) return;
        _aimHeading += degrees * Math.PI / 180;
        UpdateAim();
    }

    /// <summary>Aims at the course point under <paramref name="mapPixel"/> on the minimap (straight down, so the ray's origin is the point).</summary>
    private void AimAtMap(Vector2 mapPixel)
    {
        var at = _mapCamera.ProjectRayOrigin(mapPixel);
        var target = new P2(-at.Z, at.X);
        if (!_courseMode)
        {
            if (target.X < 1) return;   // behind the tee
            _rangeAim = 0;
            AimRange(HoleRound.Heading(new P2(0, 0), target) * 180 / Math.PI);
            return;
        }
        var round = _round;
        if (round == null || round.Holed || _outcome != null) return;
        if (round.Ball.DistanceTo(target) < 0.5) return;
        _aimHeading = HoleRound.Heading(round.Ball, target);
        UpdateAim();
    }

    /// <summary>
    /// Redraws the aim line (along the aim, as far as the power set carries the ball, or rolls it for a putt), moves
    /// the camera behind it and refreshes the plan and the overview map. Not while a shot is under way.
    /// </summary>
    private void UpdateAim(bool snapCamera = false)
    {
        var round = _round;
        if (round == null || _course == null || _outcome != null) return;
        bool putting = Putter;
        double length = Math.Max(AimDistance(round.Holed ? null : round.StanceAt(round.Ball, _aimHeading, _hud.LeftHanded)), 1);
        var aimPoint = round.Ball + new P2(Math.Cos(_aimHeading), Math.Sin(_aimHeading)) * length;
        _course.SetAim(round.Ball, _aimHeading, length, visible: !round.Holed, showRing: !putting, landingView: _landingView);
        _course.ShowGrassAround(round.Ball);

        var dir = new Vector3((float)Math.Sin(_aimHeading), 0, (float)-Math.Cos(_aimHeading));
        _camera.SetHome(BallAt(round.Ball), dir, putting || round.Lie == Lie.Green, snapCamera);
        if (round.Holed) EndLandingView(); else AimLandingView(BallAt(aimPoint), dir);
        // Pull the flag once on the green (unless it's wanted in); it goes back in when the hole is finished.
        bool onGreen = round.Lie == Lie.Green && !round.Holed;
        _course.SetFlagIn(!onGreen || _flagWhenPutting, Scenery.ToGodot(round.Hole.Pin) - Scenery.ToGodot(round.Ball));
        var wind = _match!.Wind;
        // Putts roll in still air: no wind shown on the green.
        if (wind.IsCalm || round.Holed || putting) _hud.SetWind(0, 0, 0, "");
        else _hud.SetWind(wind.SpeedMph, wind.GustMph, Math.IEEERemainder(wind.FromHeading + Math.PI - _aimHeading, 2 * Math.PI),
                          wind.Describe(_aimHeading).Split(", ", 2) is { Length: 2 } parts ? parts[1] : "");
        RefreshPlan();
        UpdateMapMarkers(aimPoint, length);
        _online?.LocalAim(_aimHeading, _hud.ClubCode, _hud.Shot.Power);
    }

    private static string HoleTitle(Hole h) =>
        string.Join(" · ", new[] { $"Hole {h.Number}", h.Name, $"Par {h.Par}", $"{h.LengthYards:0} yd" }.Where(s => s.Length > 0));
}
