using System.ComponentModel.DataAnnotations;
using AuthService.Admin;
using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Roles;
using AuthService.Sessions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AuthService.Groups;

/// <summary>
/// Groups: flat (decision) and global; a group's roles apply to all its members. A change that can take a role
/// away from someone (a member leaves, the group loses a role, the group is deleted) stops the member sessions
/// whose role they no longer hold, with events, and may not leave nobody with auth.Admin.
/// </summary>
public sealed class GroupEditor(AuthDbContext db, SessionStopper sessions, EligibleRoles roles)
{
    // Same limits as the columns, checked first for a readable error.
    private const int MaxNameLength = 100;
    private const int MaxDescriptionLength = 500;

    public async Task<Group> CreateAsync(string name, string? description, CancellationToken cancellationToken = default)
    {
        var group = new Group { Name = CheckedName(name), Description = CheckedDescription(description) };
        await EnsureNameIsFreeAsync(group.Name, exceptGroupId: null, cancellationToken);
        db.Groups.Add(group);
        await SaveWithUniqueNameAsync(group.Name, cancellationToken);
        return group;
    }

    public async Task RenameAsync(Guid groupId, string name, string? description, CancellationToken cancellationToken = default)
    {
        var group = await FindGroupAsync(groupId, cancellationToken);
        group.Name = CheckedName(name);
        group.Description = CheckedDescription(description);
        await EnsureNameIsFreeAsync(group.Name, exceptGroupId: groupId, cancellationToken);
        await SaveWithUniqueNameAsync(group.Name, cancellationToken);
    }

    /// <summary>Adds the user to the group. Adding them again changes nothing.</summary>
    public async Task AddMemberAsync(Guid groupId, Guid userId, CancellationToken cancellationToken = default)
    {
        await FindGroupAsync(groupId, cancellationToken);
        if (!await db.Users.AnyAsync(u => u.Id == userId, cancellationToken))
        {
            throw new InvalidOperationException($"No user with id {userId}.");
        }

        if (!await db.UserGroups.AnyAsync(ug => ug.GroupId == groupId && ug.UserId == userId, cancellationToken))
        {
            db.UserGroups.Add(new UserGroup { GroupId = groupId, UserId = userId });
            await SaveAddedAsync(cancellationToken);
        }
    }

    /// <summary>Gives the group the role. Giving it again changes nothing.</summary>
    public async Task AddRoleAsync(Guid groupId, Guid roleId, CancellationToken cancellationToken = default)
    {
        await FindGroupAsync(groupId, cancellationToken);
        if (!await db.Roles.AnyAsync(r => r.Id == roleId, cancellationToken))
        {
            throw new InvalidOperationException($"No role with id {roleId}.");
        }

        if (!await db.GroupRoles.AnyAsync(gr => gr.GroupId == groupId && gr.RoleId == roleId, cancellationToken))
        {
            db.GroupRoles.Add(new GroupRole { GroupId = groupId, RoleId = roleId });
            await SaveAddedAsync(cancellationToken);
        }
    }

    public async Task RemoveMemberAsync(Guid groupId, Guid userId, CancellationToken cancellationToken = default)
    {
        await TakeAwayAsync(groupId, onlyUserId: userId, async () =>
        {
            var membership = await db.UserGroups.SingleOrDefaultAsync(ug => ug.GroupId == groupId && ug.UserId == userId, cancellationToken);
            if (membership is not null)
            {
                db.UserGroups.Remove(membership);
            }
        }, cancellationToken);
    }

    public async Task RemoveRoleAsync(Guid groupId, Guid roleId, CancellationToken cancellationToken = default)
    {
        await TakeAwayAsync(groupId, onlyUserId: null, async () =>
        {
            var grant = await db.GroupRoles.SingleOrDefaultAsync(gr => gr.GroupId == groupId && gr.RoleId == roleId, cancellationToken);
            if (grant is not null)
            {
                db.GroupRoles.Remove(grant);
            }
        }, cancellationToken);
    }

