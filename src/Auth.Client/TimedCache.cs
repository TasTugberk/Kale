using System.Collections.Concurrent;

namespace Auth.Client;

/// <summary>
/// A tiny cache whose entries expire after a time, measured with an injectable clock (testable), and can be
/// evicted early (by invalidation events). Misses for the same key at the same time share one load. While
/// AuthService is unreachable, an expired entry may still be used for a while (<see cref="Freshness"/>).
/// </summary>
/// <param name="timeToLive">Asked at every read, so it can follow a state that changes (the event stream).</param>
/// <param name="usableWhileUnreachable">How long after expiring an entry may stand in for an unreachable AuthService.</param>
internal sealed class TimedCache<TKey, TValue>(Func<TimeSpan> timeToLive, TimeSpan usableWhileUnreachable, TimeProvider clock)
    where TKey : notnull
{
    // Writes between sweeps of expired entries, so keys that are never asked for again don't stay forever.
    private const int WritesPerSweep = 1_000;

    private readonly ConcurrentDictionary<TKey, (TValue Value, DateTimeOffset StoredAt)> _entries = new();

    // Guards _loads and _evictions, and makes "nothing was evicted meanwhile → store" one step, so an eviction
    // can't slip in between.
    private readonly Lock _lock = new();

    // The load running for a key. A role's operations are shared by many users: without this, every request
    // arriving between an expiry and the first answer would ask AuthService itself.
    private readonly Dictionary<TKey, Task<TValue>> _loads = [];

    // Increases whenever an eviction drops a running load. A load that started before an eviction must not store
    // its (possibly stale) answer afterwards, or the eviction would be undone for a whole time-to-live.
    private long _evictions;
    private int _writes;

    // Until when a load that found AuthService unreachable keeps the next misses from asking again (they get the
    // last answer or fail closed at once, instead of each waiting for the lookup timeout). Guarded by _lock.
    private DateTimeOffset _unreachableUntil;

    private enum Freshness
    {
        /// <summary>Within its lifetime: used without asking AuthService.</summary>
        Fresh,

        /// <summary>Expired, but used if AuthService can't be reached right now.</summary>
        UsableWhileUnreachable,

        /// <summary>Too old for anything.</summary>
        Gone,
    }

    public int Count => _entries.Count;

    /// <summary>
    /// The cached value, or else the answer of the load running for the key, or else of a new
    /// <paramref name="load"/>. If that load fails with <see cref="AuthServiceUnreachableException"/>, an expired
    /// entry still within its grace time is returned instead. <paramref name="cancellationToken"/> only stops this
    /// caller's wait: the load itself may be shared, so it must not depend on any one caller.
    /// </summary>
    public async Task<TValue> GetOrAddAsync(TKey key, Func<Task<TValue>> load, CancellationToken cancellationToken = default)
    {
        if (TryGet(key, Freshness.Fresh, out var cached))
        {
            return cached;
        }

        Task<TValue> loading;
        TaskCompletionSource<TValue>? newLoad = null;
        long evictionsBefore = 0;
        lock (_lock)
        {
            // Again under the lock: a load may have stored its answer (and forgotten itself) since the check above.
            if (TryGet(key, Freshness.Fresh, out cached))
            {
                return cached;
            }

            if (clock.GetUtcNow() < _unreachableUntil)
            {
                if (TryGet(key, Freshness.UsableWhileUnreachable, out cached))
                {
                    return cached;
                }

                throw new AuthServiceUnreachableException("AuthService couldn't be reached a moment ago.");
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

        try
        {
            return await loading.WaitAsync(cancellationToken);
        }
        catch (AuthServiceUnreachableException) when (TryGet(key, Freshness.UsableWhileUnreachable, out var stale))
        {
            return stale;
        }
    }

    public void Evict(TKey key)
    {
        lock (_lock)
        {
            _entries.TryRemove(key, out _);

            // A load that started before the eviction may bring the old answer: later misses start their own, and
            // it must not store it. With no load running, nothing can undo this eviction.
            if (_loads.Remove(key))
            {
                _evictions++;
            }
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

    // The one place that decides how old is too old.
    private Freshness FreshnessOf(DateTimeOffset storedAt, DateTimeOffset now)
    {
        var expiresAt = storedAt + timeToLive();
        if (now < expiresAt)
        {
            return Freshness.Fresh;
        }

        return now < expiresAt + usableWhileUnreachable ? Freshness.UsableWhileUnreachable : Freshness.Gone;
    }

    // An entry at least as fresh as asked for (a fresh one is also usable while unreachable).
    private bool TryGet(TKey key, Freshness atLeast, out TValue value)
    {
        if (_entries.TryGetValue(key, out var entry) && FreshnessOf(entry.StoredAt, clock.GetUtcNow()) <= atLeast)
        {
            value = entry.Value;
            return true;
        }

        value = default!;
        return false;
    }

    // Never throws: a failure goes to everyone waiting for this load, and the next miss tries again (after
    // SessionAccess.UnreachableRememberedFor if AuthService was unreachable; the old entry, if any, stays for the
    // grace time).
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
                if (exception is AuthServiceUnreachableException)
                {
                    _unreachableUntil = clock.GetUtcNow() + SessionAccess.UnreachableRememberedFor;
                }

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
            // AuthService answered, so it's reachable: misses ask it again (it may be slow rather than down).
            _unreachableUntil = default;

            if (_evictions == evictionsBefore)
            {
                _entries[key] = (value, clock.GetUtcNow());
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
            if (FreshnessOf(entry.StoredAt, now) == Freshness.Gone)
            {
                _entries.TryRemove(key, out _);
            }
        }
    }
}
