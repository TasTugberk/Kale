using System.ComponentModel.DataAnnotations;
using AuthService.Domain;
using AuthService.Outbox;
using AuthService.Persistence;
using AuthService.Sessions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AuthService.Roles;

/// <summary>
/// Changes what a role allows. The change and a "role edited" event are saved together, so client apps reload
/// the role's operations right away (DESIGN.md: editing a role takes effect on active sessions immediately).
/// </summary>
public sealed class RoleEditor(AuthDbContext db, SessionStopper sessions)
{
    // Same limits as the columns, checked first for a readable error.
    private const int MaxNameLength = 100;
    private const int MaxDescriptionLength = 500;

    public async Task<Role> CreateAsync(Guid applicationId, string name, string? description, CancellationToken cancellationToken = default)
    {
        var role = new Role { ApplicationId = applicationId, Name = CheckedName(name), Description = CheckedDescription(description) };
        await EnsureNameIsFreeAsync(applicationId, role.Name, exceptRoleId: null, cancellationToken);
        db.Roles.Add(role);
        await SaveWithUniqueNameAsync(role.Name, cancellationToken);
        return role;
    }

    public async Task RenameAsync(Guid roleId, string name, string? description, CancellationToken cancellationToken = default)
    {
        var role = await db.Roles.SingleOrDefaultAsync(r => r.Id == roleId, cancellationToken)
            ?? throw new InvalidOperationException($"No role with id {roleId}.");
        role.Name = CheckedName(name);
        role.Description = CheckedDescription(description);
        await EnsureNameIsFreeAsync(role.ApplicationId, role.Name, exceptRoleId: roleId, cancellationToken);
        await SaveWithUniqueNameAsync(role.Name, cancellationToken);
    }

    /// <summary>
    /// Deletes the role. Its sessions are stopped (with "session stopped" events) and removed, as decided:
    /// sessions restrict deleting their role, because a database cascade would remove them without any event.
    /// The role's grants and its user and group assignments go with it.
    /// </summary>
    public async Task DeleteAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Lock the role until commit. A sign-in saving a session with this role holds a lock on it too (the
        // session's foreign key), so it either commits first, and its session is found below, or waits.
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM roles WHERE id = {roleId} FOR UPDATE", cancellationToken);
        var role = await db.Roles.SingleOrDefaultAsync(r => r.Id == roleId, cancellationToken)
            ?? throw new InvalidOperationException($"No role with id {roleId}.");

        var roleSessions = await db.Sessions.Where(s => s.RoleId == roleId).ToListAsync(cancellationToken);
        foreach (var session in roleSessions)
        {
            sessions.Stop(session);
        }

        db.Sessions.RemoveRange(roleSessions);

        // A default role restricts deleting too. Those users simply pick (or get) a role at their next sign-in.
        var defaults = await db.UserApplications.Where(a => a.DefaultRoleId == roleId).ToListAsync(cancellationToken);
        foreach (var access in defaults)
        {
            access.DefaultRoleId = null;
        }

        db.Roles.Remove(role);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static string CheckedName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ValidationException("A role needs a name.");
        }

        var trimmed = name.Trim();
        if (trimmed.Length > MaxNameLength)
        {
            throw new ValidationException($"A role name can be at most {MaxNameLength} characters.");
        }

        return trimmed;
    }

    private static string? CheckedDescription(string? description)
    {
        if (description?.Length > MaxDescriptionLength)
        {
            throw new ValidationException($"A role description can be at most {MaxDescriptionLength} characters.");
        }

        return string.IsNullOrWhiteSpace(description) ? null : description;
    }

    private async Task EnsureNameIsFreeAsync(Guid applicationId, string name, Guid? exceptRoleId, CancellationToken cancellationToken)
    {
        if (await db.Roles.AnyAsync(r => r.ApplicationId == applicationId && r.Name == name && r.Id != exceptRoleId, cancellationToken))
        {
            throw new ValidationException($"This application already has a role '{name}'.");
        }
    }

    // Two saves at once can both pass the check above; the unique index stops the second, which then gets the
    // same answer as if it had come later.
    private async Task SaveWithUniqueNameAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ValidationException($"This application already has a role '{name}'.", exception);
        }
    }

    /// <summary>
    /// Replaces the role's operations with <paramref name="operationNames"/> (e.g. "billing.InvoiceRead"). Each
    /// must be a current (not obsolete) operation of the role's own application.
    /// </summary>
    public async Task SetOperationsAsync(Guid roleId, IReadOnlyCollection<string> operationNames, CancellationToken cancellationToken = default)
    {
        // Two admins saving the same role at once would each work out their changes from the same old grants, and
        // the role would end up with both sets. Locking the role row until commit makes them take turns.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM roles WHERE id = {roleId} FOR UPDATE", cancellationToken);

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
        await transaction.CommitAsync(cancellationToken);
    }
}
