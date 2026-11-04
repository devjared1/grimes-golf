using System.Security.Cryptography;
using System.Text;
using GrimesGolf.Core;
using GrimesGolf.Core.Course;
using GrimesGolf.Online.Protocol;
using GrimesGolf.Online.Transport;

namespace GrimesGolf.Online.Session;

/// <summary>Where an online round is.</summary>
public enum OnlinePhase
{
    /// <summary>In the lobby: players joining, the host choosing the course and settings.</summary>
    Lobby,
    /// <summary>The course is being built on this PC (or the host is waiting for everyone to have built it).</summary>
    Loading,
    /// <summary>A turn is open: the player up can hit.</summary>
    Open,
    /// <summary>A shot is being shown; the next turn opens once every PC has shown it.</summary>
    Flight,
    /// <summary>Everyone has finished the hole; the host's PC moves on to the next.</summary>
    HoleOver,
    Over,
}

/// <summary>An online round the host can carry on later (<c>online-round.json</c>).</summary>
public sealed record OnlineSave(string Code, string Token, RoundConfig Config, MatchSnapshot Snapshot, int Seq, DateTime Saved);

/// <summary>
/// One PC's side of an online round, host (<see cref="HostSession"/>) or friend (<see cref="ClientSession"/>).
/// The game calls in on its own thread; link events are handed to the game's thread by <c>post</c> first.
/// </summary>
public abstract class OnlineSession : IDisposable
{
    /// <summary>Waits before a player who has dropped (or whose monitor has) is skipped, and before a missing host is given up on.</summary>
    public const double DroppedSeconds = 60, MonitorSeconds = 120, HostGoneSeconds = 120;
    /// <summary>How long after a shot's flight the next turn opens even if a PC hasn't said it has shown it.</summary>
    public const double ShownGraceSeconds = 6;
    private const double AimEvery = 0.1;

    protected readonly ILobbyLink Link;
    protected readonly IOnlineGame Game;
    protected readonly Func<double> Clock;
    private Aim? _aimWaiting;
    private double _aimSentAt = double.NegativeInfinity;
    private bool _disposed;

    protected OnlineSession(ILobbyLink link, IOnlineGame game, Action<Action> post, Func<double> clock, string token, string appVersion)
    {
        Link = link;
        Game = game;
        Clock = clock;
        Token = token;
        Pc = PcIdFor(token);
        AppVersion = appVersion;
        link.Event += e => post(() => { if (!_disposed) OnLink(e); });
    }

    /// <summary>A public id for a PC from its secret relay token (the token itself never leaves for the relay).</summary>
    public static string PcIdFor(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("pc:" + token)), 0, 8).ToLowerInvariant();

    public abstract bool IsHost { get; }
    public string Token { get; }
    public string Pc { get; }
    public string AppVersion { get; }
    public string? Code => Link.Code;
    public RoundConfig? Config { get; protected set; }
    public OnlinePhase Phase { get; protected set; } = OnlinePhase.Lobby;
    public int Seq { get; protected set; }
    public LobbyState? Lobby { get; protected set; }
    /// <summary>The line across the top while something's wrong (connection, a dropped player, the host gone); null when all's well.</summary>
    public abstract string? Banner { get; }

    /// <summary>Whether player <paramref name="player"/> of the round sits at this PC.</summary>
    public bool IsLocalSeat(int player) => Config is { } c && player >= 0 && player < c.Seats.Length && c.Seats[player].Pc == Pc;

    /// <summary>Whether player <paramref name="name"/> sits at this PC.</summary>
    public bool IsLocalName(string name) => Config is { } c && c.Seats.Any(s => s.Pc == Pc && s.Name == name);

    /// <summary>The turn is open and its player sits at this PC: their monitor's next shot counts.</summary>
    public bool IsLocalTurn => Phase == OnlinePhase.Open && Game.Match is { HoleComplete: false } m && IsLocalSeat(m.Turn);

    /// <summary>"Your turn: Ann", "Waiting for Bob", "Bob's shot"…</summary>
    public string TurnText
    {
        get
        {
            string name = Game.Match is { } m && Config is { } c && m.Turn < c.Seats.Length ? c.Seats[m.Turn].Name : "";
            return Phase switch
            {
                OnlinePhase.Lobby => "In the lobby",
                OnlinePhase.Loading => "Getting everyone to the course…",
                OnlinePhase.Open when IsLocalTurn => $"Your turn: {name}",
                OnlinePhase.Open => $"Waiting for {name}",
                OnlinePhase.Flight => $"{name}'s shot",
                OnlinePhase.HoleOver => "Hole finished",
                _ => "Round over",
            };
        }
    }

    // ---- from the game ---------------------------------------------------------------------------

    /// <summary>A shot from this PC's launch monitor. Only counts if it's this PC's turn; otherwise a toast says why.</summary>
    public abstract void MonitorShot(RangeShot shot, string club, double heading, bool putt);

    /// <summary>The game has built the round's course (<see cref="IOnlineGame.BuildRound"/>); <paramref name="fingerprint"/> is what it built.</summary>
    public abstract void RoundBuilt(string fingerprint);

    /// <summary>This PC has finished showing the last shot.</summary>
    public abstract void ShotShown();

    /// <summary>Asks to take back the last shot (only the PC whose player hit it can).</summary>
    public abstract void RequestMulligan();

    /// <summary>This PC's launch monitor is live, or isn't any more.</summary>
    public abstract void SetMonitor(bool live);

    /// <summary>The aim, club and power on screen, while it's this PC's turn (sent at most 10 times a second).</summary>
    public void LocalAim(double heading, string club, double power)
    {
        if (!IsLocalTurn || Game.Match is not { } m || !double.IsFinite(heading) || !double.IsFinite(power)) return;
        _aimWaiting = new Aim(Seq, m.Turn, heading, club, power);
        SendAimIfDue();
    }

    private void SendAimIfDue()
    {
        if (_aimWaiting is not { } a || Clock() - _aimSentAt < AimEvery) return;
        _aimWaiting = null;
        if (a.Seq != Seq || !IsLocalTurn) return;
        _aimSentAt = Clock();
        SendAim(a);
    }

    protected abstract void SendAim(Aim aim);

    /// <summary>Call every frame: timers (dropped players, slow PCs, the host gone) and the aim waiting to go.</summary>
    public virtual void Tick() => SendAimIfDue();

    protected abstract void OnLink(LinkEvent e);

    protected static string Who(RoundConfig? c, int player) => c != null && player >= 0 && player < c.Seats.Length ? c.Seats[player].Name : "A player";

    public virtual void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Link.Dispose();
    }

    /// <summary>A player's name as it's shown: trimmed, no control characters, at most 16 characters, never empty.</summary>
    public static string CleanName(string? name, int index)
    {
        var s = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (s.Length > OnlineProtocol.MaxNameLength) s = s[..OnlineProtocol.MaxNameLength].TrimEnd();
        return s.Length == 0 ? $"Player {index + 1}" : s;
    }
}
