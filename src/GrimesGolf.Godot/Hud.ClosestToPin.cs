using Godot;

namespace GrimesGolf.Game;

/// <summary>
/// Closest to the Pin's bits of the course view while <see cref="SetPinMode"/> is on: the hole badge counts shots and shows
/// the player's closest so far, the standings are always on screen (they're the game, so not only with a monitor connected),
/// and the Mulligan and flag buttons go (a ball hit at the pin stays where it lands). The standings table that comes up
/// at the end is <see cref="PinResults"/>.
/// </summary>
public partial class Hud
{
    /// <summary>The standings table: over the game at the end (and on request in the middle).</summary>
    public PinResultsPanel PinResults { get; private set; } = null!;

    private bool _pinMode, _pinDone;

    private void BuildPinResults()
    {
        PinResults = new PinResultsPanel();
        AddChild(PinResults);
    }

    /// <summary>Once the last shot has been counted, HIT SHOT says RESULTS instead of NEXT PLAYER.</summary>
    public void SetPinDone(bool done) => _pinDone = done;

    /// <summary>Closest to the Pin on or off.</summary>
    public void SetPinMode(bool on)
    {
        if (_pinMode == on) return;
        _pinMode = on;
        _pinDone = false;
        _nextWord = on ? "PLAYER" : "HOLE";
        _strokeCaption.Text = on ? "SHOT" : "STROKE";
        _scoreCaption.Text = on ? "CLOSEST" : "SCORE";
        _flag.Visible = !on;
        if (on) _mulligan.Visible = false;
        else
        {
            PinResults.Close();
            SetTurn(null, UiTheme.Ink, "");
            _mulligan.TooltipText = _mulliganTip;
        }
        UpdateShotLayout();
    }
}
