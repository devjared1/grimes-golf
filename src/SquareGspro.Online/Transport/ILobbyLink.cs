namespace SquareGspro.Online.Transport;

/// <summary>Something that happened on a lobby connection. Raised on a background thread: marshal before touching the game.</summary>
public abstract record LinkEvent
{
    /// <summary>In the lobby (made, joined, or back after a drop). <paramref name="Peer"/> is 0 for the host.</summary>
    public sealed record Opened(string Code, int Peer, bool Resumed, bool HostPresent) : LinkEvent;
    /// <summary>The connection dropped; it's being retried.</summary>
    public sealed record Lost(string Reason) : LinkEvent;
    /// <summary>Over for good (no such lobby, full, wrong version, closed): it won't be retried.</summary>
    public sealed record Closed(string Code, string Reason) : LinkEvent;
    /// <summary>Host only: a friend's PC is in (or back).</summary>
    public sealed record PeerJoined(int Peer) : LinkEvent;
    /// <summary>Host only: a friend's PC dropped.</summary>
    public sealed record PeerLeft(int Peer) : LinkEvent;
    /// <summary>Friends only: the host's PC dropped, or came back.</summary>
    public sealed record HostLeft : LinkEvent;
    public sealed record HostBack : LinkEvent;
    public sealed record Message(int From, string Data) : LinkEvent;
}

/// <summary>
/// A PC's place in a lobby: the host (peer 0) talks to each friend or all of them, a friend talks to the host.
/// <see cref="RelayClient"/> over the internet; an in-memory one in the tests.
/// </summary>
public interface ILobbyLink : IDisposable
{
    event Action<LinkEvent>? Event;
    string? Code { get; }
    /// <summary>This PC's peer number once in the lobby (0 = host).</summary>
    int? Peer { get; }
    /// <summary>
    /// Sends <paramref name="data"/>: from the host to peer <paramref name="to"/> (everyone when null), from a friend
    /// to the host (<paramref name="to"/> ignored). Dropped while disconnected; the sessions resync on reconnecting.
    /// </summary>
    void Send(string data, int? to = null);
}
