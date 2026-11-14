using Godot;
using GrimesGolf.Bluetooth;
using GrimesGolf.Core;
using GrimesGolf.Core.Audio;
using GrimesGolf.Core.Course;
using GrimesGolf.Core.Garmin;
using GrimesGolf.Core.Monitors;

namespace GrimesGolf.Game;

/// <summary>
/// Bluetooth: scanning for monitors (a Square or a Garmin R10, as Settings allows), the connection, and keeping the
/// monitor's club and hand in step. With auto-connect on (settings) it connects by itself: to the monitor used last
/// time, or to the first one found if that one isn't around. If the link drops it keeps trying to reconnect, with a
/// banner saying so, until it's back or the user disconnects. Monitor-specific features (the Square's ball sensor
/// and alignment stick) are used only when the monitor connected has them.
/// </summary>
public partial class Main
{
    private MonitorScanner? _scanner;
    private ILaunchMonitorSession? _session;
    private readonly Dictionary<ulong, DeviceEntry> _devices = new();
    private bool _devicesDirty, _quitting;
    private double _deviceRefresh;
    /// <summary>The monitor the user wants connected (null = none, or they disconnected it).</summary>
    private ulong? _wantMonitor;
    /// <summary>Seconds until the next reconnection attempt (negative = none pending), and attempts so far.</summary>
    private double _reconnectIn = -1;
    private int _reconnectTries;
    /// <summary>Seconds spent scanning, for falling back from the last monitor to any monitor.</summary>
    private double _scanClock;
    /// <summary>Auto-connect is off once the user disconnects (until they connect again).</summary>
    private bool _autoConnect;
    /// <summary>A monitor is connected and talking; whether it sees a ball, and whether that ball is ready to hit (for the Ready light).</summary>
    private bool _monitorLive, _ballSeen, _ballReady;
    /// <summary>What the Ready light says while a monitor without a ball sensor (an R10) isn't ready.</summary>
    private string _monitorNotReady = "Not ready";

    /// <summary>Wait this long for the last monitor used before taking any monitor found.</summary>
    private const double LastMonitorGrace = 8;
    /// <summary>Restart a scan that has heard nothing for this long, and only reconnect to a monitor heard this recently (seconds).</summary>
    private const double ScanRestartAfter = 20, RecentlyHeard = 10;
    private double _scanQuiet;
    private bool _scanRestart, _scanStoppedByUser;
    private string _lastScanError = "Success";

    /// <summary>Development runs: the kind of stand-in monitor (<c>--fake-monitor=r10</c> or <c>=square</c>), and the one in use.</summary>
    private LaunchMonitorType? _fakeKind;
    private FakeR10Device? _fakeR10;
    private IFakeMonitor? _fake;

    private void SetupMonitor(bool forceAutoConnect)
    {
        _autoConnect = _settings.AutoConnect || forceAutoConnect;
        _hud.SetMonitorHint(_settings.MonitorType);
        if (_fakeKind is { } fake)
        {
            AddFakeDevice(fake);
            return;
        }
        try
        {
            _scanner = new MonitorScanner();
            _scanner.DeviceSeen += a => Post(() => OnDeviceSeen(a));
            _scanner.Stopped += err => Post(() =>
            {
                _hud.SetScanning(_scanner?.IsScanning == true);
                if (err != "Success" && err != _lastScanError) Log($"Scan stopped: {err}. Is Bluetooth turned on?");
                _lastScanError = err;
            });
            StartScan();
        }
        catch (Exception ex) { Log($"Bluetooth unavailable: {ex.Message}"); }
    }

    /// <summary>Device list refresh, auto-connect fallback and reconnection timer.</summary>
    private void ProcessMonitor(double delta)
    {
        foreach (var d in _devices.Values.Where(d => d.Fake).ToList()) _devices[d.Address] = d with { SeenAt = DateTime.Now };   // always "heard"
        _deviceRefresh += delta;
        if (_deviceRefresh > (_devicesDirty ? 0.5 : 2.0))
        {
            _deviceRefresh = 0;
            _devicesDirty = false;
            _hud.SetDevices(_devices.Values.OrderBy(d => d.Name).ToList());
        }
        if (_scanner?.IsScanning == true) _scanClock += delta;
        if (_session == null && _autoConnect && _wantMonitor == null && _scanClock > LastMonitorGrace && _devices.Count > 0)
            Connect(_devices.Values.OrderByDescending(d => d.Rssi).First().Address);

        if (_reconnectIn >= 0 && (_reconnectIn -= delta) < 0 && _session == null && _wantMonitor is { } addr)
        {
            // Only once it's been heard again: trying a monitor that's off stops the scan for half a minute at a time.
            if (_devices.TryGetValue(addr, out var seen) && (DateTime.Now - seen.SeenAt).TotalSeconds < RecentlyHeard)
            {
                _reconnectTries++;
                Connect(addr, reconnecting: true);
            }
            else _reconnectIn = 3;
        }
        KeepScanAlive(delta);
    }

