using GrimesGolf.Core.Monitors;
using GrimesGolf.Core.Storage;

namespace GrimesGolf.Game;

/// <summary>
/// The settings screen: players, house rules, auto-connect, text size, full screen and graphics quality. Saved to
/// settings.json. Players and rules belong to a round, so changes to them apply from the next round (or now, with
/// "Save and start a new round"). Also the home player: loaded at every start, and asked for if none is saved.
/// </summary>
public partial class Main
{
    private bool _openSettingsAtStart;
    /// <summary>What the monitor connected this session says it is (null = none has connected).</summary>
    private string? _detectedMonitor;

    private void SetupSettings()
    {
        _hud.SettingsApplied += ApplySettings;
        _hud.EditHomePressed += () => OpenHomePlayer();
        _hud.Home.Saved += SaveHomePlayer;
    }

    private void OpenSettings() => _hud.OpenSettings(_settings, roundInProgress: _match is { RoundComplete: false });

    private void OpenHomePlayer(bool showClubs = false)
    {
        _hud.Home.SetMonitor(HomeMonitorText());
        _hud.Home.Open(_settings.NeedsHomePlayer ? null : _settings.Home, _settings.HomeCandidate, showClubs);
    }

    private void SaveHomePlayer(HomePlayer home)
    {
        bool first = _settings.NeedsHomePlayer;
        SaveSettingsAlways(_settings.WithHome(home with { LaunchMonitor = _detectedMonitor ?? home.LaunchMonitor }));
        _rangePlayer %= _settings.Golfers.Count;
        if (_hud.SettingsOpen) _hud.RefreshSettingsHome(_settings);
        if (_hud.MainMenu.IsOpen) _hud.MainMenu.SetPlayers(_settings.Golfers, _settings.SavedPlayers, _settings.Home?.Name);
        if (!_courseMode) ShowRangePlayer();
        OnClubChanged();   // their hand on the monitor, and their distances on the aim line and power
        _hud.Toast(first ? $"Welcome, {home.Name}" : "Home player saved", UiTheme.Accent);
    }

    /// <summary>The carries the player up has set (null for a guest): the caddie, the aim line and on-screen swings use them.</summary>
    private IReadOnlyDictionary<string, int>? KnownYards =>
        _settings.ClubYardsFor(_courseMode && _match != null ? _match.Golfer : RangeGolfer);

    /// <summary>The home player's monitor type comes from the monitor itself, once one connects (not a development run's fake one).</summary>
    private void RememberMonitor(MonitorDeviceInfo info)
    {
        if (_fake != null) return;
        if (!info.Identified && _settings.Home?.LaunchMonitor != null) return;   // it couldn't say: keep what's known
        _detectedMonitor = info.Description;
        if (_settings.NeedsHomePlayer || _settings.Home!.LaunchMonitor == _detectedMonitor) return;
        SaveSettings(_settings with { Home = _settings.Home with { LaunchMonitor = _detectedMonitor } });
        if (_hud.SettingsOpen) _hud.RefreshSettingsHome(_settings);
    }

    /// <summary>The monitor line on the home player panel.</summary>
    private string HomeMonitorText()
    {
        if (_monitorLive && _detectedMonitor != null) return $"{_detectedMonitor}, connected";
        if (_session != null) return "Found one, connecting…";
        if (_settings.Home?.LaunchMonitor is { } saved) return $"{saved}, not connected right now";
        return "Looking for one… Not on yet? It's detected the first time it connects. You can play without one.";
    }

    private void ApplySettings(AppSettings s, bool newRound)
    {
        var old = _settings;
        s = s with { Home = old.Home };   // the home player saves by itself, and may have changed since the settings opened
        SaveSettingsAlways(s);
        if (s.TextSize != old.TextSize || s.FullScreen != old.FullScreen) ApplyDisplaySettings();
        if (s.Graphics != old.Graphics) ApplyGraphics();
        if (s.AutoConnect != old.AutoConnect) _autoConnect = s.AutoConnect;
        if (s.MonitorType != old.MonitorType || s.MonitorName != old.MonitorName) ApplyMonitorChoice();
        _rangePlayer %= s.Golfers.Count;
        if (!_courseMode) ShowRangePlayer();
        if (_hud.MainMenu.IsOpen) _hud.MainMenu.SetPlayers(s.Golfers, s.SavedPlayers, s.Home?.Name);

        if (newRound)
        {
            NewRound();
            if (_courseMode) _hud.Toast("New round started", UiTheme.Accent);
            else if (_hud.MainMenu.IsOpen) StartNewGame(_courseIndex, _settings.Setup);   // from the main menu: straight to the first tee
            else _hud.Toast("Saved. New Game on the main menu starts the round.", UiTheme.Accent);
        }
        else if (_match != null && (!s.Golfers.SequenceEqual(_match.Players) || s.Rules != _match.Rules))
            _hud.Toast("Saved. Player and rule changes apply from the next round.", UiTheme.Accent);
        else _hud.Toast("Settings saved", UiTheme.Accent);
    }

    /// <summary>
    /// A different monitor type or name in Settings: monitors no longer wanted leave the list (the one connected stays
    /// connected until it's disconnected), and the connection tips follow the choice.
    /// </summary>
    private void ApplyMonitorChoice()
    {
        foreach (var d in _devices.Values.ToList())
            if (!d.Fake && d.Address != _wantMonitor && !MonitorIdentity.Wanted(d.Type, d.Name, _settings.MonitorType, _settings.MonitorName))
                _devices.Remove(d.Address);
        _devicesDirty = true;
        if (_session == null) _hud.SetMonitorHint(_settings.MonitorType);
        Log($"Looking for: {(_settings.MonitorType == LaunchMonitorType.Auto ? "any supported monitor" : MonitorIdentity.Label(_settings.MonitorType))}" +
            (_settings.MonitorName is { } n ? $" named \"{n}\"" : ""));
    }

    /// <summary>Settings changed on the settings screen are always written (unlike remembered choices in dev runs).</summary>
    private void SaveSettingsAlways(AppSettings s)
    {
        _settings = s;
        s.Save(_dataFolder, Log);
    }
}
