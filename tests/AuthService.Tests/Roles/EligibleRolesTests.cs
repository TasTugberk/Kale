using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Roles;
using AuthService.Tests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Tests.Roles;

[Collection(UsesPostgres.Name)]
public sealed class EligibleRolesTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AuthDbContext _db = null!;
    private EligibleRoles _eligibleRoles = null!;
    private Application _billing = null!;
    private User _user = null!;

    public async Task InitializeAsync()
    {
        _db = postgres.CreateDbContext();
        await _db.Database.MigrateAsync();
        _eligibleRoles = new EligibleRoles(_db);
        _billing = await _db.AddApplication("billing");
        _user = await _db.AddUser("deniz");
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task A_directly_assigned_role_is_eligible()
    {
        var accountant = await _db.AddRole(_billing, "Accountant");
        await AssignDirectly(accountant);

        var roles = await _eligibleRoles.ForUserAsync(_user.Id, _billing.Id);

        roles.ShouldBe([new EligibleRole(accountant.Id, "Accountant")]);
    }

    [Fact]
    public async Task A_role_of_one_of_the_users_groups_is_eligible()
    {
        var auditor = await _db.AddRole(_billing, "Auditor");
        var finance = await _db.AddGroup("Finance");
        await Join(finance);
        await AssignToGroup(finance, auditor);

        var roles = await _eligibleRoles.ForUserAsync(_user.Id, _billing.Id);

        roles.ShouldBe([new EligibleRole(auditor.Id, "Auditor")]);
    }

    [Fact]
    public async Task Direct_and_group_roles_are_combined_without_duplicates_sorted_by_name()
    {
        var accountant = await _db.AddRole(_billing, "Accountant");
        var auditor = await _db.AddRole(_billing, "Auditor");
        var finance = await _db.AddGroup("Finance");
        await Join(finance);
        await AssignDirectly(auditor);
        await AssignToGroup(finance, auditor);    // same role twice: through the group and directly
        await AssignToGroup(finance, accountant);

        var roles = await _eligibleRoles.ForUserAsync(_user.Id, _billing.Id);

        roles.ShouldBe([new EligibleRole(accountant.Id, "Accountant"), new EligibleRole(auditor.Id, "Auditor")]);
    }

    [Fact]
    public async Task Roles_of_other_applications_are_not_eligible()
    {
        var hr = await _db.AddApplication("hr");
        var payroll = await _db.AddRole(hr, "Payroll");
        var finance = await _db.AddGroup("Finance");
        await Join(finance);
        await AssignDirectly(payroll);
        await AssignToGroup(finance, payroll);

        var roles = await _eligibleRoles.ForUserAsync(_user.Id, _billing.Id);

        roles.ShouldBeEmpty();
    }

    [Fact]
    public async Task Roles_of_groups_the_user_is_not_in_are_not_eligible()
    {
        var auditor = await _db.AddRole(_billing, "Auditor");
        var finance = await _db.AddGroup("Finance");
        await AssignToGroup(finance, auditor);
        var someoneElse = await _db.AddUser("ece");
        _db.UserGroups.Add(new UserGroup { UserId = someoneElse.Id, GroupId = finance.Id }); // only someone else joins
        await _db.SaveChangesAsync();

        var roles = await _eligibleRoles.ForUserAsync(_user.Id, _billing.Id);

        roles.ShouldBeEmpty();
    }

    [Fact]
    public async Task Other_users_roles_are_not_eligible()
    {
        var accountant = await _db.AddRole(_billing, "Accountant");
        var someoneElse = await _db.AddUser("ece");
        _db.UserRoles.Add(new UserRole { UserId = someoneElse.Id, RoleId = accountant.Id });
        await _db.SaveChangesAsync();

        var roles = await _eligibleRoles.ForUserAsync(_user.Id, _billing.Id);

        roles.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_user_holds_a_role_assigned_directly_or_through_a_group()
    {
        var accountant = await _db.AddRole(_billing, "Accountant");
        var auditor = await _db.AddRole(_billing, "Auditor");
        var finance = await _db.AddGroup("Finance");
        await AssignDirectly(accountant);
        await Join(finance);
        await AssignToGroup(finance, auditor);

        (await _eligibleRoles.HoldsRoleAsync(_user.Id, accountant.Id)).ShouldBeTrue();
        (await _eligibleRoles.HoldsRoleAsync(_user.Id, auditor.Id)).ShouldBeTrue();
    }

    [Fact]
    public async Task The_user_no_longer_holds_a_role_once_it_is_taken_away()
    {
        var auditor = await _db.AddRole(_billing, "Auditor");
        var finance = await _db.AddGroup("Finance");
        await Join(finance);
        await AssignToGroup(finance, auditor);

        // Leaving the group takes the role away: a session on that role must stop being ACTIVE.
        await _db.UserGroups.Where(ug => ug.UserId == _user.Id).ExecuteDeleteAsync();

        (await _eligibleRoles.HoldsRoleAsync(_user.Id, auditor.Id)).ShouldBeFalse();
    }

    private async Task AssignDirectly(Role role)
    {
        _db.UserRoles.Add(new UserRole { UserId = _user.Id, RoleId = role.Id });
        await _db.SaveChangesAsync();
    }

    private async Task Join(Group group)
    {
        _db.UserGroups.Add(new UserGroup { UserId = _user.Id, GroupId = group.Id });
        await _db.SaveChangesAsync();
    }

    private async Task AssignToGroup(Group group, Role role)
    {
        _db.GroupRoles.Add(new GroupRole { GroupId = group.Id, RoleId = role.Id });
        await _db.SaveChangesAsync();
    }
}