    // ---- scanning ----------------------------------------------------------------------------

    /// <summary>
    /// Waiting for a monitor to turn up, a scan that hears nothing gets restarted now and then. The Windows watcher
    /// can keep reporting "started" without passing on a monitor switched on after it began; restarting finds it
    /// straight away (seen in a log: 3 minutes of nothing, then found 2 seconds after Stop and Scan).
    /// </summary>
    private void KeepScanAlive(double delta)
    {
        bool waiting = _scanner != null && _session == null && !_scanStoppedByUser && (_autoConnect || _wantMonitor != null);
        if (!waiting) { _scanQuiet = 0; _scanRestart = false; return; }
        if (_scanRestart)
        {
            if (_scanner!.CanStart) StartScan(quiet: true);
            return;
        }
        if ((_scanQuiet += delta) < ScanRestartAfter) return;
        _scanRestart = true;
        _scanner!.Stop();
    }

    private void StartScan(bool quiet = false)
    {
        if (_scanner == null) return;
        _scanQuiet = 0;
        _scanRestart = false;
        try
        {
            _scanner.Start();
            _hud.SetScanning(true);
            if (!quiet) Log(_settings.MonitorType switch
            {
                LaunchMonitorType.Square => "Scanning for Square launch monitors...",
                LaunchMonitorType.GarminR10 => "Scanning for a Garmin R10...",
                _ => "Scanning for launch monitors (Square, Garmin R10)...",
            });
        }
        catch (Exception ex) { if (!quiet) Log($"Can't scan: {ex.Message}"); }
    }

    private void ToggleScan()
    {
        if (_scanner == null) return;
        _scanStoppedByUser = _scanner.IsScanning;
        if (_scanner.IsScanning) { _scanner.Stop(); _hud.SetScanning(false); }
        else StartScan();
    }

    private void OnDeviceSeen(MonitorAdvertisement a)
    {
        // Only the kind of monitor chosen in Settings (any, for Auto), and only the one named there if one is.
        if (!MonitorIdentity.Wanted(a.Type, a.Name, _settings.MonitorType, _settings.MonitorName)) return;
        _scanQuiet = 0;
        if (!_devices.ContainsKey(a.Address)) Log($"Found {a.Name} ({a.AddressText}, {a.Rssi} dBm)");
        _devices[a.Address] = new DeviceEntry(a.Address, a.Name, a.AddressText, a.Rssi, a.SeenAt, a.Type);
        _devicesDirty = true;
        if (_session == null && _hud.SelectedDevice == null) ShowDevice(_devices[a.Address]);
        // (After a drop the reconnection timer takes it from here, so failed attempts are spaced out.)
        if (_session == null && _wantMonitor == null && _autoConnect && (_settings.LastMonitor == null || _settings.LastMonitor == a.Address))
            Connect(a.Address);
    }

    /// <summary>A development run's stand-in monitor, listed (and auto-connected) like a real one.</summary>
    private void AddFakeDevice(LaunchMonitorType kind)
    {
        bool r10 = kind == LaunchMonitorType.GarminR10;
        var d = new DeviceEntry(r10 ? 0xFA4E10UL : 0xFA4E50UL, r10 ? "Approach R10 (fake)" : "SquareGolf(FAKE)", "fake", -50, DateTime.Now, kind, Fake: true);
        _devices[d.Address] = d;
        _devicesDirty = true;
        Log($"Fake {MonitorIdentity.Label(kind)}: HIT SHOT (Space) hits with it once it's connected");
        ShowDevice(d);
        if (_autoConnect) Connect(d.Address);
    }

    private void ShowDevice(DeviceEntry d)
    {
        _hud.SetStat("Name", d.Name);
        _hud.SetStat("Address", d.AddressText);
        _hud.SetStat("Signal", $"{d.Rssi} dBm");
    }

