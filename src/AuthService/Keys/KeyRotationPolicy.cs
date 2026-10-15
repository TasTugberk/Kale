namespace AuthService.Keys;

public sealed record KeyRotationOptions
{
    /// <summary>How long one key is used for signing (or encrypting) before its successor takes over.</summary>
    public TimeSpan RotationInterval { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// How long a new key is only published (in JWKS) before it's used. Instances and client apps refresh their
    /// view of the keys periodically; this makes sure everyone knows a key before any token uses it.
    /// </summary>
    public TimeSpan PublishAhead { get; init; } = TimeSpan.FromDays(1);

    /// <summary>
    /// How long an old key stays published after it stopped being used, so tokens it made can still be checked.
    /// Must cover the longest token lifetime (refresh tokens live as long as the 8 h session).
    /// </summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(1);
}

public sealed record KeyAge(Guid Id, DateTimeOffset CreatedAt);

/// <param name="CreateNewKey">A key must be created: there is none, or the successor is due.</param>
/// <param name="SigningKeyId">The key to sign (or encrypt) with now; null only when there are no keys.</param>
/// <param name="KeysToDelete">Old keys past their retention.</param>
public sealed record KeyRotationPlan(bool CreateNewKey, Guid? SigningKeyId, IReadOnlyList<Guid> KeysToDelete);

/// <summary>
/// The lifecycle of one kind of key, as a pure decision over the keys' ages:
/// created → published only (PublishAhead) → in use (RotationInterval) → published only (Retention) → deleted.
/// </summary>
public static class KeyRotationPolicy
{
    public static KeyRotationPlan Plan(IReadOnlyList<KeyAge> keys, DateTimeOffset now, KeyRotationOptions options)
    {
        if (keys.Count == 0)
        {
            return new KeyRotationPlan(CreateNewKey: true, SigningKeyId: null, KeysToDelete: []);
        }

        var newestFirst = keys.OrderByDescending(k => k.CreatedAt).ToList();

        // The newest key that has been published long enough. On first start no key is that old yet,
        // so use the oldest one we have: nothing could have learned about any key before it existed.
        var signing = newestFirst.FirstOrDefault(k => now - k.CreatedAt >= options.PublishAhead) ?? newestFirst[^1];

        // Create the successor early enough to publish it for PublishAhead before the current key's time is up.
        var createNewKey = now - newestFirst[0].CreatedAt >= options.RotationInterval - options.PublishAhead;

        // Delete keys whose use and retention are both over. Never the key in use, even if rotation stalled.
        var toDelete = keys
            .Where(k => k.Id != signing.Id && now - k.CreatedAt >= options.RotationInterval + options.Retention)
            .Select(k => k.Id)
            .ToList();

        return new KeyRotationPlan(createNewKey, signing.Id, toDelete);
    }
}
