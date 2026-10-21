using System.ComponentModel.DataAnnotations;
using AuthService.Domain;
using AuthService.Groups;
using AuthService.Persistence;
using AuthService.Roles;
using AuthService.Sessions;
using AuthService.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace AuthService.Tests.Groups;

/// <summary>
/// Groups (flat, global) give their roles to their members. Taking a member, a role or the whole group away
/// stops the member sessions whose role they no longer hold, with events.
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class GroupEditorTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Start);
    private AuthDbContext _db = null!;
    private GroupEditor _groups = null!;
    private User _user = null!;
    private Application _billing = null!;
    private Role _accountant = null!;

    public async Task InitializeAsync()
    {
        _db = postgres.CreateDbContext(_clock);
        await _db.Database.MigrateAsync();
        _groups = new GroupEditor(_db, new SessionStopper(_db, _clock), new EligibleRoles(_db));
        _user = await _db.AddUser("deniz");
        _billing = await _db.AddApplication("billing");
        _accountant = await _db.AddRole(_billing, "Accountant");
        _db.UserApplications.Add(new UserApplication { UserId = _user.Id, ApplicationId = _billing.Id });
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task A_group_is_created_with_a_unique_trimmed_name()
    {
        var group = await _groups.CreateAsync("  Finance  ", " Money people ");

        group.Name.ShouldBe("Finance");
        group.Description.ShouldBe("Money people");
        (await Should.ThrowAsync<ValidationException>(() => _groups.CreateAsync("Finance", null))).Message.ShouldContain("already");
        await Should.ThrowAsync<ValidationException>(() => _groups.CreateAsync(" ", null));
        await Should.ThrowAsync<ValidationException>(() => _groups.CreateAsync(new string('g', 101), null));
    }

    [Fact]
    public async Task Removing_a_member_stops_sessions_of_roles_they_only_had_through_the_group()
    {
        var group = await GroupWithRole(_accountant);
        await _groups.AddMemberAsync(group.Id, _user.Id);
        var throughGroup = await SignedIn(_accountant);
        var direct = await _db.AddRole(_billing, "Viewer");
        _db.UserRoles.Add(new UserRole { UserId = _user.Id, RoleId = direct.Id });
        await _db.SaveChangesAsync();
        var directSession = await SignedIn(direct);

        await _groups.RemoveMemberAsync(group.Id, _user.Id);

        await using var fresh = Fresh();
        (await fresh.Sessions.SingleAsync(s => s.Id == throughGroup.Id)).EndedAt.ShouldNotBeNull();
        (await fresh.Sessions.SingleAsync(s => s.Id == directSession.Id)).EndedAt.ShouldBeNull();
        (await fresh.OutboxMessages.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Taking_a_role_from_the_group_stops_members_sessions_with_it()
    {
        var group = await GroupWithRole(_accountant);
        await _groups.AddMemberAsync(group.Id, _user.Id);
        var session = await SignedIn(_accountant);

        await _groups.RemoveRoleAsync(group.Id, _accountant.Id);

        await using var fresh = Fresh();
        (await fresh.Sessions.SingleAsync(s => s.Id == session.Id)).EndedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_role_still_held_directly_keeps_its_sessions()
    {
        var group = await GroupWithRole(_accountant);
        await _groups.AddMemberAsync(group.Id, _user.Id);
        _db.UserRoles.Add(new UserRole { UserId = _user.Id, RoleId = _accountant.Id });
        await _db.SaveChangesAsync();
        var session = await SignedIn(_accountant);

        await _groups.RemoveRoleAsync(group.Id, _accountant.Id);

        await using var fresh = Fresh();
        (await fresh.Sessions.SingleAsync(s => s.Id == session.Id)).EndedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Deleting_the_group_stops_sessions_that_depended_on_it()
    {
        var group = await GroupWithRole(_accountant);
        await _groups.AddMemberAsync(group.Id, _user.Id);
        var session = await SignedIn(_accountant);

        (await _groups.DeleteAsync(group.Id)).ShouldBeTrue();

        await using var fresh = Fresh();
        (await fresh.Groups.AnyAsync()).ShouldBeFalse();
        (await fresh.Sessions.SingleAsync(s => s.Id == session.Id)).EndedAt.ShouldNotBeNull();
        (await _groups.DeleteAsync(group.Id)).ShouldBeFalse("already gone");
    }

    [Fact]
    public async Task A_sign_in_still_in_progress_finishes_first_and_its_session_is_stopped_too()
    {
        var group = await GroupWithRole(_accountant);
        await _groups.AddMemberAsync(group.Id, _user.Id);

        // A sign-in holds a shared lock on the user from its checks until its session is saved.
        await using var signIn = Fresh();
        await using var transaction = await signIn.Database.BeginTransactionAsync();
        await signIn.Database.ExecuteSqlAsync($"SELECT 1 FROM users WHERE id = {_user.Id} FOR SHARE");

        var remove = _groups.RemoveMemberAsync(group.Id, _user.Id);
        await Task.Delay(500);
        remove.IsCompleted.ShouldBeFalse("leaving the group must wait for the sign-in");

        var lateSession = new Session { UserId = _user.Id, ApplicationId = _billing.Id, RoleId = _accountant.Id, ExpiresAt = Start.AddHours(8) };
        signIn.Sessions.Add(lateSession);
        await signIn.SaveChangesAsync();
        await transaction.CommitAsync();
        await remove;

        await using var fresh = Fresh();
        (await fresh.Sessions.SingleAsync(s => s.Id == lateSession.Id)).EndedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Adding_a_member_or_a_role_twice_is_harmless()
    {
        var group = await GroupWithRole(_accountant);
        await _groups.AddRoleAsync(group.Id, _accountant.Id);
        await _groups.AddMemberAsync(group.Id, _user.Id);
        await _groups.AddMemberAsync(group.Id, _user.Id);

        await using var fresh = Fresh();
        (await fresh.GroupRoles.CountAsync()).ShouldBe(1);
        (await fresh.UserGroups.CountAsync()).ShouldBe(1);
    }

    private async Task<Group> GroupWithRole(Role role)
    {
        var group = await _groups.CreateAsync($"Group-{Guid.CreateVersion7():N}", null);
        await _groups.AddRoleAsync(group.Id, role.Id);
        return group;
    }

    private async Task<Session> SignedIn(Role role)
    {
        var session = new Session { UserId = _user.Id, ApplicationId = role.ApplicationId, RoleId = role.Id, ExpiresAt = Start.AddHours(8) };
        _db.Sessions.Add(session);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return session;
    }

    private AuthDbContext Fresh() => postgres.CreateDbContext(_db.Database.GetDbConnection().Database, _clock);
}
