using SquareGspro.Core.Storage;

namespace SquareGspro.Range3D;

/// <summary>
/// The settings screen: players, house rules, auto-connect, text size, full screen and graphics quality. Saved to
/// settings.json. Players and rules belong to a round, so changes to them apply from the next round (or now, with
/// "Save and start a new round").
/// </summary>
public partial class Main
{
    private bool _openSettingsAtStart;

    private void SetupSettings() => _hud.SettingsApplied += ApplySettings;

    private void OpenSettings() => _hud.OpenSettings(_settings, roundInProgress: _match is { RoundComplete: false });

    private void ApplySettings(AppSettings s, bool newRound)
    {
        var old = _settings;
        SaveSettingsAlways(s);
        if (s.TextSize != old.TextSize || s.FullScreen != old.FullScreen) ApplyDisplaySettings();
        if (s.Graphics != old.Graphics) ApplyGraphics();
        if (s.AutoConnect != old.AutoConnect) _autoConnect = s.AutoConnect;
        _rangePlayer %= s.Golfers.Count;
        if (!_courseMode) ShowRangePlayer();
        if (_hud.MainMenu.IsOpen) _hud.MainMenu.SetPlayers(PlayersText(), s.Golfers);

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

    /// <summary>Settings changed on the settings screen are always written (unlike remembered choices in dev runs).</summary>
    private void SaveSettingsAlways(AppSettings s)
    {
        _settings = s;
        s.Save(_dataFolder, Log);
    }
}
