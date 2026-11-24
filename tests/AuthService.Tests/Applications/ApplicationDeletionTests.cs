using System.ComponentModel.DataAnnotations;
using Auth.Contracts.V1;
using AuthService.Admin;
using AuthService.Applications;
using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Tests.Admin;
using AuthService.Tests.Hosting;
using AuthService.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;

namespace AuthService.Tests.Applications;

/// <summary>
/// Deleting an application that was deactivated first: its sessions stop (events), and everything that belongs to
/// it goes, in one transaction. Users and groups are global and stay.
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class ApplicationDeletionTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AuthServiceApp _app = null!;
    private AsyncServiceScope _scope;
    private ApplicationService _applications = null!;
    private AuthDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _app = await AuthServiceApp.CreateAsync(postgres);
        await AdminSteps.BootstrapAsync(_app);
        _scope = _app.Services.CreateAsyncScope();
        _applications = _scope.ServiceProvider.GetRequiredService<ApplicationService>();
        _db = _scope.ServiceProvider.GetRequiredService<AuthDbContext>();
    }

    public async Task DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Deleting_a_deactivated_application_stops_its_sessions_and_removes_everything_of_it()
    {
        var billing = await CreateWithEverythingAsync("billing");
        var hr = await CreateWithEverythingAsync("hr");
        await _applications.SetActiveAsync(billing.Application.Id, isActive: false);

        await _applications.DeleteAsync(billing.Application.Id);

        await using var fresh = _app.Services.CreateAsyncScope();
        var db = fresh.ServiceProvider.GetRequiredService<AuthDbContext>();
        var id = billing.Application.Id;
        (await db.Applications.AnyAsync(a => a.Id == id)).ShouldBeFalse();
        (await db.Sessions.AnyAsync(s => s.ApplicationId == id)).ShouldBeFalse();
        (await db.UserApplications.AnyAsync(a => a.ApplicationId == id)).ShouldBeFalse();
        (await db.Roles.AnyAsync(r => r.ApplicationId == id)).ShouldBeFalse();
        (await db.Operations.AnyAsync(o => o.ApplicationId == id)).ShouldBeFalse();
        (await db.OperationImplications.AnyAsync(i => i.ApplicationId == id)).ShouldBeFalse();
        (await db.RoleOperations.AnyAsync(ro => ro.ApplicationId == id)).ShouldBeFalse();
        (await db.UserRoles.AnyAsync(ur => ur.RoleId == billing.Role.Id)).ShouldBeFalse();
        (await db.GroupRoles.AnyAsync(gr => gr.RoleId == billing.Role.Id)).ShouldBeFalse();
        var clients = await db.Set<OpenIddictEntityFrameworkCoreApplication<Guid>>().Select(c => c.ClientId).ToListAsync();
        clients.ShouldNotContain("billing");
        clients.ShouldNotContain("billing.desktop");
        (await db.Set<OpenIddictEntityFrameworkCoreToken<Guid>>().CountAsync()).ShouldBe(1, "hr's token stays");
        (await db.Set<OpenIddictEntityFrameworkCoreAuthorization<Guid>>().CountAsync()).ShouldBe(1, "hr's authorization stays");

        // The other application, the user and the group stay.
        (await db.Applications.AnyAsync(a => a.Id == hr.Application.Id)).ShouldBeTrue();
        (await db.Sessions.CountAsync(s => s.ApplicationId == hr.Application.Id)).ShouldBe(2);
        (await db.Users.AnyAsync(u => u.Id == billing.User.Id)).ShouldBeTrue();
        (await db.Groups.AnyAsync(g => g.Id == billing.Group.Id)).ShouldBeTrue();

        var events = (await db.OutboxMessages.ToListAsync()).Select(m => InvalidationEvent.Parser.ParseFrom(m.Payload)).ToList();
        events.Where(e => e.EventCase == InvalidationEvent.EventOneofCase.SessionStopped)
            .Select(e => e.SessionStopped.SessionId)
            .ShouldBe([billing.OpenSession.Id.ToString()], "only the open session is announced");
    }

    [Fact]
    public async Task No_cache_outlives_the_request_that_deleted_the_clients()
    {
        // DeleteAsync deletes OpenIddict's clients in bulk, past its application cache: fine only while that cache
        // lives per scope (request).
        await using var other = _app.Services.CreateAsyncScope();
        var cache = typeof(IOpenIddictApplicationCache<OpenIddictEntityFrameworkCoreApplication<Guid>>);
        other.ServiceProvider.GetRequiredService(cache).ShouldNotBeSameAs(_scope.ServiceProvider.GetRequiredService(cache));

        var billing = await CreateWithEverythingAsync("billing");
        (await other.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>().FindByClientIdAsync("billing")).ShouldNotBeNull();
        await _applications.SetActiveAsync(billing.Application.Id, isActive: false);

        await _applications.DeleteAsync(billing.Application.Id);

        await using var next = _app.Services.CreateAsyncScope();
        (await next.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>().FindByClientIdAsync("billing")).ShouldBeNull();
    }

    [Fact]
    public async Task An_active_application_is_not_deleted()
    {
        var billing = await CreateWithEverythingAsync("billing");

        var error = await Should.ThrowAsync<ValidationException>(() => _applications.DeleteAsync(billing.Application.Id));

        error.Message.ShouldContain("Deactivate");
        await using var fresh = _app.Services.CreateAsyncScope();
        (await fresh.ServiceProvider.GetRequiredService<AuthDbContext>().Applications.AnyAsync(a => a.Id == billing.Application.Id)).ShouldBeTrue();
    }

    [Fact]
    public async Task Auth_is_never_deleted()
    {
        var auth = await _db.Applications.SingleAsync(a => a.Key == AuthApplication.Key);

        var error = await Should.ThrowAsync<ValidationException>(() => _applications.DeleteAsync(auth.Id));

        error.Message.ShouldContain($"'{AuthApplication.Key}'");
    }

    [Fact]
    public async Task An_unknown_application_is_not_found()
    {
        await Should.ThrowAsync<NotFoundException>(() => _applications.DeleteAsync(Guid.CreateVersion7()));
    }

    private sealed record Setup(Application Application, Role Role, User User, Group Group, Session OpenSession);

    // An application with its clients (web and desktop), operations with an implication, a role granting them, a
    // user with access (the role as default), the role directly and through a group, an open and an ended session,
    // and an OpenIddict authorization with a token for its client.
    private async Task<Setup> CreateWithEverythingAsync(string key)
    {
        var created = await _applications.CreateAsync(new NewApplication(key, key, [new Uri($"https://{key}.example/signin-oidc")], []));
        var application = created.Application;
        await _applications.SetDesktopSignInAsync(application.Id, enabled: true);
        var read = await _db.AddOperation(application, "Read");
        var write = await _db.AddOperation(application, "Write");
        _db.OperationImplications.Add(new OperationImplication { ApplicationId = application.Id, OperationId = write.Id, ImpliedOperationId = read.Id });
        var role = await _db.AddRole(application, "Member");
        _db.RoleOperations.Add(new RoleOperation { ApplicationId = application.Id, RoleId = role.Id, OperationId = write.Id });
        var user = await _db.AddUser($"{key}-user");
        _db.UserApplications.Add(new UserApplication { UserId = user.Id, ApplicationId = application.Id, DefaultRoleId = role.Id });
        _db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        var group = await _db.AddGroup($"{key}-group");
        _db.UserGroups.Add(new UserGroup { UserId = user.Id, GroupId = group.Id });
        _db.GroupRoles.Add(new GroupRole { GroupId = group.Id, RoleId = role.Id });
        var expires = _app.Clock.GetUtcNow().AddHours(8);
        var open = new Session { UserId = user.Id, ApplicationId = application.Id, RoleId = role.Id, ExpiresAt = expires };
        var ended = new Session { UserId = user.Id, ApplicationId = application.Id, RoleId = role.Id, ExpiresAt = expires };
        ended.End(_app.Clock.GetUtcNow());
        _db.Sessions.AddRange(open, ended);

        var client = await _db.Set<OpenIddictEntityFrameworkCoreApplication<Guid>>().SingleAsync(c => c.ClientId == key);
        var authorization = new OpenIddictEntityFrameworkCoreAuthorization<Guid>
        {
            Application = client, Subject = user.Id.ToString(), Status = "valid", Type = "ad-hoc",
        };
        _db.Add(authorization);
        _db.Add(new OpenIddictEntityFrameworkCoreToken<Guid>
        {
            Application = client, Authorization = authorization, Subject = user.Id.ToString(), Type = "refresh_token", Status = "valid",
        });
        await _db.SaveChangesAsync();
        return new Setup(application, role, user, group, open);
    }
}
