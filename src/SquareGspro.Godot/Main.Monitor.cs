using Godot;
using SquareGspro.Bluetooth;
using SquareGspro.Core;

namespace SquareGspro.Range3D;

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

    private void SetupMonitor(bool forceAutoConnect)
    {
        _autoConnect = _settings.AutoConnect || forceAutoConnect;
        try
        {
            _scanner = new SquareBleScanner();
            _scanner.DeviceSeen += a => Post(() => OnDeviceSeen(a));
            _scanner.Stopped += err => Post(() =>
            {
                _hud.SetScanning(false);
                if (err != "Success") Log($"Scan stopped: {err}. Is Bluetooth turned on?");
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
            _reconnectTries++;
            if (_devices.ContainsKey(addr)) Connect(addr, reconnecting: true);
            else _reconnectIn = 3;   // not heard yet: the scan will connect as soon as it is
        }
    }

    // ---- scanning ----------------------------------------------------------------------------

    private void StartScan()
    {
        if (_scanner == null) return;
        try
        {
            _scanner.Start();
            _hud.SetScanning(true);
            Log("Scanning for Square launch monitors...");
        }
        catch (Exception ex) { Log($"Can't scan: {ex.Message}"); }
    }

    private void ToggleScan()
    {
        if (_scanner == null) return;
        if (_scanner.IsScanning) { _scanner.Stop(); _hud.SetScanning(false); }
        else StartScan();
    }

    private void OnDeviceSeen(SquareAdvertisement a)
    {
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
            if (_reconnectTries > 0) _hud.Toast("Launch monitor reconnected", UiTheme.Accent);
            _hud.SetBanner(null);
            _reconnectTries = 0;
            if (_settings.LastMonitor != address) SaveSettings(_settings with { LastMonitor = address });
        });
        s.Battery += b => Post(() => _hud.SetBattery(b.Level, ChargingText(b.ChargingState)));
        s.Clock += secs => Post(() => _hud.SetStat("Uptime", TimeSpan.FromSeconds(secs).ToString(secs >= 86400 ? @"d\.hh\:mm\:ss" : @"hh\:mm\:ss")));
        s.StateChanged += st => Post(() => _hud.SetStat("State", st.ToString()));
        s.Sensor += sn => Post(() =>
        {
            _hud.SetStat("Ball", sn.BallReady ? "Ready" : sn.BallDetected ? "Detected" : "None");
            _ballReady = sn.BallReady;
            _ballSeen = sn.BallDetected;
            if (sn.BallReady) _hud.SetReady("Ready — hit!", new Color(0.18f, 0.54f, 0.32f));
            else if (sn.BallDetected) _hud.SetReady("Ball detected", new Color(0.80f, 0.60f, 0.10f));
            else _hud.SetReady("Place a ball", UiTheme.Muted);
        });
        s.Shot += (ball, club) => Post(() =>
        {
            _ballReady = _ballSeen = false;   // it's just been hit
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
    private async void OnClubChanged()
    {
        SuggestPower();
        if (_courseMode) UpdateAim(); else UpdateRangeAim();
        var s = _session;
        if (s == null) return;
        string code = _hud.ClubCode;
        bool lh = _hud.LeftHanded;
        try
        {
            await Task.Run(() => s.SelectClubAsync(code, lh));
            Log($"Club set to {code}{(lh ? " (LH)" : "")}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log($"Club change failed: {ex.Message}"); }
    }

    private static string ChargingText(int state) => state switch
    {
        0 => "not charging", 1 => "on battery", 2 => "charging", 3 => "full", 4 => "no battery", _ => "unknown",
    };
}
