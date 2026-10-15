namespace AuthService.Keys;

public sealed record KeyRotationOptions
{
    /// <summary>How often a new key is created. Each key is then used for this long, starting PublishAhead after its creation.</summary>
    public TimeSpan RotationInterval { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// How long a new key is only published (in JWKS) before it's used. Instances and client apps refresh their
    /// view of the keys periodically; this makes sure everyone knows a key before any token uses it.
    /// </summary>
    public TimeSpan PublishAhead { get; init; } = TimeSpan.FromDays(1);

    /// <summary>
    /// How long an old key stays published after its successor took over, so tokens it made can still be checked.
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
/// Every step is measured from facts the keys themselves carry, so a refresh that runs late shifts the schedule
/// but never shortens a step.
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

        var createNewKey = now - newestFirst[0].CreatedAt >= options.RotationInterval;

        // Keys older than the one in use stopped being used when it took over (PublishAhead after its creation).
        // Delete them once Retention has passed since then. Retention must not count from a key's own age: if a
        // refresh ran late, an old key may have been in use until moments ago.
        var signingTookOverAt = signing.CreatedAt + options.PublishAhead;
        var toDelete = now - signingTookOverAt >= options.Retention
            ? keys.Where(k => k.CreatedAt < signing.CreatedAt).Select(k => k.Id).ToList()
            : [];

        return new KeyRotationPlan(createNewKey, signing.Id, toDelete);
    }
}
