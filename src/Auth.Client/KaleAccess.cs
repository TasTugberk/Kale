using System.Security.Claims;

namespace Auth.Client;

/// <summary>
/// The operations check in code, for what [Authorize(Operation)] can't express: a decision inside a method ("may
/// this user approve this order?"), or telling a front end which buttons to show. Registered by AddKaleAuth for the
/// application's operations enum. It asks exactly what the attribute asks: the session behind the token's "sid"
/// must be ACTIVE, and its role's operations (implications included) come from the same caches.
/// </summary>
/// <remarks>
/// It fails closed like the attribute: no Kale session (no "sid", an ended or unknown session) holds nothing, and
/// when Kale can't be reached and nothing cached can stand in, it throws <see cref="AuthServiceUnreachableException"/>
/// (answer 503, or show the buttons as unavailable), never a guess. Other failures of the lookup (Kale refused this
/// application's own call) come through as they are. A value that isn't a member of the enum (e.g. a cast number, or
/// a combination of [Flags]) throws InvalidOperationException: a programming error, not a refusal.
/// </remarks>
public sealed class KaleAccess<TOperations>(SessionAccess access, OperationCatalog catalog)
    where TOperations : struct, Enum
{
    /// <summary>Whether the user's Kale session holds <paramref name="operation"/>.</summary>
    public async Task<bool> HasAsync(ClaimsPrincipal user, TOperations operation, CancellationToken cancellationToken = default)
    {
        var operations = await QualifiedOperationsAsync(user, cancellationToken);
        return operations.Contains(catalog.QualifiedName(operation.ToString()));
    }

    /// <summary>Every operation of this application the user's Kale session holds (empty without one).</summary>
    public async Task<IReadOnlySet<TOperations>> GetOperationsAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        var operations = await QualifiedOperationsAsync(user, cancellationToken);
        var held = new HashSet<TOperations>();
        foreach (var member in Enum.GetValues<TOperations>())
        {
            if (operations.Contains(catalog.QualifiedName(member.ToString())))
            {
                held.Add(member);
            }
        }

        return held;
    }

    // "billing.InvoiceRead"-style names, as AuthService returns them; empty when there's no active session.
    private async Task<IReadOnlySet<string>> QualifiedOperationsAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var sessionId = user.FindFirst(OperationAuthorizationHandler.SessionIdClaim)?.Value;
        if (sessionId is null)
        {
            return new HashSet<string>();
        }

        return await access.GetOperationsAsync(sessionId, cancellationToken) ?? new HashSet<string>();
    }
}