    // ---- connection --------------------------------------------------------------------------

    private void Connect(ulong address) => Connect(address, reconnecting: false);

    private void Connect(ulong address, bool reconnecting)
    {
        if (_session != null || !_devices.TryGetValue(address, out var d)) return;

        _wantMonitor = address;
        _scanStoppedByUser = false;
        _reconnectIn = -1;
        if (!reconnecting) _reconnectTries = 0;
        _scanner?.Stop();
        _hud.SetScanning(false);
        _hud.SetConnected(true);
        _hud.ResetStats();
        _hud.ShowStatsFor(d.Type);
        _hud.SetMonitorHint(d.Type);
        ShowDevice(d);
        _hud.SetStatus($"Connecting to {d.Name}...");
        _hud.SetReady(reconnecting ? "Reconnecting..." : "Connecting...", UiTheme.Warn);

        var s = NewSession(d);
        _session = s;
        s.Connected += info => Post(() =>
        {
            _hud.SetStatus($"Connected to {d.Name}");
            foreach (var stat in info.Stats) _hud.SetStat(stat.Label, stat.Value);
            _hud.SetMonitorName(info.Description);
            _hud.SetCanAlign(s is IAlignmentMonitor);
            _monitorNotReady = "Not ready";
            if (s is IBallSensingMonitor) _hud.SetReady("Place a ball", UiTheme.Muted);
            else if (!_ballReady) _hud.SetReady("Connected", UiTheme.Muted);
            _monitorLive = true;
            _hud.SetMonitorLive(true);
            if (_reconnectTries > 0) _hud.Toast("Launch monitor reconnected", UiTheme.Accent);
            _hud.SetBanner(null);
            _reconnectTries = 0;
            RememberMonitor(info);
            if (_settings.LastMonitor != address && !d.Fake) SaveSettings(_settings with { LastMonitor = address });
        });
        s.Battery += b => Post(() => _hud.SetBattery(b.Level, b.Charging));
        if (s is IMonitorClock clock)
            clock.Clock += secs => Post(() =>
            {
                _hud.SetStat("Uptime", TimeSpan.FromSeconds(secs).ToString(secs >= 86400 ? @"d\.hh\:mm\:ss" : @"hh\:mm\:ss"));
                // Its clock went backwards: it has been restarted, so what it was told is gone.
                if (secs < _monitorClock && _alignedAngle != null)
                {
                    _alignedAngle = null;
                    Log("The monitor restarted: its alignment is gone.");
                }
                _monitorClock = secs;
                ShowAlignment();
            });
        s.StateChanged += st => Post(() => _hud.SetStat("State", st));
        if (s is IAlignmentMonitor aligner) aligner.Stick += st => Post(() => { if (_aligning) _hud.Alignment.ShowReading(st); });
        if (s is IBallSensingMonitor sensing)
            sensing.Sensor += sn => Post(() =>
            {
                if (_aligning) return;
                _hud.SetBall(sn.Position, sn.BallReady);
                _hud.SetStat("Ball", sn.BallReady ? "Ready" : sn.BallDetected ? "Detected" : "None");
                // A chime as the monitor becomes ready for the shot (its own setting, apart from the other sound effects).
                if (sn.BallReady && !_ballReady && _settings.ReadySound) _sounds.Play(SoundEffect.Ready, -6);
                _ballReady = sn.BallReady;
                _ballSeen = sn.BallDetected;
                if (sn.BallReady) _hud.SetReady("Ready — hit!", ReadyColour);
                else if (sn.BallDetected) _hud.SetReady("Ball detected", new Color(0.80f, 0.60f, 0.10f));
                else _hud.SetReady("Place a ball", UiTheme.Muted);
            });
        // Monitors without a ball sensor say when they're ready for a shot instead.
        s.ReadinessChanged += r => Post(() =>
        {
            if (_aligning) return;
            if (r.Ready && !_ballReady && _settings.ReadySound) _sounds.Play(SoundEffect.Ready, -6);
            _ballReady = r.Ready;
            _ballSeen = false;
            _monitorNotReady = r.Text;
            _hud.SetReady(r.Text, r.Ready ? ReadyColour : r.Problem ? UiTheme.Warn : UiTheme.Muted);
            if (r.Problem) _hud.Toast(r.Text, UiTheme.Warn);
        });
        s.Shot += ms => Post(() =>
        {
            if (_aligning) return;
            _ballReady = _ballSeen = false;   // it's just been hit
            _hud.SetBall(null);
            OnMonitorShot(s, ms);
        });
        s.Ended += reason => Post(() => OnSessionEnded(s, reason));

        string code = _hud.ClubCode;
        bool lh = _hud.LeftHanded;
        _ = Task.Run(() => s.RunAsync(code, lh));
    }

