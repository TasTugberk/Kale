using System.Collections.Concurrent;

namespace Auth.Client;

/// <summary>
/// A tiny cache whose entries expire after a fixed time, measured with an injectable clock (testable), and can
/// be evicted early (by invalidation events).
/// </summary>
internal sealed class TimedCache<TKey, TValue>(TimeSpan timeToLive, TimeProvider clock)
    where TKey : notnull
{
    // Writes between sweeps of expired entries, so keys that are never asked for again don't stay forever.
    private const int WritesPerSweep = 1_000;

    private readonly ConcurrentDictionary<TKey, (TValue Value, DateTimeOffset ExpiresAt)> _entries = new();

    // Makes "nothing was evicted meanwhile → store" one step, so an eviction can't slip in between.
    private readonly Lock _evictionLock = new();

    // Increases on every eviction. A lookup that started before an eviction must not store its (possibly
    // stale) answer afterwards, or the eviction would be undone for a whole time-to-live.
    private long _evictions;
    private int _writes;

    public int Count => _entries.Count;

    public async Task<TValue> GetOrAddAsync(TKey key, Func<Task<TValue>> load)
    {
        if (_entries.TryGetValue(key, out var entry) && clock.GetUtcNow() < entry.ExpiresAt)
        {
            return entry.Value;
        }

        var evictionsBefore = Interlocked.Read(ref _evictions);
        var value = await load();

        // Still answer this caller, but only remember the answer if nothing was evicted meanwhile.
        var stored = false;
        lock (_evictionLock)
        {
            if (_evictions == evictionsBefore)
            {
                _entries[key] = (value, clock.GetUtcNow() + timeToLive);
                stored = true;
            }
        }

        if (stored)
        {
            SweepExpiredNowAndThen();
        }

        return value;
    }

    public void Evict(TKey key)
    {
        lock (_evictionLock)
        {
            Interlocked.Increment(ref _evictions);
            _entries.TryRemove(key, out _);
        }
    }

    public void Clear()
    {
        lock (_evictionLock)
        {
            Interlocked.Increment(ref _evictions);
            _entries.Clear();
        }
    }

    private void SweepExpiredNowAndThen()
    {
        if (Interlocked.Increment(ref _writes) % WritesPerSweep != 0)
        {
            return;
        }

        var now = clock.GetUtcNow();
        foreach (var (key, entry) in _entries)
        {
            if (now >= entry.ExpiresAt)
            {
                _entries.TryRemove(key, out _);
            }
        }
    }
}
