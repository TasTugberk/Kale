using System.ComponentModel.DataAnnotations;
using Auth.Contracts.V1;
using AuthService.Admin;
using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Tests.Admin;
using AuthService.Tests.Hosting;
using AuthService.Tests.Persistence;
using AuthService.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.EntityFrameworkCore.Models;

namespace AuthService.Tests.Users;

/// <summary>
/// Correcting a user's names, and deleting a user with everything that hangs on them. "ayse" is the first admin
/// (bootstrap).
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class UserEditorTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AuthServiceApp _app = null!;
    private AsyncServiceScope _scope;
    private AuthDbContext _db = null!;
    private UserEditor _editor = null!;
    private User _deniz = null!;

    public async Task InitializeAsync()
    {
        _app = await AuthServiceApp.CreateAsync(postgres);
        await AdminSteps.BootstrapAsync(_app);
        _scope = _app.Services.CreateAsyncScope();
        _db = _scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        _editor = _scope.ServiceProvider.GetRequiredService<UserEditor>();
        _deniz = await _scope.ServiceProvider.GetRequiredService<UserCreator>().CreateAsync("deniz", "Deniz", AdminSteps.Password);
    }

    public async Task DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Names_are_corrected_without_signing_the_user_out()
    {
        var stamp = _deniz.SecurityStamp;
        var session = await AddSessionAsync(_deniz, "billing");

        await _editor.UpdateAsync(_deniz.Id, "deniz.yilmaz", "  Deniz Yılmaz ");

        await using var fresh = _app.Services.CreateAsyncScope();
        var users = fresh.ServiceProvider.GetRequiredService<UserManager<User>>();
        var renamed = (await users.FindByNameAsync("DENIZ.YILMAZ")).ShouldNotBeNull("found by the new name, like the login page does");
        renamed.Id.ShouldBe(_deniz.Id);
        renamed.DisplayName.ShouldBe("Deniz Yılmaz");
        renamed.SecurityStamp.ShouldBe(stamp, "a rename keeps the sign-in cookie");
        (await users.FindByNameAsync("deniz")).ShouldBeNull();
        var db = fresh.ServiceProvider.GetRequiredService<AuthDbContext>();
        (await db.Sessions.SingleAsync(s => s.Id == session.Id)).EndedAt.ShouldBeNull("sessions stay");
    }

    [Fact]
    public async Task Every_problem_is_reported_at_once()
    {
        var error = await Should.ThrowAsync<ValidationException>(() => _editor.UpdateAsync(_deniz.Id, "AYSE", " "));

        error.Message.ShouldContain("already taken");
        error.Message.ShouldContain("display name");
    }

    [Fact]
    public async Task Keeping_the_same_user_name_is_not_a_conflict_with_itself()
    {
        await _editor.UpdateAsync(_deniz.Id, "deniz", "Deniz Y.");

        await using var fresh = _app.Services.CreateAsyncScope();
        (await fresh.ServiceProvider.GetRequiredService<AuthDbContext>().Users.SingleAsync(u => u.Id == _deniz.Id))
            .DisplayName.ShouldBe("Deniz Y.");
    }

    [Fact]
    public async Task A_user_name_with_characters_identity_does_not_allow_is_refused()
    {
        await Should.ThrowAsync<ValidationException>(() => _editor.UpdateAsync(_deniz.Id, "deniz yılmaz", "Deniz"));
    }

    [Fact]
    public async Task Deleting_a_user_stops_their_sessions_and_removes_everything_of_theirs()
    {
        var open = await AddSessionAsync(_deniz, "billing");
        var ended = await AddSessionAsync(_deniz, "hr");
        ended.End(_app.Clock.GetUtcNow());
        var group = await _db.AddGroup("Finance");
        _db.UserGroups.Add(new UserGroup { UserId = _deniz.Id, GroupId = group.Id });
        _db.UserRoles.Add(new UserRole { UserId = _deniz.Id, RoleId = open.RoleId });
        var authorization = new OpenIddictEntityFrameworkCoreAuthorization<Guid> { Subject = _deniz.Id.ToString(), Status = "valid", Type = "ad-hoc" };
        _db.Add(authorization);
        _db.Add(new OpenIddictEntityFrameworkCoreToken<Guid> { Subject = _deniz.Id.ToString(), Authorization = authorization, Type = "refresh_token", Status = "valid" });
        // A token that names the user only through its authorization.
        _db.Add(new OpenIddictEntityFrameworkCoreToken<Guid> { Authorization = authorization, Type = "authorization_code", Status = "redeemed" });
        var ayseToken = new OpenIddictEntityFrameworkCoreToken<Guid> { Subject = (await AyseIdAsync()).ToString(), Type = "refresh_token", Status = "valid" };
        _db.Add(ayseToken);
        await _db.SaveChangesAsync();

        await _editor.DeleteAsync(_deniz.Id, deletedBy: null);

        await using var fresh = _app.Services.CreateAsyncScope();
        var db = fresh.ServiceProvider.GetRequiredService<AuthDbContext>();
        (await db.Users.AnyAsync(u => u.Id == _deniz.Id)).ShouldBeFalse();
        (await db.Sessions.AnyAsync(s => s.UserId == _deniz.Id)).ShouldBeFalse();
        (await db.UserApplications.AnyAsync(a => a.UserId == _deniz.Id)).ShouldBeFalse();
        (await db.UserRoles.AnyAsync(r => r.UserId == _deniz.Id)).ShouldBeFalse();
        (await db.UserGroups.AnyAsync(g => g.UserId == _deniz.Id)).ShouldBeFalse();
        (await db.Groups.AnyAsync(g => g.Id == group.Id)).ShouldBeTrue("the group stays");
        (await db.Set<OpenIddictEntityFrameworkCoreAuthorization<Guid>>().AnyAsync()).ShouldBeFalse();
        (await db.Set<OpenIddictEntityFrameworkCoreToken<Guid>>().Select(t => t.Id).ToListAsync()).ShouldBe([ayseToken.Id]);

        // Only the open session is announced: client apps may still cache it.
        var events = (await db.OutboxMessages.ToListAsync()).Select(m => InvalidationEvent.Parser.ParseFrom(m.Payload)).ToList();
        events.Where(e => e.EventCase == InvalidationEvent.EventOneofCase.SessionStopped)
            .Select(e => e.SessionStopped.SessionId)
            .ShouldBe([open.Id.ToString()]);
    }

    [Fact]
    public async Task The_last_admin_cannot_be_deleted()
    {
        var ayse = await AyseIdAsync();

        var error = await Should.ThrowAsync<ValidationException>(() => _editor.DeleteAsync(ayse, deletedBy: null));

        error.Message.ShouldContain("auth.Admin");
        await using var fresh = _app.Services.CreateAsyncScope();
        (await fresh.ServiceProvider.GetRequiredService<AuthDbContext>().Users.AnyAsync(u => u.Id == ayse)).ShouldBeTrue();
    }

    [Fact]
    public async Task An_admin_cannot_delete_themselves()
    {
        var ayse = await AyseIdAsync();

        var error = await Should.ThrowAsync<ValidationException>(() => _editor.DeleteAsync(ayse, deletedBy: ayse));

        error.Message.ShouldContain("own account");
    }

    [Fact]
    public async Task An_admin_can_be_deleted_while_another_remains()
    {
        var ayse = await AyseIdAsync();
        await AdminSteps.RunAsync(_app, "", "assign-role", "deniz", AuthApplication.Key, "Admin");

        await _editor.DeleteAsync(ayse, deletedBy: _deniz.Id);

        await using var fresh = _app.Services.CreateAsyncScope();
        (await fresh.ServiceProvider.GetRequiredService<AuthDbContext>().Users.AnyAsync(u => u.Id == ayse)).ShouldBeFalse();
    }

    [Fact]
    public async Task An_unknown_user_is_not_found()
    {
        await Should.ThrowAsync<NotFoundException>(() => _editor.UpdateAsync(Guid.CreateVersion7(), "x", "X"));
        await Should.ThrowAsync<NotFoundException>(() => _editor.DeleteAsync(Guid.CreateVersion7(), deletedBy: null));
    }

    // An open session of the user in a new application with one role (and an access row, which sessions need).
    private async Task<Session> AddSessionAsync(User user, string applicationKey)
    {
        var application = await _db.AddApplication(applicationKey);
        var role = await _db.AddRole(application, "Member");
        _db.UserApplications.Add(new UserApplication { UserId = user.Id, ApplicationId = application.Id });
        var session = new Session { UserId = user.Id, ApplicationId = application.Id, RoleId = role.Id, ExpiresAt = _app.Clock.GetUtcNow().AddHours(8) };
        _db.Sessions.Add(session);
        await _db.SaveChangesAsync();
        return session;
    }

    private Task<Guid> AyseIdAsync() => _db.Users.Where(u => u.UserName == "ayse").Select(u => u.Id).SingleAsync();

}
