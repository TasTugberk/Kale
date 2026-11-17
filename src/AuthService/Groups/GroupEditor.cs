using System.ComponentModel.DataAnnotations;
using AuthService.Admin;
using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Roles;
using AuthService.Sessions;
using AuthService.Users;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AuthService.Groups;

/// <summary>
/// Groups: flat (decision) and global; a group's roles apply to all its members. A change that can take a role
/// away from someone (a member leaves, the group loses a role, the group is deleted) stops the member sessions
/// whose role they no longer hold, with events, and may not leave nobody with auth.Admin.
/// </summary>
public sealed class GroupEditor(AuthDbContext db, SessionStopper sessions, EligibleRoles roles, TimeProvider clock)
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

    /// <summary>
    /// Adds the user to the group, with active access to each application the group has roles in where they had
    /// no access row (<see cref="AccessWithRole"/>). Adding them again changes nothing.
    /// </summary>
    public async Task AddMemberAsync(Guid groupId, Guid userId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Shared: members join side by side, but not while the group's roles change (that locks the group FOR
        // UPDATE), so the roles read below are the ones the new member gets access for.
        await RowLocks.LockGroupForNewMemberAsync(db, groupId, cancellationToken);
        await FindGroupAsync(groupId, cancellationToken);

        // Then the user, as UserAccess does before it creates access: a sign-in or another access change of
        // theirs takes turns with this one.
        await RowLocks.LockUserForChangeAsync(db, userId, cancellationToken);
        await EnsureUserExistsAsync(userId, cancellationToken);
        if (await db.UserGroups.AnyAsync(ug => ug.GroupId == groupId && ug.UserId == userId, cancellationToken))
        {
            return;
        }

        db.UserGroups.Add(new UserGroup { GroupId = groupId, UserId = userId });
        var applicationIds = await (
                from groupRole in db.GroupRoles
                join role in db.Roles on groupRole.RoleId equals role.Id
                where groupRole.GroupId == groupId
                select role.ApplicationId)
            .Distinct()
            .ToListAsync(cancellationToken);
        await AccessWithRole.AddMissingAsync(db, [userId], applicationIds, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Gives the group the role, and its members active access to the role's application where they had no
    /// access row (<see cref="AccessWithRole"/>). Giving it again changes nothing.
    /// </summary>
    public async Task AddRoleAsync(Guid groupId, Guid roleId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Exclusive: a member joining at this moment (who holds the group FOR SHARE, or through their membership
        // row's foreign key) either commits first and is among the members read below, or waits and then sees
        // this role.
        await RowLocks.LockGroupForChangeAsync(db, groupId, cancellationToken);
        await FindGroupAsync(groupId, cancellationToken);
        var applicationId = await db.Roles.Where(r => r.Id == roleId).Select(r => (Guid?)r.ApplicationId).SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException($"No role with id {roleId}.");
        if (await db.GroupRoles.AnyAsync(gr => gr.GroupId == groupId && gr.RoleId == roleId, cancellationToken))
        {
            return;
        }

        var memberIds = await MembersAsync(groupId, cancellationToken);
        // Like UserAccess: sign-ins and other access changes of the members take turns with this one.
        await RowLocks.LockUsersForChangeAsync(db, memberIds, cancellationToken);
        db.GroupRoles.Add(new GroupRole { GroupId = groupId, RoleId = roleId });
        await AccessWithRole.AddMissingAsync(db, memberIds, [applicationId], cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        {
            // The role isn't locked: deleted after the check above, the foreign key stops the insert, and the
            // caller gets the same answer as if it had come later.
            db.ChangeTracker.Clear();
            throw new NotFoundException($"No role with id {roleId}.");
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Takes the user out of the group. Their sessions with a role they no longer hold stop; the last admin may
    /// not leave this way.
    /// </summary>
    public async Task RemoveMemberAsync(Guid groupId, Guid userId, CancellationToken cancellationToken = default)
    {
        await FindGroupAsync(groupId, cancellationToken);
        await EnsureUserExistsAsync(userId, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // A member joining at this moment either commits first and is among the members read next, or waits.
        await RowLocks.LockGroupForChangeAsync(db, groupId, cancellationToken);
        List<Guid> affected = [userId];
        // Sign-in shares this lock: a sign-in in progress finishes first (its session is stopped below) or waits.
        await RowLocks.LockUsersForChangeAsync(db, affected, cancellationToken);
        var adminsBefore = await LastAdminCheck.AdminsAmongAsync(db, affected, cancellationToken);

        var membership = await db.UserGroups.SingleOrDefaultAsync(ug => ug.GroupId == groupId && ug.UserId == userId, cancellationToken);
        if (membership is not null)
        {
            db.UserGroups.Remove(membership);
        }

        await db.SaveChangesAsync(cancellationToken);
        await AfterRolesTakenAwayAsync(affected, adminsBefore, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Takes the role from the group. Members' sessions with it stop, unless they still hold it another way; the
    /// last admin may not lose auth.Admin this way.
    /// </summary>
    public async Task RemoveRoleAsync(Guid groupId, Guid roleId, CancellationToken cancellationToken = default)
    {
        await FindGroupAsync(groupId, cancellationToken);
        await EnsureRoleExistsAsync(roleId, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // A member joining at this moment either commits first and is among the members read next, or waits.
        await RowLocks.LockGroupForChangeAsync(db, groupId, cancellationToken);
        var affected = await MembersAsync(groupId, cancellationToken);
        // Sign-in shares this lock: a sign-in in progress finishes first (its session is stopped below) or waits.
        await RowLocks.LockUsersForChangeAsync(db, affected, cancellationToken);
        var adminsBefore = await LastAdminCheck.AdminsAmongAsync(db, affected, cancellationToken);

        var grant = await db.GroupRoles.SingleOrDefaultAsync(gr => gr.GroupId == groupId && gr.RoleId == roleId, cancellationToken);
        if (grant is not null)
        {
            db.GroupRoles.Remove(grant);
        }

        await db.SaveChangesAsync(cancellationToken);
        await AfterRolesTakenAwayAsync(affected, adminsBefore, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Deletes the group (its memberships and roles go with it), or returns false when there is none. Members'
    /// sessions with a role they no longer hold stop; the last admin may not lose auth.Admin this way.
    /// </summary>
    public async Task<bool> DeleteAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        if (!await db.Groups.AnyAsync(g => g.Id == groupId, cancellationToken))
        {
            return false;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // A member joining at this moment either commits first and is among the members read next, or waits.
        await RowLocks.LockGroupForChangeAsync(db, groupId, cancellationToken);
        var affected = await MembersAsync(groupId, cancellationToken);
        // Sign-in shares this lock: a sign-in in progress finishes first (its session is stopped below) or waits.
        await RowLocks.LockUsersForChangeAsync(db, affected, cancellationToken);
        var adminsBefore = await LastAdminCheck.AdminsAmongAsync(db, affected, cancellationToken);

        var group = await db.Groups.SingleOrDefaultAsync(g => g.Id == groupId, cancellationToken);
        if (group is not null)
        {
            db.Groups.Remove(group);
        }

        await db.SaveChangesAsync(cancellationToken);
        await AfterRolesTakenAwayAsync(affected, adminsBefore, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// After a change that took roles away from <paramref name="affected"/> users was saved (not committed, so
    /// these queries see it): if one of <paramref name="adminsBefore"/> lost auth.Admin, someone else must keep
    /// it; then their open sessions with a role they no longer hold stop, with events. A few queries for the
    /// whole group, not a few per member: the users stay locked (and can't sign in) until the commit.
    /// </summary>
    private async Task AfterRolesTakenAwayAsync(List<Guid> affected, List<Guid> adminsBefore, CancellationToken cancellationToken)
    {
        var adminsAfter = await LastAdminCheck.AdminsAmongAsync(db, adminsBefore, cancellationToken);
        if (adminsAfter.Count < adminsBefore.Count)
        {
            await LastAdminCheck.EnsureAnotherAdminRemainsAsync(db, excludedRoleId: null, excludedUserId: null, cancellationToken);
        }

        var lost = await roles.OpenSessionsWithRoleNoLongerHeldAsync(affected, clock.GetUtcNow(), cancellationToken);
        foreach (var session in lost)
        {
            sessions.Stop(session);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private Task<List<Guid>> MembersAsync(Guid groupId, CancellationToken cancellationToken) =>
        db.UserGroups.Where(ug => ug.GroupId == groupId).Select(ug => ug.UserId).ToListAsync(cancellationToken);

    private async Task<Group> FindGroupAsync(Guid groupId, CancellationToken cancellationToken) =>
        await db.Groups.SingleOrDefaultAsync(g => g.Id == groupId, cancellationToken)
            ?? throw new NotFoundException($"No group with id {groupId}.");

    private async Task EnsureUserExistsAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (!await db.Users.AnyAsync(u => u.Id == userId, cancellationToken))
        {
            throw new NotFoundException($"No user with id {userId}.");
        }
    }

    private async Task EnsureRoleExistsAsync(Guid roleId, CancellationToken cancellationToken)
    {
        if (!await db.Roles.AnyAsync(r => r.Id == roleId, cancellationToken))
        {
            throw new NotFoundException($"No role with id {roleId}.");
        }
    }

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
