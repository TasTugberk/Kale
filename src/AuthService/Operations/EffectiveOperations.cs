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

        // Obsolete operations are left out entirely: not granted, and not followed as an implication.
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
                where implication.ApplicationId == applicationId
                    && source.ObsoletedAt == null
                    && target.ObsoletedAt == null
                select new { Source = source.Name, Target = target.Name })
            .ToListAsync(cancellationToken);

        return OperationImplications.Expand(granted, implications.ToLookup(i => i.Source, i => i.Target));
    }
}