    private static readonly Color ReadyColour = new(0.18f, 0.54f, 0.32f);

    /// <summary>The session for a monitor of this kind: over Bluetooth, or a development run's stand-in.</summary>
    private ILaunchMonitorSession NewSession(DeviceEntry d)
    {
        void SessionLog(string m) => Post(() => Log(m));
        if (d.Fake && d.Type == LaunchMonitorType.Square)
        {
            var square = new FakeSquareSession(SessionLog);
            _fake = square;
            return square;
        }
        if (d.Type == LaunchMonitorType.GarminR10)
        {
            var capture = new MonitorCapture(_dataFolder, "r10");
            SessionLog($"Raw R10 traffic is saved to {capture.Path}");
            if (d.Fake)
            {
                var device = _fakeR10 ??= new FakeR10Device();
                _fake = device;
                return new R10Session(() => device, SessionLog, capture.Write);
            }
            return new R10Session(() => new R10BleLink(d.Address, SessionLog), SessionLog, capture.Write);
        }
        return new SquareMonitorAdapter(d.Address, SessionLog);
    }

    /// <summary>A shot from the monitor, into the same pipeline as every other shot.</summary>
    private void OnMonitorShot(ILaunchMonitorSession s, MonitorShot ms)
    {
        var shot = RangeShot.FromLaunchMonitor(ms, out var check);
        // (A Square's shots fly exactly as they always have, so only other monitors' notes are logged.)
        if (s.Type != LaunchMonitorType.Square)
            foreach (var note in (ms.Notes ?? Array.Empty<string>()).Concat(check.Warnings)) Log($"  {note}");
        if (shot == null)
        {
            Log($"The monitor's shot couldn't be played: {string.Join(" ", check.Errors)}");
            _hud.Toast("The monitor missed that one — hit again", UiTheme.Warn);
            return;
        }
        ShowShot(shot);
    }

    /// <summary>The user's Disconnect: no reconnecting, and no auto-connect until they connect again.</summary>
    private void Disconnect()
    {
        _wantMonitor = null;
        _autoConnect = false;
        _reconnectIn = -1;
        _hud.SetBanner(null);
        _session?.Stop();
    }

    private void OnSessionEnded(ILaunchMonitorSession s, string reason)
    {
        if (_session != s) return;
        _session = null;
        _fake = null;
        _monitorLive = _ballSeen = _ballReady = false;
        if (_quitting) { GetTree().Quit(); return; }
        _hud.SetMonitorLive(false);
        _hud.SetBall(null);
        _aligning = false;
        _hud.Alignment.Close();
        Log(reason);
        _hud.SetStatus(reason);
        _hud.SetStat("State", "—");
        _hud.SetStat("Ball", "—");
        _hud.SetConnected(false);
        if (_wantMonitor != null)
        {
            // Dropped (or a reconnection attempt failed): keep trying, and say so where everyone can see it.
            _reconnectIn = Math.Min(2 + _reconnectTries, 10);
            _hud.SetReady("Reconnecting...", UiTheme.Warn);
            _hud.SetBanner(_reconnectTries == 0 ? "Launch monitor disconnected — reconnecting…"
                                                : $"Launch monitor disconnected — reconnecting… (try {_reconnectTries + 1})");
        }
        else
        {
            _hud.SetReady("Not connected", UiTheme.Muted);
            _hud.SetMonitorName(null);
        }
        _scanClock = 0;
        StartScan();
    }

    /// <summary>Club or hand changed: refresh the plan or meter, and tell the monitor.</summary>
    private void OnClubChanged()
    {
        SuggestPower();
        if (_courseMode) UpdateAim(); else UpdateRangeAim();
        // Online, a club shown for a player at another PC isn't sent to this monitor (it's set when the turn comes here).
        if (_online != null && _courseMode && !_online.IsLocalTurn) return;
        SendClubToMonitor();
    }