    /// <summary>Deletes the group (its memberships and roles go with it), or returns false when there is none.</summary>
    public async Task<bool> DeleteAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        if (!await db.Groups.AnyAsync(g => g.Id == groupId, cancellationToken))
        {
            return false;
        }

        await TakeAwayAsync(groupId, onlyUserId: null, async () =>
        {
            var group = await db.Groups.SingleOrDefaultAsync(g => g.Id == groupId, cancellationToken);
            if (group is not null)
            {
                db.Groups.Remove(group);
            }
        }, cancellationToken);
        return true;
    }

    /// <summary>
    /// Runs a change to the group that can take roles away from its members (or only from
    /// <paramref name="onlyUserId"/>), in one transaction: lock the group and those users (the lock sign-in
    /// shares), apply and save the change, check that an admin remains if one of them lost auth.Admin, then stop
    /// their sessions with a role they no longer hold.
    /// </summary>
    private async Task TakeAwayAsync(Guid groupId, Guid? onlyUserId, Func<Task> change, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The group first: a member joining at this moment (their membership row locks the group too, through
        // its foreign key) either commits first and is among the members read below, or waits.
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM groups WHERE id = {groupId} FOR UPDATE", cancellationToken);
        List<Guid> userIds = onlyUserId is { } userId ? [userId] : await MembersAsync(groupId, cancellationToken);

        // In id order, so two changes that lock several of the same users can't deadlock.
        var ids = userIds.ToArray();
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM users WHERE id = ANY({ids}) ORDER BY id FOR UPDATE", cancellationToken);

        var adminsBefore = new List<Guid>();
        foreach (var memberId in userIds)
        {
            if (await LastAdminCheck.IsAdminAsync(db, memberId, cancellationToken))
            {
                adminsBefore.Add(memberId);
            }
        }

        await change();
        await db.SaveChangesAsync(cancellationToken);

        // Saved (not committed), so these see the change.
        foreach (var adminId in adminsBefore)
        {
            if (!await LastAdminCheck.IsAdminAsync(db, adminId, cancellationToken))
            {
                await LastAdminCheck.EnsureAnotherAdminRemainsAsync(db, excludedRoleId: null, excludedUserId: null, cancellationToken);
                break;
            }
        }

        foreach (var memberId in userIds)
        {
            var open = await db.Sessions.Where(s => s.UserId == memberId && s.EndedAt == null).ToListAsync(cancellationToken);
            foreach (var session in open)
            {
                if (!await roles.HoldsRoleAsync(memberId, session.RoleId, cancellationToken))
                {
                    sessions.Stop(session);
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // The same member or role added twice at the same moment: both pass the "already there?" check and the
    // second hits the primary key. It's there either way, which is what was asked.
    private async Task SaveAddedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
        }
    }

    private Task<List<Guid>> MembersAsync(Guid groupId, CancellationToken cancellationToken) =>
        db.UserGroups.Where(ug => ug.GroupId == groupId).Select(ug => ug.UserId).ToListAsync(cancellationToken);

    private async Task<Group> FindGroupAsync(Guid groupId, CancellationToken cancellationToken) =>
        await db.Groups.SingleOrDefaultAsync(g => g.Id == groupId, cancellationToken)
            ?? throw new InvalidOperationException($"No group with id {groupId}.");

    private static string CheckedName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ValidationException("A group needs a name.");
        }

        var trimmed = name.Trim();
        if (trimmed.Length > MaxNameLength)
        {
            throw new ValidationException($"A group name can be at most {MaxNameLength} characters.");
        }

        return trimmed;
    }

    private static string? CheckedDescription(string? description)
    {
        var trimmed = description?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > MaxDescriptionLength)
        {
            throw new ValidationException($"A group description can be at most {MaxDescriptionLength} characters.");
        }

        return trimmed;
    }

    private async Task EnsureNameIsFreeAsync(string name, Guid? exceptGroupId, CancellationToken cancellationToken)
    {
        if (await db.Groups.AnyAsync(g => g.Name == name && g.Id != exceptGroupId, cancellationToken))
        {
            throw new ValidationException($"There is already a group '{name}'.");
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
            throw new ValidationException($"There is already a group '{name}'.", exception);
        }
    }
}
