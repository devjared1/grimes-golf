using Godot;
using GrimesGolf.Bluetooth;
using GrimesGolf.Core;
using GrimesGolf.Core.Audio;
using GrimesGolf.Core.Course;

namespace GrimesGolf.Game;

/// <summary>
/// Bluetooth: scanning for monitors, the connection, and keeping the monitor's club and hand in step. With auto-connect
/// on (settings) it connects by itself: to the monitor used last time, or to the first one found if that one isn't
/// around. If the link drops it keeps trying to reconnect, with a banner saying so, until it's back or the user
/// disconnects.
/// </summary>
public partial class Main
{
    private SquareBleScanner? _scanner;
    private MonitorSession? _session;
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

    /// <summary>Wait this long for the last monitor used before taking any monitor found.</summary>
    private const double LastMonitorGrace = 8;
    /// <summary>Restart a scan that has heard nothing for this long, and only reconnect to a monitor heard this recently (seconds).</summary>
    private const double ScanRestartAfter = 20, RecentlyHeard = 10;
    private double _scanQuiet;
    private bool _scanRestart, _scanStoppedByUser;
    private string _lastScanError = "Success";

    private void SetupMonitor(bool forceAutoConnect)
    {
        _autoConnect = _settings.AutoConnect || forceAutoConnect;
        try
        {
            _scanner = new SquareBleScanner();
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
            if (!quiet) Log("Scanning for Square launch monitors...");
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

    private void OnDeviceSeen(SquareAdvertisement a)
    {
        _scanQuiet = 0;
        if (!_devices.ContainsKey(a.Address)) Log($"Found {a.Name} ({a.AddressText}, {a.Rssi} dBm)");
        _devices[a.Address] = new DeviceEntry(a.Address, a.Name, a.AddressText, a.Rssi, a.SeenAt);
        _devicesDirty = true;
        if (_session == null && _hud.SelectedDevice == null) ShowDevice(_devices[a.Address]);
        // (After a drop the reconnection timer takes it from here, so failed attempts are spaced out.)
        if (_session == null && _wantMonitor == null && _autoConnect && (_settings.LastMonitor == null || _settings.LastMonitor == a.Address))
            Connect(a.Address);
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
        ShowDevice(d);
        _hud.SetStatus($"Connecting to {d.Name}...");
        _hud.SetReady(reconnecting ? "Reconnecting..." : "Connecting...", UiTheme.Warn);

        var s = new MonitorSession(address, m => Post(() => Log(m)));
        _session = s;
        s.InfoRead += info => Post(() =>
        {
            _hud.SetStatus($"Connected to {d.Name}");
            _hud.SetStat("Serial", info.Serial ?? "—");
            _hud.SetStat("Hardware", info.HardwareVersion ?? "—");
            _hud.SetStat("Firmware (LM)", info.FirmwareComponents.GetValueOrDefault("lm") ?? info.FirmwareRaw ?? "—");
            _hud.SetStat("Firmware (MMI)", info.FirmwareComponents.GetValueOrDefault("mmi") ?? "—");
            _hud.SetStat("Firmware (launcher)", info.FirmwareComponents.GetValueOrDefault("launcher") ?? "—");
            _hud.SetReady("Place a ball", UiTheme.Muted);
            _monitorLive = true;
            _hud.SetMonitorLive(true);
            if (_reconnectTries > 0) _hud.Toast("Launch monitor reconnected", UiTheme.Accent);
            _hud.SetBanner(null);
            _reconnectTries = 0;
            RememberMonitor(info);
            if (_settings.LastMonitor != address) SaveSettings(_settings with { LastMonitor = address });
        });
        s.Battery += b => Post(() => _hud.SetBattery(b.Level, ChargingText(b.ChargingState)));
        s.Clock += secs => Post(() =>
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
        s.StateChanged += st => Post(() => _hud.SetStat("State", st.ToString()));
        s.Stick += st => Post(() => { if (_aligning) _hud.Alignment.ShowReading(st); });
        s.Sensor += sn => Post(() =>
        {
            if (_aligning) return;
            _hud.SetBall(sn.Position, sn.BallReady);
            _hud.SetStat("Ball", sn.BallReady ? "Ready" : sn.BallDetected ? "Detected" : "None");
            _ballReady = sn.BallReady;
            _ballSeen = sn.BallDetected;
            if (sn.BallReady) _hud.SetReady("Ready — hit!", new Color(0.18f, 0.54f, 0.32f));
            else if (sn.BallDetected) _hud.SetReady("Ball detected", new Color(0.80f, 0.60f, 0.10f));
            else _hud.SetReady("Place a ball", UiTheme.Muted);
        });
        s.Shot += (ball, club) => Post(() =>
        {
            if (_aligning) return;
            _ballReady = _ballSeen = false;   // it's just been hit
            _hud.SetBall(null);
            ShowShot(RangeShot.FromSquare(ball, club));
        });
        s.Ended += reason => Post(() => OnSessionEnded(s, reason));

        string code = _hud.ClubCode;
        bool lh = _hud.LeftHanded;
        _ = Task.Run(() => s.RunAsync(code, lh));
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

    private void OnSessionEnded(MonitorSession s, string reason)
    {
        if (_session != s) return;
        _session = null;
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
        else _hud.SetReady("Not connected", UiTheme.Muted);
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

    /// <summary>The club and hand on screen, to the monitor.</summary>
    private async void SendClubToMonitor()
    {
        var s = _session;
        if (s == null || _aligning) return;   // (ending the alignment sends the club on screen)
        string code = _hud.ClubCode;
        bool lh = _hud.LeftHanded;
        try
        {
            await Task.Run(() => s.SelectClubAsync(code, lh));
            Log($"Club set to {code}{(lh ? " (LH)" : "")}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log($"Club change failed: {ex.Message}"); }
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
        if (_outcome != null || _player.IsPlaying) return;
        if (_online != null && _courseMode && !_online.IsLocalTurn) { _hud.Toast("Align on your turn", UiTheme.Ink); return; }
        _aligning = true;
        _hud.SetBall(null);
        _hud.Alignment.Open();
        bool lh = _hud.LeftHanded;
        _ = RunMonitorTask(() => s.BeginAlignmentAsync(lh), "Couldn't start aligning");
    }

    private void FinishAlignment(bool confirm, double angle)
    {
        var s = _session;
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

    private static string ChargingText(int state) => state switch
    {
        0 => "not charging", 1 => "on battery", 2 => "charging", 3 => "full", 4 => "no battery", _ => "unknown",
    };
}
