using SquareGspro.Core.Course;
using SquareGspro.Online.Protocol;

namespace SquareGspro.Online.Session;

/// <summary>
/// What the sessions need from the game on this PC (the Godot app; a fake in the tests). All calls come on the
/// game's own thread.
/// </summary>
public interface IOnlineGame
{
    /// <summary>The round's match on this PC (null until the round is built).</summary>
    Match? Match { get; }

    /// <summary>
    /// Builds the round: the course from <paramref name="config"/> (perhaps over a few frames) and a match with its
    /// seats, put as <paramref name="snapshot"/> (on the host, null: a new round). When it's built, the game calls
    /// <see cref="OnlineSession.RoundBuilt"/> with the course it built.
    /// </summary>
    void BuildRound(RoundConfig config, MatchSnapshot? snapshot);

    /// <summary>Friends: a shot the host played. Animate it (the match is put as its snapshot first), then call <see cref="OnlineSession.ShotShown"/>.</summary>
    void ShowShot(ShotResult shot);

    /// <summary>Friends: a new turn (or a resync). Put the match as the snapshot and set up the next shot if it changed anything.</summary>
    void OpenTurn(TurnOpen turn);

    /// <summary>The player whose turn it is on another PC is aiming.</summary>
    void ShowAim(Aim aim);

    void Notice(string text);

    /// <summary>The lobby changed (players, PCs, monitors, settings).</summary>
    void LobbyChanged(LobbyState lobby);

    /// <summary>The round is over, or stopped by the host to carry on later (<paramref name="paused"/>).</summary>
    void RoundOver(bool paused, string summary);

    /// <summary>The session can't go on (refused by the host, the lobby closed).</summary>
    void Ended(string reason);

    // ---- host only ----

    /// <summary>Host: plays the shot (the turn is already the request's player) and starts animating it. Null if it didn't count.</summary>
    OutcomeData? PlayShot(ShotRequest shot);

    /// <summary>Host: the shot is taking too long to show here: finish it now (the ball to where it stopped).</summary>
    void FinishShotNow();

    /// <summary>Host: the match changed by itself (a player skipped or back): show it.</summary>
    void MatchChanged();

    /// <summary>Host: takes back the last shot, as the mulligan button does. False if it couldn't.</summary>
    bool Mulligan();
}
