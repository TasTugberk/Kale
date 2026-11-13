using System.Collections.Concurrent;

namespace Auth.Client;

/// <summary>
/// A tiny cache whose entries expire after a fixed time, measured with an injectable clock (testable), and can
/// be evicted early (by invalidation events). Misses for the same key at the same time share one load.
/// </summary>
internal sealed class TimedCache<TKey, TValue>(Func<TimeSpan> timeToLiveNow, TimeSpan usableWhileUnreachable, TimeProvider clock)
    where TKey : notnull
{
    private TimeSpan timeToLive => timeToLiveNow();

    // Writes between sweeps of expired entries, so keys that are never asked for again don't stay forever.
    private const int WritesPerSweep = 1_000;

    private readonly ConcurrentDictionary<TKey, (TValue Value, DateTimeOffset ExpiresAt)> _entries = new();

    // Guards _loads and _evictions, and makes "nothing was evicted meanwhile → store" one step, so an eviction
    // can't slip in between.
    private readonly Lock _lock = new();

    // The load running for a key. A role's operations are shared by many users: without this, every request
    // arriving between an expiry and the first answer would ask AuthService itself.
    private readonly Dictionary<TKey, Task<TValue>> _loads = [];

    // Increases on every eviction. A load that started before an eviction must not store its (possibly stale)
    // answer afterwards, or the eviction would be undone for a whole time-to-live.
    private long _evictions;
    private int _writes;

    public int Count => _entries.Count;

    /// <summary>
    /// The cached value, or else the answer of the load running for the key, or else of a new
    /// <paramref name="load"/>. <paramref name="cancellationToken"/> only stops this caller's wait: the load
    /// itself may be shared, so it must not depend on any one caller.
    /// </summary>
    public async Task<TValue> GetOrAddAsync(TKey key, Func<Task<TValue>> load, CancellationToken cancellationToken = default)
    {
        if (_entries.TryGetValue(key, out var entry) && clock.GetUtcNow() < entry.ExpiresAt)
        {
            return entry.Value;
        }

        Task<TValue> loading;
        TaskCompletionSource<TValue>? newLoad = null;
        long evictionsBefore = 0;
        lock (_lock)
        {
            // Again under the lock: a load may have stored its answer (and forgotten itself) since the check above.
            if (_entries.TryGetValue(key, out entry) && clock.GetUtcNow() < entry.ExpiresAt)
            {
                return entry.Value;
            }

            if (!_loads.TryGetValue(key, out loading!))
            {
                // Taken here, under the lock, so a second miss at the same moment waits for this load. Started
                // below, outside the lock, because it runs the caller's code.
                newLoad = new TaskCompletionSource<TValue>(TaskCreationOptions.RunContinuationsAsynchronously);
                loading = newLoad.Task;
                _loads[key] = loading;
                evictionsBefore = _evictions;
            }
        }

        if (newLoad is not null)
        {
            // Not awaited here: it finishes (and stores or forgets its answer) even if this caller gives up.
            _ = LoadAsync(key, load, newLoad, evictionsBefore);
        }

        return await loading.WaitAsync(cancellationToken);
    }

    public void Evict(TKey key)
    {
        lock (_lock)
        {
            _evictions++;
            _entries.TryRemove(key, out _);

            // A load that started before the eviction may bring the old answer: later misses start their own.
            _loads.Remove(key);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _evictions++;
            _entries.Clear();
            _loads.Clear();
        }
    }

    // Never throws: a failure goes to everyone waiting for this load, and the next miss tries again (nothing
    // remembers the failure).
    private async Task LoadAsync(TKey key, Func<Task<TValue>> load, TaskCompletionSource<TValue> loading, long evictionsBefore)
    {
        TValue value;
        try
        {
            value = await load();
        }
        catch (Exception exception)
        {
            lock (_lock)
            {
                ForgetLoad(key, loading.Task);
            }

            loading.SetException(exception);

            // Marks the failure as seen. When every caller has already given up, nobody else reads it, and the
            // host would get it as an unobserved task exception. Callers still waiting get it as before (and the
            // authorization handler logs it).
            _ = loading.Task.Exception;
            return;
        }

        // Answer everyone waiting, but only remember the answer if nothing was evicted meanwhile.
        var stored = false;
        lock (_lock)
        {
            if (_evictions == evictionsBefore)
            {
                _entries[key] = (value, clock.GetUtcNow() + timeToLive);
                stored = true;
            }

            ForgetLoad(key, loading.Task);
        }

        loading.SetResult(value);
        if (stored)
        {
            SweepExpiredNowAndThen();
        }
    }

    // Only its own: after an eviction, a newer load may have taken the key.
    private void ForgetLoad(TKey key, Task<TValue> load)
    {
        if (_loads.TryGetValue(key, out var current) && current == load)
        {
            _loads.Remove(key);
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
