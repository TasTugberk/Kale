using AuthService.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Operations;

/// <summary>
/// Answers "which operations does this role hold?" for RoleService.GetEffectiveOperations:
/// the role's granted operations plus everything they imply, by qualified name.
/// </summary>
public sealed class EffectiveOperations(AuthDbContext db)
{
    /// <summary>
    /// Returns null when the role doesn't exist <b>or belongs to another application</b>, so the caller
    /// (answering NOT_FOUND) can't tell the two apart and learns nothing about other applications.
    /// </summary>
    public async Task<IReadOnlySet<string>?> ForRoleAsync(
        Guid applicationId, Guid roleId, CancellationToken cancellationToken = default)
    {
        var roleBelongsToApplication = await db.Roles
            .AnyAsync(r => r.Id == roleId && r.ApplicationId == applicationId, cancellationToken);
        if (!roleBelongsToApplication)
        {
            return null;
        }

        // Obsolete operations are left out entirely: not granted, and not followed as an implication. That also
        // cuts chains through them (A -> obsolete B -> C gives only A): once B leaves the enum, re-registration
        // drops A's [Implies(B)] anyway, and if A should grant C the enum says so directly.
        var granted = await (
                from roleOperation in db.RoleOperations
                join operation in db.Operations on roleOperation.OperationId equals operation.Id
                where roleOperation.RoleId == roleId && operation.ObsoletedAt == null
                select operation.Name)
            .ToListAsync(cancellationToken);

        var implications = await (
                from implication in db.OperationImplications
                join source in db.Operations on implication.OperationId equals source.Id
                join target in db.Operations on implication.ImpliedOperationId equals target.Id
                // Only limits which rows are loaded. Isolation comes from the composite foreign keys, which keep
                // both ends of an implication in the same application as the implication row.
                where implication.ApplicationId == applicationId
                    // An obsolete target is never reached; an obsolete source can't be reached either, since it's
                    // neither granted nor the target of a loaded implication.
                    && target.ObsoletedAt == null
                select new { Source = source.Name, Target = target.Name })
            .ToListAsync(cancellationToken);

        return OperationImplications.Expand(granted, implications.ToLookup(i => i.Source, i => i.Target));
    }
}
