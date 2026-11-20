using Godot;
using GrimesGolf.Core;
using GrimesGolf.Core.Audio;
using GrimesGolf.Core.Course;
using GrimesGolf.Core.OpenConnect;
using GrimesGolf.Core.Storage;

namespace GrimesGolf.Game;

/// <summary>
/// GSPro Open Connect: with it on (settings, or <c>--open-connect[=port]</c>), the app listens where GSPro would, so a
/// monitor's GSPro connector app (the Garmin R10 adapter, the MLM2PRO Bluetooth app and others) sends its shots here.
/// A connected connector counts as a launch monitor: the Ready light follows its ready signals, and it's told the club
/// and hand of the player up. It works alongside a Square monitor on Bluetooth; Square-only features (the alignment
/// stick, the ball position) stay with the Square.
/// </summary>
public partial class Main
{
    private OpenConnectServer? _openConnect;
    /// <summary>A connector is connected, the device it named, and whether it has ever sent a ready signal.</summary>
    private bool _connectorLive, _connectorSignalsReady;
    private string? _connectorDevice;
    /// <summary><c>--open-connect[=port]</c>: on for this run, whatever the settings say (port 0 = the settings' port).</summary>
    private int? _openConnectArg;

    private bool OpenConnectWanted => _openConnectArg != null || _settings.OpenConnect;
    private int OpenConnectPort => _openConnectArg is int p and > 0 ? p : _settings.OpenConnectPort;

    private void ReadOpenConnectArgs(string[] args)
    {
        if (args.FirstOrDefault(a => a == "--open-connect" || a.StartsWith("--open-connect=")) is not { } a) return;
        _openConnectArg = a.Length > 15 && int.TryParse(a[15..], out int port) && port is > 0 and < 65536 ? port : 0;
    }

    /// <summary>Starts, stops or restarts the server to match the settings.</summary>
    private void SetupOpenConnect()
    {
        StopOpenConnect();
        if (!OpenConnectWanted) return;
        var server = new OpenConnectServer(OpenConnectPort, _settings.OpenConnectFromNetwork);
        server.Log += m => Post(() => Log(m));
        server.Connected += from => Post(() => OnConnectorConnected(server, from));
        server.Disconnected += why => Post(() => OnConnectorDisconnected(server, why));
        server.DeviceNamed += name => Post(() =>
        {
            if (_openConnect != server) return;
            _connectorDevice = name;
            Log($"Open Connect: the connector is a {name}");
        });
        server.ReadyChanged += (ready, ball) => Post(() => OnConnectorReady(server, ready, ball));
        server.ShotReceived += shot => Post(() => OnConnectorShot(server, shot));
        try
        {
            server.Start();
            _openConnect = server;
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            server.Dispose();
            Log($"Open Connect: can't listen on port {OpenConnectPort}: {ex.Message}");
            _hud.Toast($"Can't accept GSPro connectors: port {OpenConnectPort} is in use (is GSPro running?)", UiTheme.Warn);
        }
    }

    private void StopOpenConnect()
    {
        if (_openConnect == null) return;
        _openConnect.Dispose();
        _openConnect = null;
        if (_connectorLive) ConnectorGone("Open Connect turned off");
    }

    private void OnConnectorConnected(OpenConnectServer server, string from)
    {
        if (_openConnect != server) return;
        bool was = _connectorLive;
        _connectorLive = true;
        _connectorSignalsReady = false;
        Log($"Open Connect: connector connected from {from}");
        if (!was) _hud.Toast("GSPro connector connected", UiTheme.Accent);
        _monitorLive = true;
        _hud.SetMonitorLive(true);
        // Until it says otherwise, take it as ready: not every connector sends ready signals.
        if (_session == null) SetConnectorReady(true, true);
        SendPlayerToConnector();
    }

    private void OnConnectorDisconnected(OpenConnectServer server, string why)
    {
        if (_openConnect != server) return;
        ConnectorGone(why);
    }

    private void ConnectorGone(string why)
    {
        _connectorLive = false;
        _connectorDevice = null;
        Log($"Open Connect: {why}");
        _hud.Toast("GSPro connector disconnected", UiTheme.Warn);
        if (_session != null) return;   // the Square monitor is still connected and in charge of the Ready light
        _monitorLive = _ballSeen = _ballReady = false;
        _hud.SetMonitorLive(false);
        _hud.SetReady("Not connected", UiTheme.Muted);
    }

    private void OnConnectorReady(OpenConnectServer server, bool ready, bool? ball)
    {
        if (_openConnect != server || !_connectorLive) return;
        _connectorSignalsReady = true;
        if (_session == null) SetConnectorReady(ready, ball ?? ready);
    }

    private void SetConnectorReady(bool ready, bool ball)
    {
        if (ready && !_ballReady && _settings.ReadySound) _sounds.Play(SoundEffect.Ready, -6);
        _ballReady = ready;
        _ballSeen = ball;
        string name = _connectorDevice is { } d ? $" ({d})" : "";
        if (ready) _hud.SetReady($"Ready — hit!{name}", new Color(0.18f, 0.54f, 0.32f));
        else if (ball) _hud.SetReady($"Ball detected{name}", new Color(0.80f, 0.60f, 0.10f));
        else _hud.SetReady($"Waiting for the monitor{name}", UiTheme.Muted);
    }

    private void OnConnectorShot(OpenConnectServer server, OpenConnectShot shot)
    {
        if (_openConnect != server || _aligning) return;
        var s = shot.Shot;
        Log($"Open Connect: shot {shot.ShotNumber} from {shot.Device ?? "the connector"}: {s.BallMph:0.0} mph, " +
            $"launch {s.LaunchDeg:0.0}°, direction {RangeShot.Side(s.DirectionDeg)}°, spin {s.SpinRpm:0} rpm, axis {RangeShot.Side(s.SpinAxisDeg)}°");
        // A connector with ready signals will say when it's ready again; one without stays ready.
        if (_session == null && _connectorSignalsReady) SetConnectorReady(false, false);
        ShowShot(s);
    }

    /// <summary>The player up (hand, club, distance to the target) to the connector, which sets them on its monitor.</summary>
    private void SendPlayerToConnector()
    {
        if (_openConnect == null || !_connectorLive) return;
        double yards = _courseMode && _round != null ? _round.DistanceToPin * Units.WorldToYards
                     : _rangeTarget >= 0 ? RangeWorld.TargetYards[_rangeTarget]
                     : Putter ? 10 : 150;
        _openConnect.SendPlayer(_hud.LeftHanded, _hud.ClubCode, yards);
    }
}
