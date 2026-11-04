using System.Security.Cryptography;
using System.Text.Json;

namespace SquareGspro.Relay;

/// <summary>
/// Every lobby, in memory. A lobby is a host PC (peer 0) and up to three friends' PCs (peers 1, 2, …), each known by
/// a secret token so it can drop and come back as the same peer. Messages go from the host to one friend or all of
/// them, and from a friend to the host; what's in them is never looked at.
/// <para>
/// A lobby lasts while its host is connected, up to <see cref="RelayOptions.MaxLobbyAge"/>, and for
/// <see cref="RelayOptions.HostGrace"/> after the host drops. If the relay restarts everything is lost, so a host
/// that comes back asks for its old code again (and gets it, if it's free) and its friends rejoin with theirs.
/// </para>
/// All state changes happen under one lock; sending only queues, so nothing waits on the network inside it.
/// </summary>
public sealed class LobbyRegistry
{
    /// <summary>Letters and digits that can't be mistaken for each other when read out (no I, L, O, 0, 1).</summary>
    public const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    public const int CodeLength = 5;

    private readonly object _lock = new();
    private readonly Dictionary<string, Lobby> _lobbies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _attempts = new(StringComparer.Ordinal);
    private readonly RelayOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<LobbyRegistry>? _log;

    public LobbyRegistry(RelayOptions options, TimeProvider time, ILogger<LobbyRegistry>? log = null)
    {
        _options = options;
        _time = time;
        _log = log;
    }

    private sealed class Lobby(string code, string hostToken, DateTimeOffset created)
    {
        public string Code { get; } = code;
        public string HostToken { get; } = hostToken;
        public DateTimeOffset Created { get; } = created;
        public DateTimeOffset? HostLeftAt { get; set; }
        public RelayConnection? Host { get; set; }
        public Dictionary<string, Member> Members { get; } = new(StringComparer.Ordinal);
        public int NextPeer { get; set; } = 1;
    }

    private sealed class Member(int peer)
    {
        public int Peer { get; } = peer;
        public RelayConnection? Connection { get; set; }
    }

    public int LobbyCount { get { lock (_lock) return _lobbies.Count; } }

    public int PeerCount
    {
        get
        {
            lock (_lock) return _lobbies.Values.Sum(l => (l.Host != null ? 1 : 0) + l.Members.Values.Count(m => m.Connection != null));
        }
    }

    public static bool IsValidCode(string? code) => code is { Length: CodeLength } && code.All(c => CodeAlphabet.Contains(c));

    private static bool IsValidToken(string? token) => token is { Length: >= 16 and <= 128 };

    // ---- create / join --------------------------------------------------------------------------

    /// <summary>
    /// A host makes a lobby, with a new code, or with <paramref name="code"/> to take its own lobby back (after a
    /// drop, or a relay restart that lost it).
    /// </summary>
    public void Create(RelayConnection c, string? code, string? token, string ip)
    {
        lock (_lock)
        {
            if (c.Bound) { c.Fail("bad-request", "Already in a lobby."); return; }
            if (!IsValidToken(token)) { c.Fail("bad-request", "Missing token."); return; }
            if (!Allow(ip)) { c.Fail("rate", "Too many attempts. Wait a minute and try again."); return; }
            if (code != null)
            {
                code = code.ToUpperInvariant();
                if (!IsValidCode(code)) { c.Fail("bad-code", "That isn't a lobby code."); return; }
                if (_lobbies.TryGetValue(code, out var mine))
                {
                    if (!FixedTimeEquals(mine.HostToken, token!)) { c.Fail("code-taken", "That code is in use by another lobby."); return; }
                    var old = mine.Host;
                    mine.Host = c;
                    mine.HostLeftAt = null;
                    c.Bind(mine.Code, 0);
                    old?.Fail("replaced", "This lobby was opened again somewhere else.");
                    c.Send(Wire("created", ("code", mine.Code), ("peer", 0), ("resumed", true)));
                    foreach (var m in mine.Members.Values.Where(m => m.Connection != null))
                    {
                        m.Connection!.Send(Wire("host-back"));
                        c.Send(Wire("peer-joined", ("peer", m.Peer)));
                    }
                    return;
                }
            }
            if (_lobbies.Count >= _options.MaxLobbies) { c.Fail("busy", "The relay is full. Try again later."); return; }
            code ??= NewCode();
            var lobby = new Lobby(code, token!, _time.GetUtcNow()) { Host = c };
            _lobbies[code] = lobby;
            c.Bind(code, 0);
            c.Send(Wire("created", ("code", code), ("peer", 0), ("resumed", false)));
            LogCounts("lobby opened");
        }
    }

