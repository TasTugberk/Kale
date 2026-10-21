using System.ComponentModel.DataAnnotations;
using AuthService.Admin;
using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Roles;
using AuthService.Tests.Hosting;
using AuthService.Tests.Persistence;
using AuthService.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.Tests.Admin;

/// <summary>
/// Nobody may take administration away from everyone: `admin bootstrap` runs only once, so the only way back
/// would be editing the database by hand. A role change that would leave no active user holding auth.Admin is
/// refused.
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class LastAdminTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AuthServiceApp _app = null!;
    private AsyncServiceScope _scope;
    private AuthDbContext _db = null!;
    private RoleEditor _roles = null!;
    private Role _admin = null!;

    public async Task InitializeAsync()
    {
        _app = await AuthServiceApp.CreateAsync(postgres);
        await AdminSteps.BootstrapAsync(_app); // "ayse" holds the role Admin
        _scope = _app.Services.CreateAsyncScope();
        _db = _scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        _roles = _scope.ServiceProvider.GetRequiredService<RoleEditor>();
        _admin = await _db.Roles.SingleAsync(r => r.Name == "Admin");
    }

    public async Task DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Deleting_the_only_admin_role_is_refused()
    {
        var error = await Should.ThrowAsync<ValidationException>(() => _roles.DeleteAsync(_admin.Id));

        error.Message.ShouldContain("auth.Admin");
        _db.ChangeTracker.Clear();
        (await _db.Roles.AnyAsync(r => r.Id == _admin.Id)).ShouldBeTrue();
    }

    [Fact]
    public async Task Taking_auth_Admin_away_from_the_only_admin_role_is_refused()
    {
        await Should.ThrowAsync<ValidationException>(() => _roles.SetOperationsAsync(_admin.Id, []));
    }

    [Fact]
    public async Task With_another_active_admin_the_change_is_allowed()
    {
        await AddAdminRoleFor("deniz", isActive: true, throughGroup: false);

        await _roles.DeleteAsync(_admin.Id);
    }

    [Fact]
    public async Task An_admin_through_a_group_counts()
    {
        await AddAdminRoleFor("deniz", isActive: true, throughGroup: true);

        await _roles.SetOperationsAsync(_admin.Id, []);
    }

    [Fact]
    public async Task A_disabled_user_does_not_count_as_a_remaining_admin()
    {
        await AddAdminRoleFor("deniz", isActive: false, throughGroup: false);

        await Should.ThrowAsync<ValidationException>(() => _roles.DeleteAsync(_admin.Id));
    }

    [Fact]
    public async Task Disabling_the_only_admin_is_refused()
    {
        var ayse = await _db.Users.SingleAsync(u => u.UserName == "ayse");

        await Should.ThrowAsync<ValidationException>(() => _scope.ServiceProvider.GetRequiredService<UserAccounts>().DisableAsync(ayse.Id));

        _db.ChangeTracker.Clear();
        (await _db.Users.SingleAsync(u => u.UserName == "ayse")).IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task Disabling_an_admin_while_another_remains_is_allowed()
    {
        await AddAdminRoleFor("deniz", isActive: true, throughGroup: false);
        var ayse = await _db.Users.SingleAsync(u => u.UserName == "ayse");
        var deniz = await _db.Users.SingleAsync(u => u.UserName == "deniz");

        await _scope.ServiceProvider.GetRequiredService<UserAccounts>().DisableAsync(ayse.Id);

        _db.ChangeTracker.Clear();
        (await _db.Users.SingleAsync(u => u.Id == ayse.Id)).IsActive.ShouldBeFalse();
        (await LastAdminCheck.IsAdminAsync(_db, deniz.Id, CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task Two_admins_disabling_each_other_at_once_leave_one_admin()
    {
        // Each request locks only the user it disables, so without a shared lock both "another admin remains?"
        // checks could pass before either commits, leaving nobody.
        await AddAdminRoleFor("deniz", isActive: true, throughGroup: false);
        var admins = await _db.Users.Where(u => u.UserName == "ayse" || u.UserName == "deniz").Select(u => u.Id).ToListAsync();

        var attempts = admins.Select(async id =>
        {
            await using var scope = _app.Services.CreateAsyncScope();
            try
            {
                await scope.ServiceProvider.GetRequiredService<UserAccounts>().DisableAsync(id);
                return "disabled";
            }
            catch (ValidationException)
            {
                return "refused";
            }
        });
        var results = await Task.WhenAll(attempts);

        results.ShouldBe(["disabled", "refused"], ignoreOrder: true);
        _db.ChangeTracker.Clear();
        (await _db.Users.CountAsync(u => admins.Contains(u.Id) && u.IsActive)).ShouldBe(1);
    }

    [Fact]
    public async Task Taking_the_only_admins_access_to_auth_away_is_refused()
    {
        var ayse = await _db.Users.SingleAsync(u => u.UserName == "ayse");
        var auth = await _db.Applications.SingleAsync(a => a.Key == AuthApplication.Key);

        await Should.ThrowAsync<ValidationException>(() => _scope.ServiceProvider.GetRequiredService<UserAccess>()
            .SetApplicationAccessAsync(ayse.Id, auth.Id, isActive: false, defaultRoleId: null));
    }

    [Fact]
    public async Task Unassigning_the_only_admins_Admin_role_is_refused()
    {
        var ayse = await _db.Users.SingleAsync(u => u.UserName == "ayse");

        await Should.ThrowAsync<ValidationException>(() => _scope.ServiceProvider.GetRequiredService<UserAccess>()
            .UnassignRoleAsync(ayse.Id, _admin.Id));
    }

    [Fact]
    public async Task The_only_admin_can_lose_a_role_of_auth_that_isnt_Admin()
    {
        var ayse = await _db.Users.SingleAsync(u => u.UserName == "ayse");
        var auth = await _db.Applications.SingleAsync(a => a.Key == AuthApplication.Key);
        var viewer = await _roles.CreateAsync(auth.Id, "Viewer", null);
        var access = _scope.ServiceProvider.GetRequiredService<UserAccess>();
        await access.AssignRoleAsync(ayse.Id, viewer.Id);

        await access.UnassignRoleAsync(ayse.Id, viewer.Id);
    }

    [Fact]
    public async Task The_only_admin_can_lose_the_Admin_role_they_also_hold_through_a_group()
    {
        var ayse = await _db.Users.SingleAsync(u => u.UserName == "ayse");
        var group = await _db.AddGroup("Admins");
        _db.UserGroups.Add(new UserGroup { UserId = ayse.Id, GroupId = group.Id });
        _db.GroupRoles.Add(new GroupRole { GroupId = group.Id, RoleId = _admin.Id });
        await _db.SaveChangesAsync();

        await _scope.ServiceProvider.GetRequiredService<UserAccess>().UnassignRoleAsync(ayse.Id, _admin.Id);

        (await LastAdminCheck.IsAdminAsync(_db, ayse.Id, CancellationToken.None)).ShouldBeTrue();
    }

    // Another role of "auth" with auth.Admin, held by a user with access to "auth".
    private async Task AddAdminRoleFor(string userName, bool isActive, bool throughGroup)
    {
        var auth = await _db.Applications.SingleAsync(a => a.Key == AuthApplication.Key);
        var user = await _db.AddUser(userName);
        user.IsActive = isActive;
        _db.UserApplications.Add(new UserApplication { UserId = user.Id, ApplicationId = auth.Id });
        var otherAdmins = await _roles.CreateAsync(auth.Id, "Other admins", null);
        await _roles.SetOperationsAsync(otherAdmins.Id, ["auth.Admin"]);
        if (throughGroup)
        {
            var group = await _db.AddGroup("Admins");
            _db.UserGroups.Add(new UserGroup { UserId = user.Id, GroupId = group.Id });
            _db.GroupRoles.Add(new GroupRole { GroupId = group.Id, RoleId = otherAdmins.Id });
        }
        else
        {
            _db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = otherAdmins.Id });
        }

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }
}
