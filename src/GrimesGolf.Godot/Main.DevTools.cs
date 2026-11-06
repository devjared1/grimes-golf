using System.Collections.Concurrent;
using Godot;
using GrimesGolf.Bluetooth;
using GrimesGolf.Core;
using GrimesGolf.Core.Course;
using GrimesGolf.Core.Square;
using GrimesGolf.Core.Storage;
using GrimesGolf.Physics;

namespace GrimesGolf.Game;

/// <summary>Development switches: demo shots, scripted swings, screenshots and one-off tuning changes.</summary>
public partial class Main
{
    // ---- development tools -------------------------------------------------------------------

    private string? _screenshotPath;
    private int _demoShotsLeft;
    private double _devClock, _screenshotAt = 4, _nextDemoAt = 1.5;

    private void SetupDevTools(string[] args)
    {
        string? Arg(string name) => args.FirstOrDefault(a => a.StartsWith(name + "="))?[(name.Length + 1)..];
        _screenshotPath = Arg("--screenshot");
        if (double.TryParse(Arg("--screenshot-delay"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) _screenshotAt = d;
        if (int.TryParse(Arg("--demo-shots"), out var n)) _demoShotsLeft = n;
        if (args.Contains("--monitor-drawer")) _hud.ToggleDrawer();
        // --monitor-ui: the layout and status used with a monitor connected (to check how it looks without one).
        if (args.Contains("--monitor-ui"))
        {
            _hud.SetMonitorLive(true);
            _hud.SetBattery(85, "");
            _hud.SetReady("Ready — hit!", new Color(0.18f, 0.54f, 0.32f));
        }
        // --ball=-40,-30,-33,ready: the ball position panel with this ball (mm), and --stick=1.5: the alignment panel with this reading.
        if (Arg("--ball") is { } ball && ball.Split(',') is { Length: >= 3 } b)
            _hud.SetBall(new Core.Square.BallPosition(Number(b[0]), Number(b[1]), Number(b[2])), ready: b.Length > 3);
        if (Arg("--stick") is { } stick)
        {
            _hud.Alignment.Open();
            _hud.Alignment.ShowReading(new Core.Square.StickEvent(Core.Square.StickStatus.Stable, Number(stick), 0, 0, 0));
        }
        if (Arg("--aligned") is { } aligned) { _alignedAngle = Number(aligned); _alignedAt = DateTime.Now; ShowAlignment(); }
        if (args.Contains("--tuner")) _tuner.Toggle();
        if (args.Contains("--green-read")) SetGreenOverlay(true, true);
        if (args.Contains("--settings")) _openSettingsAtStart = true;
        if (args.Contains("--menu")) _openMenuAtStart = true;
        _showScorecardForScreenshot = args.Contains("--scorecard");
        // --ball-closeup: hold the camera right beside the ball (to check how it looks).
        _ballCloseUp = args.Contains("--ball-closeup");
        // --landing-view: press Zoom just before the screenshot (to check how the landing ring looks).
        _landingViewForScreenshot = args.Contains("--landing-view");
        // --reconnecting: show the banner a dropped monitor gets (to check how it looks without a monitor).
        if (args.Contains("--reconnecting")) _hud.SetBanner("Launch monitor disconnected — reconnecting…");
        // --loading-screen and --celebrate=Birdie: show the loading screen, or a hole's score announcement (to check how they look).
        if (args.Contains("--loading-screen"))
            _hud.Loading.Open(CourseLibrary.All[0].Name, Brand.CourseLogo(CourseLibrary.All[0].Name.Split(" (")[0]), "Building the course…");
        if (Arg("--celebrate") is { } score)
            GetTree().CreateTimer(Math.Max(0.1, _screenshotAt - 1.3)).Timeout += () =>
                _hud.CelebrateScore(score.ToUpperInvariant() + "!", "Ann  ·  3 strokes on hole 7", UiTheme.Accent, sparks: true);
        // --players=Ann,Bob,Cat:L  and  --text-size=Large: for this run only (not saved).
        if (Arg("--players") is { } players)
            _settings = _settings with
            {
                Players = players.Split(',').Select(p => p.EndsWith(":L") ? new Golfer(p[..^2], true) : new Golfer(p)).ToList(),
            };
        if (Enum.TryParse<TextSize>(Arg("--text-size"), true, out var size))
        {
            _settings = _settings with { TextSize = size };
            ApplyDisplaySettings();
        }
        // --graphics=Ultra: this run only (not saved), to compare the presets.
        if (Enum.TryParse<GraphicsQuality>(Arg("--graphics"), true, out var quality))
        {
            _settings = _settings with { Graphics = quality };
            ApplyGraphics();
        }
        // --game=Scramble, --holes=Back9, --wind=Breezy, --weather=Wet: the game setup for this run only (not saved).
        if (Enum.TryParse<GameType>(Arg("--game"), true, out var game)) _game = _game with { Type = game };
        if (Enum.TryParse<HoleSet>(Arg("--holes"), true, out var holes)) _game = _game with { Holes = holes };
        if (Enum.TryParse<WindStrength>(Arg("--wind"), true, out var wind)) _game = _game with { Wind = wind };
        if (Enum.TryParse<Weather>(Arg("--weather"), true, out var weather)) _game = _game with { Weather = weather };
        _settings = _settings with { Game = _game };
        // --hit-at=2,8.5: press HIT SHOT at these times (seconds after start).
        if (Arg("--hit-at") is { } at)
            _hitPresses = new Queue<double>(at.Split(',').Select(t => double.Parse(t, System.Globalization.CultureInfo.InvariantCulture)));
        if (Arg("--tuner-tab") is { } tab) _tuner.ShowTab(tab);
        // --tune=key=value: after the demo shots, change one tuning value and replay the last shot.
        if (Arg("--tune") is { } tune && tune.Split('=') is [var key, var value] && PhysicsTuning.Parameters.Any(p => p.Key == key) &&
            double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
            _devTune = (key, v);
    }

    private static double Number(string s) => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    private (string Key, double Value)? _devTune;
    /// <summary>--scorecard: open the scorecard just before the screenshot.</summary>
    private bool _showScorecardForScreenshot, _openMenuAtStart, _ballCloseUp, _landingViewForScreenshot;
    private Queue<double>? _hitPresses;

    /// <summary>Hits the requested demo shots one after another, then takes the screenshot and quits.</summary>
    private void RunDevTools(double delta)
    {
        if (_ballCloseUp) _camera.CloseUp(_player.BallPosition);
        if (_screenshotPath == null && _demoShotsLeft == 0 && _hitPresses == null) return;
        _devClock += delta;
        if (_hitPresses is { Count: > 0 } presses && _devClock >= presses.Peek())
        {
            presses.Dequeue();
            HitPress();
        }
        bool busy = _player.IsPlaying || _outcome != null || _camera.Flying;
        if (_demoShotsLeft > 0 && !busy && _devClock >= _nextDemoAt && _player.IdleSeconds > 0.5)
        {
            _demoShotsLeft--;
            Demo();
            _nextDemoAt = _devClock + 0.5;
            return;
        }
        if (_devTune is { } t && _demoShotsLeft == 0 && !busy && _player.IdleSeconds > 0.5)
        {
            _devTune = null;
            ActivePhysics.Apply(ActivePhysics.Tuning.With(t.Key, t.Value));
            Replay();
            return;
        }
        // (A hole flyover doesn't hold up the screenshot, so one can be taken part-way through it.)
        if (_screenshotPath != null && _demoShotsLeft == 0 && !_player.IsPlaying && _outcome == null && _devClock >= _screenshotAt && _player.IdleSeconds > 1.2)
        {
            if (_landingViewForScreenshot) { _landingViewForScreenshot = false; ToggleLandingView(); _screenshotAt = _devClock + 3; return; }
            if (_showScorecardForScreenshot) { _showScorecardForScreenshot = false; _hud.ShowScorecard(true); _screenshotAt = _devClock + 0.3; return; }
            string path = _screenshotPath;
            _screenshotPath = null;
            GetViewport().GetTexture().GetImage().SavePng(path);
            GD.Print($"Saved screenshot to {path}");
            if (_tuner.Visible)
            {
                string tunerPath = Path.ChangeExtension(path, null) + "-tuner.png";
                _tuner.GetTexture().GetImage().SavePng(tunerPath);
                GD.Print($"Saved tuner screenshot to {tunerPath}");
            }
            GetTree().Quit();
        }
    }
}
