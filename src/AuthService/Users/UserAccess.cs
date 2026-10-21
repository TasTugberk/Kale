using System.ComponentModel.DataAnnotations;
using AuthService.Admin;
using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Roles;
using AuthService.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AuthService.Users;

/// <summary>
/// What a user may sign in to: their access to each application, and their directly assigned roles. Taking
/// something away stops the sessions that depended on it, with "session stopped" events, in the same
/// transaction, so client apps refuse them right away.
/// </summary>
public sealed class UserAccess(AuthDbContext db, SessionStopper sessions, EligibleRoles roles)
{
    /// <summary>
    /// Grants or changes the user's access to the application: active or not, and the role picked when they
    /// don't choose (one of this application's roles, or none).
    /// </summary>
    public async Task SetApplicationAccessAsync(
        Guid userId, Guid applicationId, bool isActive, Guid? defaultRoleId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await BeginWithUserLockedAsync(userId, cancellationToken);
        if (!await db.Applications.AnyAsync(a => a.Id == applicationId, cancellationToken))
        {
            throw new InvalidOperationException($"No application with id {applicationId}.");
        }

        if (defaultRoleId is { } roleId && !await db.Roles.AnyAsync(r => r.Id == roleId && r.ApplicationId == applicationId, cancellationToken))
        {
            throw new ValidationException("The default role must be one of this application's roles.");
        }

        var access = await db.UserApplications.SingleOrDefaultAsync(
            a => a.UserId == userId && a.ApplicationId == applicationId, cancellationToken);
        var hadAccess = access is { IsActive: true };
        if (access is null)
        {
            access = new UserApplication { UserId = userId, ApplicationId = applicationId };
            db.UserApplications.Add(access);
        }

        var losesAccess = hadAccess && !isActive;
        if (losesAccess && await IsAuthApplicationAsync(applicationId, cancellationToken)
            && await LastAdminCheck.IsAdminAsync(db, userId, cancellationToken))
        {
            await LastAdminCheck.EnsureAnotherAdminRemainsAsync(db, excludedRoleId: null, excludedUserId: userId, cancellationToken);
        }

        access.IsActive = isActive;
        access.DefaultRoleId = defaultRoleId;
        if (losesAccess)
        {
            var open = await db.Sessions
                .Where(s => s.UserId == userId && s.ApplicationId == applicationId && s.EndedAt == null)
                .ToListAsync(cancellationToken);
            StopAll(open);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Gives the user the role directly. Assigning it again changes nothing.</summary>
    public async Task AssignRoleAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await BeginWithUserLockedAsync(userId, cancellationToken);
        if (!await db.Roles.AnyAsync(r => r.Id == roleId, cancellationToken))
        {
            throw new InvalidOperationException($"No role with id {roleId}.");
        }

        if (!await db.UserRoles.AnyAsync(ur => ur.UserId == userId && ur.RoleId == roleId, cancellationToken))
        {
            db.UserRoles.Add(new UserRole { UserId = userId, RoleId = roleId });
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Takes the directly assigned role away. Sessions using it stop, unless the user still holds the role
    /// through a group.
    /// </summary>
    public async Task UnassignRoleAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await BeginWithUserLockedAsync(userId, cancellationToken);
        var assignment = await db.UserRoles.SingleOrDefaultAsync(ur => ur.UserId == userId && ur.RoleId == roleId, cancellationToken);
        if (assignment is null)
        {
            return;
        }

        var wasAdmin = await LastAdminCheck.IsAdminAsync(db, userId, cancellationToken);
        db.UserRoles.Remove(assignment);
        await db.SaveChangesAsync(cancellationToken);

        // Asked before and after (the removal is saved, not committed), so only a change that really takes
        // auth.Admin away needs another admin: not a role of "auth" without it, nor one also held through a group.
        if (wasAdmin && !await LastAdminCheck.IsAdminAsync(db, userId, cancellationToken))
        {
            await LastAdminCheck.EnsureAnotherAdminRemainsAsync(db, excludedRoleId: null, excludedUserId: userId, cancellationToken);
        }

        // Also after the save, so this sees the assignment gone and only a group can still grant the role.
        if (!await roles.HoldsRoleAsync(userId, roleId, cancellationToken))
        {
            var open = await db.Sessions
                .Where(s => s.UserId == userId && s.RoleId == roleId && s.EndedAt == null)
                .ToListAsync(cancellationToken);
            StopAll(open);
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    // Sign-in takes a shared lock on the user before checking what they may do, so a sign-in in progress either
    // finishes first (and its session is found and stopped here) or waits and sees the change.
    private async Task<IDbContextTransaction> BeginWithUserLockedAsync(
        Guid userId, CancellationToken cancellationToken)
    {
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM users WHERE id = {userId} FOR UPDATE", cancellationToken);
        if (!await db.Users.AnyAsync(u => u.Id == userId, cancellationToken))
        {
            await transaction.DisposeAsync();
            throw new InvalidOperationException($"No user with id {userId}.");
        }

        return transaction;
    }

    private void StopAll(List<Session> open)
    {
        foreach (var session in open)
        {
            sessions.Stop(session);
        }
    }

    private Task<bool> IsAuthApplicationAsync(Guid applicationId, CancellationToken cancellationToken) =>
        db.Applications.AnyAsync(a => a.Id == applicationId && a.Key == AuthApplication.Key, cancellationToken);
}
