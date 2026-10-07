using AuthService.Domain;
using AuthService.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AuthService.Tests.Persistence;

/// <summary>
/// Users and groups are global; roles belong to one application. These tests pin down what the database
/// allows when the two meet, and what happens to assignments when a user, group or role is deleted.
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class UserAssignmentTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AuthDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _db = postgres.CreateDbContext();
        await _db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    /// <summary>
    /// Without this, EF applies cascade/restrict itself to the rows it is tracking, and the test would prove
    /// EF's behavior instead of the database's.
    /// </summary>
    private void ForgetTrackedRows() => _db.ChangeTracker.Clear();

    [Fact]
    public async Task User_application_default_role_must_belong_to_that_application()
    {
        var billing = await _db.AddApplication("billing");
        var hr = await _db.AddApplication("hr");
        var hrRole = await _db.AddRole(hr, "Payroll");
        var user = await _db.AddUser("deniz");

        _db.UserApplications.Add(new UserApplication { UserId = user.Id, ApplicationId = billing.Id, DefaultRoleId = hrRole.Id });

        await DbAssert.ShouldViolate(
            _db, PostgresErrorCodes.ForeignKeyViolation, "fk_user_applications_default_role_same_application");
    }

    [Fact]
    public async Task User_application_can_have_a_default_role_of_that_application_or_none()
    {
        var billing = await _db.AddApplication("billing");
        var hr = await _db.AddApplication("hr");
        var accountant = await _db.AddRole(billing, "Accountant");
        var user = await _db.AddUser("deniz");

        _db.UserApplications.Add(new UserApplication { UserId = user.Id, ApplicationId = billing.Id, DefaultRoleId = accountant.Id });
        _db.UserApplications.Add(new UserApplication { UserId = user.Id, ApplicationId = hr.Id, DefaultRoleId = null });
        await _db.SaveChangesAsync();

        (await _db.UserApplications.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task A_role_that_is_someones_default_cannot_be_deleted()
    {
        var billing = await _db.AddApplication("billing");
        var accountant = await _db.AddRole(billing, "Accountant");
        var user = await _db.AddUser("deniz");
        _db.UserApplications.Add(new UserApplication { UserId = user.Id, ApplicationId = billing.Id, DefaultRoleId = accountant.Id });
        await _db.SaveChangesAsync();

        ForgetTrackedRows();
        _db.Roles.Remove(accountant);

        // The default must be cleared first (by the admin service), rather than silently disappearing.
        // ON DELETE RESTRICT reports restrict_violation (23001), not foreign_key_violation (23503).
        await DbAssert.ShouldViolate(
            _db, PostgresErrorCodes.RestrictViolation, "fk_user_applications_default_role_same_application");
    }

    [Fact]
    public async Task An_application_that_users_can_access_cannot_be_deleted()
    {
        var billing = await _db.AddApplication("billing");
        var user = await _db.AddUser("deniz");
        _db.UserApplications.Add(new UserApplication { UserId = user.Id, ApplicationId = billing.Id });
        await _db.SaveChangesAsync();

        ForgetTrackedRows();
        _db.Applications.Remove(billing);

        await DbAssert.ShouldViolate(_db, PostgresErrorCodes.RestrictViolation, "fk_user_applications_application");
    }

    [Fact]
    public async Task Group_name_is_unique()
    {
        await _db.AddGroup("Finance");

        _db.Groups.Add(new Group { Name = "Finance" });

        await DbAssert.ShouldViolate(_db, PostgresErrorCodes.UniqueViolation, "ix_groups_name");
    }

    [Fact]
    public async Task Deleting_a_user_removes_their_memberships_roles_and_application_access()
    {
        var billing = await _db.AddApplication("billing");
        var accountant = await _db.AddRole(billing, "Accountant");
        var finance = await _db.AddGroup("Finance");
        var user = await _db.AddUser("deniz");
        _db.UserGroups.Add(new UserGroup { UserId = user.Id, GroupId = finance.Id });
        _db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = accountant.Id });
        _db.UserApplications.Add(new UserApplication { UserId = user.Id, ApplicationId = billing.Id, DefaultRoleId = accountant.Id });
        await _db.SaveChangesAsync();

        ForgetTrackedRows();
        _db.Users.Remove(user);
        await _db.SaveChangesAsync();

        (await _db.UserGroups.CountAsync()).ShouldBe(0);
        (await _db.UserRoles.CountAsync()).ShouldBe(0);
        (await _db.UserApplications.CountAsync()).ShouldBe(0);
        (await _db.Groups.CountAsync()).ShouldBe(1, "the group itself stays");
    }

    [Fact]
    public async Task Deleting_a_role_removes_its_user_and_group_assignments()
    {
        var billing = await _db.AddApplication("billing");
        var accountant = await _db.AddRole(billing, "Accountant");
        var finance = await _db.AddGroup("Finance");
        var user = await _db.AddUser("deniz");
        _db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = accountant.Id });
        _db.GroupRoles.Add(new GroupRole { GroupId = finance.Id, RoleId = accountant.Id });
        await _db.SaveChangesAsync();

        ForgetTrackedRows();
        _db.Roles.Remove(accountant);
        await _db.SaveChangesAsync();

        (await _db.UserRoles.CountAsync()).ShouldBe(0);
        (await _db.GroupRoles.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Deleting_a_group_removes_its_memberships_and_role_assignments()
    {
        var billing = await _db.AddApplication("billing");
        var accountant = await _db.AddRole(billing, "Accountant");
        var finance = await _db.AddGroup("Finance");
        var user = await _db.AddUser("deniz");
        _db.UserGroups.Add(new UserGroup { UserId = user.Id, GroupId = finance.Id });
        _db.GroupRoles.Add(new GroupRole { GroupId = finance.Id, RoleId = accountant.Id });
        await _db.SaveChangesAsync();

        ForgetTrackedRows();
        _db.Groups.Remove(finance);
        await _db.SaveChangesAsync();

        (await _db.UserGroups.CountAsync()).ShouldBe(0);
        (await _db.GroupRoles.CountAsync()).ShouldBe(0);
        (await _db.Users.CountAsync()).ShouldBe(1, "the user itself stays");
    }

    [Fact]
    public async Task Users_are_stored_in_a_table_named_users()
    {
        await _db.AddUser("deniz");

        var count = await _db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM users").SingleAsync();

        count.ShouldBe(1);
    }
}