    /// <summary>The club and hand on screen, to the monitor (one without a club setting just notes the hand for its shots).</summary>
    private async void SendClubToMonitor()
    {
        var s = _session;
        if (s == null || _aligning) return;   // (ending the alignment sends the club on screen)
        string code = _hud.ClubCode;
        bool lh = _hud.LeftHanded;
        try
        {
            await Task.Run(() => s.SelectClubAsync(code, lh));
            if (s.Capabilities.HasFlag(MonitorCapabilities.ClubSelection)) Log($"Club set to {code}{(lh ? " (LH)" : "")}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log($"Club change failed: {ex.Message}"); }
    }

    /// <summary>HIT SHOT with a development run's stand-in monitor connected: it "hits" the shot set up, as the monitor would send it.</summary>
    private bool HitWithFakeMonitor()
    {
        if (_fake == null || !_monitorLive) return false;
        var shot = _courseMode ? CourseDemoShot() : Putter ? Caddie.DemoShot(_hud.Club, 6, Lie.Green, _rng) : RangeShot.Demo(_hud.Club, _rng);
        if (shot != null) _fake.Hit(shot);
        return true;
    }

    // ---- alignment stick ---------------------------------------------------------------------

    /// <summary>The monitor is looking for the stick rather than a ball.</summary>
    private bool _aligning;
    /// <summary>The target line given to the monitor this session (null = none yet), when, and the monitor's clock (seconds).</summary>
    private double? _alignedAngle;
    private DateTime _alignedAt;
    private ulong _monitorClock;

    private void OpenAlignment()
    {
        var s = _session;
        if (s == null || !_monitorLive) { _hud.Toast("Connect the launch monitor first", UiTheme.Warn); return; }
        if (s is not IAlignmentMonitor aligner) { _hud.Toast("This launch monitor doesn't use an alignment stick", UiTheme.Ink); return; }
        if (_outcome != null || _player.IsPlaying) return;
        if (_online != null && _courseMode && !_online.IsLocalTurn) { _hud.Toast("Align on your turn", UiTheme.Ink); return; }
        _aligning = true;
        _hud.SetBall(null);
        _hud.Alignment.Open();
        bool lh = _hud.LeftHanded;
        _ = RunMonitorTask(() => aligner.BeginAlignmentAsync(lh), "Couldn't start aligning");
    }

    private void FinishAlignment(bool confirm, double angle)
    {
        var s = _session as IAlignmentMonitor;
        _aligning = false;
        if (confirm)
        {
            _alignedAngle = angle;
            _alignedAt = DateTime.Now;
            ShowAlignment();
            _hud.Toast($"Aligned {AlignmentPanel.AngleText(angle)}. Take the stick away.", UiTheme.Accent);
        }
        if (s == null) return;
        string club = _hud.ClubCode;
        bool lh = _hud.LeftHanded;
        _ = RunMonitorTask(() => s.EndAlignmentAsync(confirm, angle, club, lh), "Couldn't finish aligning");
    }

    private async Task RunMonitorTask(Func<Task> work, string failed)
    {
        try { await Task.Run(work); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log($"{failed}: {ex.Message}"); }
        catch (OperationCanceledException) { }
    }

    private void ShowAlignment() =>
        _hud.SetAlignment(_alignedAngle is { } a ? AlignmentPanel.AngleText(a) : null, _alignedAngle == null ? "" : _alignedAt.ToString("h:mm tt"));

    // ---- sound -------------------------------------------------------------------------------

    private Sounds _sounds = null!;

    private void SetupSounds()
    {
        _sounds = new Sounds();
        AddChild(_sounds);
        _player.Landed += lie => { if (lie != Lie.Water) Sound(SoundEffect.Landing, -8); };
        _player.TreeHit += () => Sound(SoundEffect.Tree);
        _player.Stopped += () => { if (_outcome is { Holed: true }) Sound(SoundEffect.Cup); };
    }

    private void Sound(SoundEffect effect, float volumeDb = 0)
    {
        if (_settings.SoundEffects) _sounds.Play(effect, volumeDb);
    }
}

/// <summary>
/// A raw capture of one monitor session (every frame both ways), in <c>monitor-logs</c> in the data folder, to send
/// back and turn into test fixtures. Opened on the first line written.
/// </summary>
public sealed class MonitorCapture
{
    private readonly object _gate = new();
    public string Path { get; }

    public MonitorCapture(string dataFolder, string kind) =>
        Path = System.IO.Path.Combine(dataFolder, "monitor-logs", $"{kind}-{DateTime.Now:yyyyMMdd-HHmmss}.log");

    public void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff}  {line}\n");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