    /// <summary>A friend's PC joins a lobby by its code, or comes back to it as the same peer (same token).</summary>
    public void Join(RelayConnection c, string? code, string? token, string ip)
    {
        lock (_lock)
        {
            if (c.Bound) { c.Fail("bad-request", "Already in a lobby."); return; }
            if (!IsValidToken(token)) { c.Fail("bad-request", "Missing token."); return; }
            if (!Allow(ip)) { c.Fail("rate", "Too many attempts. Wait a minute and try again."); return; }
            code = code?.ToUpperInvariant();
            if (!IsValidCode(code) || !_lobbies.TryGetValue(code!, out var lobby)) { c.Fail("not-found", "No lobby with that code."); return; }
            if (FixedTimeEquals(lobby.HostToken, token!)) { c.Fail("bad-request", "That's the host's own lobby."); return; }
            bool rejoined = lobby.Members.TryGetValue(token!, out var member);
            if (!rejoined)
            {
                if (lobby.Members.Count + 1 >= _options.MaxPcsPerLobby) { c.Fail("full", "That lobby is full."); return; }
                member = new Member(lobby.NextPeer++);
                lobby.Members[token!] = member;
            }
            var old = member!.Connection;
            member.Connection = c;
            c.Bind(lobby.Code, member.Peer);
            old?.Fail("replaced", "This PC joined again on a new connection.");
            c.Send(Wire("joined", ("code", lobby.Code), ("peer", member.Peer), ("rejoined", rejoined), ("host", lobby.Host != null)));
            lobby.Host?.Send(Wire("peer-joined", ("peer", member.Peer)));
            LogCounts("peer joined");
        }
    }

    // ---- messages -------------------------------------------------------------------------------

    /// <summary>
    /// Passes a message on, untouched: from the host to peer <paramref name="to"/> (everyone when null), or from a
    /// friend to the host. Dropped if there's nobody there to take it.
    /// </summary>
    public void Forward(RelayConnection from, int? to, string data)
    {
        lock (_lock)
        {
            if (!from.Bound || !_lobbies.TryGetValue(from.Code!, out var lobby) || !IsCurrent(lobby, from)) return;
            string msg = Wire("msg", ("from", from.Peer), ("data", data));
            if (from.Peer != 0) { lobby.Host?.Send(msg); return; }
            foreach (var m in lobby.Members.Values)
                if (m.Connection != null && (to == null || to == m.Peer)) m.Connection.Send(msg);
        }
    }

    /// <summary>A connection has closed: the others in its lobby hear about it.</summary>
    public void Left(RelayConnection c)
    {
        lock (_lock)
        {
            if (!c.Bound || !_lobbies.TryGetValue(c.Code!, out var lobby) || !IsCurrent(lobby, c)) return;
            if (c.Peer == 0)
            {
                lobby.Host = null;
                lobby.HostLeftAt = _time.GetUtcNow();
                foreach (var m in lobby.Members.Values) m.Connection?.Send(Wire("host-left"));
            }
            else
            {
                var m = lobby.Members.Values.First(m => m.Peer == c.Peer);
                m.Connection = null;
                lobby.Host?.Send(Wire("peer-left", ("peer", c.Peer)));
            }
            LogCounts("peer left");
        }
    }

    /// <summary>Closes lobbies that are too old, or whose host has been gone too long.</summary>
    public void Sweep()
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            foreach (var lobby in _lobbies.Values.ToList())
            {
                bool old = now - lobby.Created > _options.MaxLobbyAge;
                bool hostless = lobby.HostLeftAt is { } left && now - left > _options.HostGrace;
                if (!old && !hostless) continue;
                _lobbies.Remove(lobby.Code);
                string why = old ? "The lobby has been open as long as it can be." : "The host didn't come back.";
                lobby.Host?.Fail("lobby-closed", why);
                foreach (var m in lobby.Members.Values) m.Connection?.Fail("lobby-closed", why);
                LogCounts("lobby closed");
            }
            foreach (var (ip, q) in _attempts.ToList())
            {
                while (q.Count > 0 && now - q.Peek() > _options.JoinWindow) q.Dequeue();
                if (q.Count == 0) _attempts.Remove(ip);
            }
        }
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static bool IsCurrent(Lobby lobby, RelayConnection c) =>
        c.Peer == 0 ? lobby.Host == c : lobby.Members.Values.Any(m => m.Peer == c.Peer && m.Connection == c);

    private bool Allow(string ip)
    {
        var now = _time.GetUtcNow();
        if (!_attempts.TryGetValue(ip, out var q)) _attempts[ip] = q = new Queue<DateTimeOffset>();
        while (q.Count > 0 && now - q.Peek() > _options.JoinWindow) q.Dequeue();
        if (q.Count >= _options.JoinsPerWindow) return false;
        q.Enqueue(now);
        return true;
    }

    private string NewCode()
    {
        while (true)
        {
            var chars = new char[CodeLength];
            for (int i = 0; i < CodeLength; i++) chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
            var code = new string(chars);
            if (!_lobbies.ContainsKey(code)) return code;
        }
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));

    /// <summary>Counts only: no codes, names or addresses go in the log.</summary>
    private void LogCounts(string what) =>
        _log?.LogInformation("{What}: {Lobbies} lobbies, {Peers} PCs connected", what, _lobbies.Count,
            _lobbies.Values.Sum(l => (l.Host != null ? 1 : 0) + l.Members.Values.Count(m => m.Connection != null)));

    /// <summary>One relay message: <c>{"t": type, ...fields}</c>.</summary>
    internal static string Wire(string type, params (string Key, object? Value)[] fields)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("t", type);
            foreach (var (k, v) in fields)
            {
                switch (v)
                {
                    case null: w.WriteNull(k); break;
                    case string s: w.WriteString(k, s); break;
                    case int i: w.WriteNumber(k, i); break;
                    case bool b: w.WriteBoolean(k, b); break;
                    default: throw new ArgumentException($"Unsupported value for {k}.");
                }
            }
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}
