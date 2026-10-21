using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Auth.Contracts.V1;
using AuthService.Admin;
using AuthService.Applications;
using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Tests.Hosting;
using AuthService.Tests.Persistence;
using AuthService.Tests.SignIn;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.Tests.Admin;

/// <summary>
/// The admin API's sessions: who is signed in where, and stopping a session (DESIGN.md: admins must be able to
/// stop a session even while its token is valid).
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class AdminSessionsTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly Uri Sessions = new("/admin/api/sessions", UriKind.Relative);

    private AuthServiceApp _app = null!;
    private HttpClient _admin = null!;
    private Application _billing = null!;
    private Session _open = null!;
    private Session _ended = null!;
    private Session _expired = null!;

    public async Task InitializeAsync()
    {
        _app = await AuthServiceApp.CreateAsync(postgres);
        var authSecret = await AdminSteps.BootstrapAsync(_app);

        await using var scope = _app.Services.CreateAsyncScope();
        _billing = (await scope.ServiceProvider.GetRequiredService<ApplicationService>()
            .CreateAsync(new NewApplication("billing", "Billing", [Browser.RedirectUri("billing")], []))).Application;
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var accountant = await db.AddRole(_billing, "Accountant");
        var deniz = await db.AddUser("deniz");
        db.UserApplications.Add(new UserApplication { UserId = deniz.Id, ApplicationId = _billing.Id });
        var now = _app.Clock.GetUtcNow();
        _open = NewSession(deniz, accountant, now.AddHours(8));
        _ended = NewSession(deniz, accountant, now.AddHours(8));
        _ended.End(now);
        _expired = NewSession(deniz, accountant, now.AddHours(1));
        db.Sessions.AddRange(_open, _ended, _expired);
        await db.SaveChangesAsync();

        // Past the expired session's end. The admin signs in after that, so their own token is fresh.
        _app.Clock.Advance(TimeSpan.FromHours(2));
        _admin = _app.CreateClient();
        _admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await AdminSteps.SignInAsync(_app, "ayse", AuthApplication.Key, authSecret));
    }

    public async Task DisposeAsync()
    {
        _admin.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Only_open_sessions_are_listed_with_who_where_and_which_role()
    {
        var sessions = await _admin.GetFromJsonAsync<JsonElement[]>(new Uri($"/admin/api/sessions?applicationId={_billing.Id}", UriKind.Relative));

        var session = sessions!.Single();
        session.GetProperty("id").GetGuid().ShouldBe(_open.Id);
        session.GetProperty("userId").GetGuid().ShouldBe(_open.UserId);
        session.GetProperty("userName").GetString().ShouldBe("deniz");
        session.GetProperty("applicationKey").GetString().ShouldBe("billing");
        session.GetProperty("roleName").GetString().ShouldBe("Accountant");
        session.GetProperty("createdAt").GetDateTimeOffset().ShouldBe(_open.CreatedAt);
        session.GetProperty("expiresAt").GetDateTimeOffset().ShouldBe(_open.ExpiresAt);
    }

    [Fact]
    public async Task Newer_sessions_come_first()
    {
        // The admin's own session to the admin UI started after the seeded ones (the clock moved in between).
        var sessions = await _admin.GetFromJsonAsync<JsonElement[]>(Sessions);

        sessions!.Select(s => s.GetProperty("applicationKey").GetString()).ShouldBe(["auth", "billing"]);
    }

    [Fact]
    public async Task The_list_can_be_narrowed_to_one_user()
    {
        var ayse = await UserId("ayse");

        var sessions = await _admin.GetFromJsonAsync<JsonElement[]>(new Uri($"/admin/api/sessions?userId={ayse}", UriKind.Relative));

        sessions!.ShouldAllBe(s => s.GetProperty("userName").GetString() == "ayse");
        sessions!.Length.ShouldBe(1, "the admin's own session to the admin UI");
    }

    [Fact]
    public async Task Stopping_a_session_ends_it_and_tells_client_apps()
    {
        (await _admin.DeleteAsync(new Uri($"/admin/api/sessions/{_open.Id}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        (await db.Sessions.SingleAsync(s => s.Id == _open.Id)).EndedAt.ShouldNotBeNull();
        var events = (await db.OutboxMessages.ToListAsync()).Select(m => InvalidationEvent.Parser.ParseFrom(m.Payload));
        events.Count(e => e.SessionStopped?.SessionId == _open.Id.ToString()).ShouldBe(1);
    }

    [Fact]
    public async Task An_unknown_session_is_a_404()
    {
        (await _admin.DeleteAsync(new Uri($"/admin/api/sessions/{Guid.CreateVersion7()}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task<Guid> UserId(string userName)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Users.Where(u => u.UserName == userName).Select(u => u.Id).SingleAsync();
    }

    private static Session NewSession(User user, Role role, DateTimeOffset expiresAt) =>
        new() { UserId = user.Id, ApplicationId = role.ApplicationId, RoleId = role.Id, ExpiresAt = expiresAt };
}
