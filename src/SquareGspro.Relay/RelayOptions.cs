namespace SquareGspro.Relay;

/// <summary>
/// Limits for a relay meant for a few friends, not the public: small frames, a handful of lobbies, and lobbies that
/// go away once their host has been gone a while.
/// </summary>
public sealed class RelayOptions
{
    /// <summary>The envelope version a client must speak (see <c>RelayWire</c> in SquareGspro.Online).</summary>
    public const int Protocol = 1;

    /// <summary>Largest message accepted, bytes.</summary>
    public int MaxFrameBytes { get; set; } = 256 * 1024;
    /// <summary>PCs in a lobby, the host's included.</summary>
    public int MaxPcsPerLobby { get; set; } = 4;
    public int MaxLobbies { get; set; } = 100;
    /// <summary>How long a lobby lasts at most, host there or not.</summary>
    public TimeSpan MaxLobbyAge { get; set; } = TimeSpan.FromHours(12);
    /// <summary>How long a lobby waits for its host to come back.</summary>
    public TimeSpan HostGrace { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>Lobby creates and joins allowed per IP address in <see cref="JoinWindow"/>.</summary>
    public int JoinsPerWindow { get; set; } = 20;
    public TimeSpan JoinWindow { get; set; } = TimeSpan.FromMinutes(1);
    /// <summary>Messages waiting to go out to one PC before it's dropped as too slow.</summary>
    public int SendQueue { get; set; } = 256;
    /// <summary>A connection that hasn't said create or join by then is closed.</summary>
    public TimeSpan HelloTimeout { get; set; } = TimeSpan.FromSeconds(15);
}
