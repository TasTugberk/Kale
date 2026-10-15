using System.Security.Cryptography;
using AuthService.Domain;
using AuthService.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace AuthService.Keys;

/// <summary>The key to use now, and every key to publish or accept (including <see cref="Current"/>).</summary>
public sealed record KeySet(SecurityKey Current, IReadOnlyList<SecurityKey> All);

public sealed record ServerKeys(KeySet Signing, KeySet Encryption);

/// <summary>
/// Generates, stores and rotates the server's RSA keys (DESIGN.md: "keys are generated and rotated inside the
/// auth service"), following <see cref="KeyRotationPolicy"/>. Private keys are encrypted with Data Protection,
/// whose own key ring is also in the database, so every instance can read them.
/// </summary>
public sealed class KeyRing(
    AuthDbContext db, IDataProtectionProvider dataProtection, TimeProvider clock, KeyRotationOptions options)
{
    // Our own advisory-lock namespace (registration uses 1001).
    private const int KeyRotationLockNamespace = 1002;
    private const int RsaKeySize = 2048;

    private readonly IDataProtector _protector = dataProtection.CreateProtector("Kale.AuthService.ServerKeys");

    /// <summary>Applies the rotation policy (creating or deleting keys as needed) and returns the keys to use.</summary>
    public async Task<ServerKeys> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Instances refresh at the same moment on startup. The lock makes them take turns, so only the first
        // creates keys and the others find them.
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock({KeyRotationLockNamespace}, 0)", cancellationToken);

        var signing = await RotateAsync(KeyUse.Signing, cancellationToken);
        var encryption = await RotateAsync(KeyUse.Encryption, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new ServerKeys(ToKeySet(signing), ToKeySet(encryption));
    }

    // Returns the keys of this use that remain, in a state where the policy finds a current one.
    private async Task<List<ServerKey>> RotateAsync(KeyUse use, CancellationToken cancellationToken)
    {
        var keys = await db.ServerKeys.Where(k => k.Use == use).ToListAsync(cancellationToken);
        var plan = KeyRotationPolicy.Plan(Ages(keys), clock.GetUtcNow(), options);

        foreach (var key in keys.Where(k => plan.KeysToDelete.Contains(k.Id)).ToList())
        {
            db.ServerKeys.Remove(key);
            keys.Remove(key);
        }

        if (plan.CreateNewKey)
        {
            var key = new ServerKey { Use = use, ProtectedPrivateKey = _protector.Protect(NewPrivateKeyPem()) };
            db.ServerKeys.Add(key);

            // SaveChanges would set this too; set it now so the policy sees the real age below.
            key.CreatedAt = clock.GetUtcNow();
            keys.Add(key);
        }

        return keys;
    }

    private KeySet ToKeySet(List<ServerKey> keys)
    {
        var plan = KeyRotationPolicy.Plan(Ages(keys), clock.GetUtcNow(), options);
        var securityKeys = keys.ToDictionary(k => k.Id, ToSecurityKey);

        // Current first: OpenIddict signs with the first signing key it's given.
        var current = securityKeys[plan.SigningKeyId!.Value];
        var all = securityKeys.Values.Where(k => k != current).Prepend(current).ToList();
        return new KeySet(current, all);
    }

    private SecurityKey ToSecurityKey(ServerKey key)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(_protector.Unprotect(key.ProtectedPrivateKey));
        return new RsaSecurityKey(rsa) { KeyId = key.Id.ToString("N") };
    }

    private static List<KeyAge> Ages(List<ServerKey> keys) => keys.Select(k => new KeyAge(k.Id, k.CreatedAt)).ToList();

    private static string NewPrivateKeyPem()
    {
        using var rsa = RSA.Create(RsaKeySize);
        return rsa.ExportRSAPrivateKeyPem();
    }
}
