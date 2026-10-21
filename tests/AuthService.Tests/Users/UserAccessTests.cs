using System.ComponentModel.DataAnnotations;
using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Roles;
using AuthService.Sessions;
using AuthService.Tests.Persistence;
using AuthService.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace AuthService.Tests.Users;

/// <summary>
/// What a user may sign in to: access rows and directly assigned roles. Taking something away stops the
/// sessions that depended on it (with events), so client apps refuse them right away.
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class UserAccessTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Start);
    private AuthDbContext _db = null!;
    private UserAccess _access = null!;
    private User _user = null!;
    private Application _billing = null!;
    private Role _accountant = null!;

    public async Task InitializeAsync()
    {
        _db = postgres.CreateDbContext(_clock);
        await _db.Database.MigrateAsync();
        _access = new UserAccess(_db, new SessionStopper(_db, _clock), new EligibleRoles(_db));
        _user = await _db.AddUser("deniz");
        _billing = await _db.AddApplication("billing");
        _accountant = await _db.AddRole(_billing, "Accountant");
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task Granting_access_creates_an_active_row_with_its_default_role()
    {
        await _access.SetApplicationAccessAsync(_user.Id, _billing.Id, isActive: true, defaultRoleId: _accountant.Id);

        await using var fresh = Fresh();
        var row = await fresh.UserApplications.SingleAsync();
        row.IsActive.ShouldBeTrue();
        row.DefaultRoleId.ShouldBe(_accountant.Id);
    }

    [Fact]
    public async Task A_default_role_of_another_application_is_refused()
    {
        var hr = await _db.AddApplication("hr");
        var payroll = await _db.AddRole(hr, "Payroll");

        var error = await Should.ThrowAsync<ValidationException>(() =>
            _access.SetApplicationAccessAsync(_user.Id, _billing.Id, isActive: true, defaultRoleId: payroll.Id));

        error.Message.ShouldContain("default role");
    }

    [Fact]
    public async Task Deactivating_access_stops_the_users_sessions_in_that_application_only()
    {
        var billingSession = await SignedIn(_accountant);
        var hrSession = await SignedIn(await _db.AddRole(await _db.AddApplication("hr"), "Payroll"));

        await _access.SetApplicationAccessAsync(_user.Id, _billing.Id, isActive: false, defaultRoleId: null);

        await using var fresh = Fresh();
        (await fresh.Sessions.SingleAsync(s => s.Id == billingSession.Id)).EndedAt.ShouldNotBeNull();
        (await fresh.Sessions.SingleAsync(s => s.Id == hrSession.Id)).EndedAt.ShouldBeNull();
        (await fresh.OutboxMessages.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Unassigning_a_role_stops_the_sessions_using_it()
    {
        var session = await SignedIn(_accountant);

        await _access.UnassignRoleAsync(_user.Id, _accountant.Id);

        await using var fresh = Fresh();
        (await fresh.UserRoles.AnyAsync()).ShouldBeFalse();
        (await fresh.Sessions.SingleAsync(s => s.Id == session.Id)).EndedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_role_still_held_through_a_group_keeps_its_sessions()
    {
        var session = await SignedIn(_accountant);
        var group = await _db.AddGroup("Accounting");
        _db.UserGroups.Add(new UserGroup { UserId = _user.Id, GroupId = group.Id });
        _db.GroupRoles.Add(new GroupRole { GroupId = group.Id, RoleId = _accountant.Id });
        await _db.SaveChangesAsync();

        await _access.UnassignRoleAsync(_user.Id, _accountant.Id);

        await using var fresh = Fresh();
        (await fresh.Sessions.SingleAsync(s => s.Id == session.Id)).EndedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Assigning_a_role_twice_is_harmless()
    {
        await _access.AssignRoleAsync(_user.Id, _accountant.Id);
        await _access.AssignRoleAsync(_user.Id, _accountant.Id);

        await using var fresh = Fresh();
        (await fresh.UserRoles.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Unknown_users_applications_and_roles_are_errors()
    {
        var unknown = Guid.CreateVersion7();

        await Should.ThrowAsync<InvalidOperationException>(() => _access.SetApplicationAccessAsync(unknown, _billing.Id, true, null));
        await Should.ThrowAsync<InvalidOperationException>(() => _access.SetApplicationAccessAsync(_user.Id, unknown, true, null));
        await Should.ThrowAsync<InvalidOperationException>(() => _access.AssignRoleAsync(_user.Id, unknown));
    }

    // The user with access to the role's application, holding the role directly, signed in with it.
    private async Task<Session> SignedIn(Role role)
    {
        if (!await _db.UserApplications.AnyAsync(a => a.UserId == _user.Id && a.ApplicationId == role.ApplicationId))
        {
            _db.UserApplications.Add(new UserApplication { UserId = _user.Id, ApplicationId = role.ApplicationId });
        }

        _db.UserRoles.Add(new UserRole { UserId = _user.Id, RoleId = role.Id });
        var session = new Session { UserId = _user.Id, ApplicationId = role.ApplicationId, RoleId = role.Id, ExpiresAt = Start.AddHours(8) };
        _db.Sessions.Add(session);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return session;
    }

    private AuthDbContext Fresh() => postgres.CreateDbContext(_db.Database.GetDbConnection().Database, _clock);
}
