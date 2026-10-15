using System.Collections.Concurrent;

namespace Auth.Client;

/// <summary>
/// A tiny cache whose entries expire after a fixed time, measured with an injectable clock (testable), and can
/// be evicted early (by invalidation events).
/// </summary>
internal sealed class TimedCache<TKey, TValue>(TimeSpan timeToLive, TimeProvider clock)
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, (TValue Value, DateTimeOffset ExpiresAt)> _entries = new();

    public async Task<TValue> GetOrAddAsync(TKey key, Func<Task<TValue>> load)
    {
        if (_entries.TryGetValue(key, out var entry) && clock.GetUtcNow() < entry.ExpiresAt)
        {
            return entry.Value;
        }

        var value = await load();
        _entries[key] = (value, clock.GetUtcNow() + timeToLive);
        return value;
    }

    public void Evict(TKey key) => _entries.TryRemove(key, out _);

    public void Clear() => _entries.Clear();
}
