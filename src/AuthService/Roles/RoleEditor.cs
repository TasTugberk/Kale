using System.ComponentModel.DataAnnotations;
using AuthService.Domain;
using AuthService.Outbox;
using AuthService.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Roles;

/// <summary>
/// Changes what a role allows. The change and a "role edited" event are saved together, so client apps reload
/// the role's operations right away (DESIGN.md: editing a role takes effect on active sessions immediately).
/// </summary>
public sealed class RoleEditor(AuthDbContext db)
{
    /// <summary>
    /// Replaces the role's operations with <paramref name="operationNames"/> (e.g. "billing.InvoiceRead"). Each
    /// must be a current (not obsolete) operation of the role's own application.
    /// </summary>
    public async Task SetOperationsAsync(Guid roleId, IReadOnlyCollection<string> operationNames, CancellationToken cancellationToken = default)
    {
        var role = await db.Roles.SingleOrDefaultAsync(r => r.Id == roleId, cancellationToken)
            ?? throw new InvalidOperationException($"No role with id {roleId}.");

        var operations = await db.Operations
            .Where(o => o.ApplicationId == role.ApplicationId && o.ObsoletedAt == null && operationNames.Contains(o.Name))
            .ToListAsync(cancellationToken);

        // The database would also refuse another application's operation (composite FK), but only with a
        // constraint name; this says which name is wrong.
        var notFound = operationNames.Except(operations.Select(o => o.Name)).ToList();
        if (notFound.Count > 0)
        {
            throw new ValidationException(
                $"Not a current operation of this role's application: {string.Join(", ", notFound)}.");
        }

        var wanted = operations.Select(o => o.Id).ToHashSet();
        var granted = await db.RoleOperations.Where(ro => ro.RoleId == roleId).ToListAsync(cancellationToken);
        foreach (var grant in granted)
        {
            if (!wanted.Contains(grant.OperationId))
            {
                db.RoleOperations.Remove(grant);
            }
        }

        var alreadyGranted = granted.Select(g => g.OperationId).ToHashSet();
        foreach (var operation in operations)
        {
            if (!alreadyGranted.Contains(operation.Id))
            {
                db.RoleOperations.Add(new RoleOperation { ApplicationId = role.ApplicationId, RoleId = roleId, OperationId = operation.Id });
            }
        }

        db.OutboxMessages.Add(OutboxEvents.RoleEdited(roleId));
        await db.SaveChangesAsync(cancellationToken);
    }
}
