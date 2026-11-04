using GrimesGolf.Online.Transport;

namespace GrimesGolf.Online.Tests;

/// <summary>Collects a link's events (from any thread) so a test can wait for the one it expects.</summary>
public sealed class Recorder
{
    private readonly List<LinkEvent> _events = new();
    private readonly SemaphoreSlim _signal = new(0);
    private int _read;

    public Recorder(ILobbyLink link) => link.Event += e => { lock (_events) _events.Add(e); _signal.Release(); };

    public IReadOnlyList<LinkEvent> All { get { lock (_events) return _events.ToList(); } }

    /// <summary>The next event of type <typeparamref name="T"/> (matching <paramref name="match"/>), skipping others.</summary>
    public async Task<T> WaitFor<T>(Func<T, bool>? match = null, int ms = 5000) where T : LinkEvent
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (true)
        {
            lock (_events)
            {
                for (; _read < _events.Count; _read++)
                    if (_events[_read] is T t && (match == null || match(t))) { _read++; return t; }
            }
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) throw new TimeoutException($"No {typeof(T).Name}. Seen: {string.Join(", ", All)}");
            await _signal.WaitAsync(left);
        }
    }
}
