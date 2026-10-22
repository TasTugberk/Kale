using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Sessions;
using AuthService.Tests.Hosting;
using AuthService.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthService.Tests.Sessions;

/// <summary>
/// The running app cleans up every hour: old finished sessions, and OpenIddict's old tokens and authorizations
/// (refresh tokens and codes are stored too and would otherwise pile up just the same), with the same retention.
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class SessionCleanupServiceTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Every_hour_the_app_deletes_old_finished_sessions_and_prunes_old_tokens()
    {
        await using var app = await AuthServiceApp.CreateAsync(postgres);
        _ = app.Services; // Starts the app and its background services.
        var now = app.Clock.GetUtcNow();
        var (sessionId, oldTokenId) = await AddOldSessionAndToken(app, postgres, now.AddDays(-30));
        var oldAuthorizationId = await AddRevokedAuthorization(app, now.AddDays(-30));
        // Finished 3 days ago: inside the 7 days kept, so it stays (the prune uses the configured retention).
        var recentTokenId = await AddExpiredToken(app, now.AddDays(-3));

        // The first cleanup runs one interval after startup; keep moving the clock until it has run.
        var cleaned = false;
        for (var i = 0; i < 50 && !cleaned; i++)
        {
            app.Clock.Advance(SessionCleanupService.Interval);
            await Task.Delay(100);
            cleaned = !await SessionExists(app, sessionId) && !await TokenExists(app, oldTokenId)
                && !await AuthorizationExists(app, oldAuthorizationId);
        }

        cleaned.ShouldBeTrue("the old session, token and authorization should be gone after the hourly cleanup");
        (await TokenExists(app, recentTokenId)).ShouldBeTrue("a token that finished inside the retention stays");
    }

    [Fact]
    public async Task Finished_sessions_are_kept_7_days_unless_configured()
    {
        await using var app = await AuthServiceApp.CreateAsync(postgres);
        await using var configured = new AuthServiceApp(app.ConnectionString, ("Sessions:KeepFinishedFor", "2.00:00:00"));

        app.Services.GetRequiredService<SessionCleanupSettings>().KeepFinishedFor.ShouldBe(TimeSpan.FromDays(7));
        configured.Services.GetRequiredService<SessionCleanupSettings>().KeepFinishedFor.ShouldBe(TimeSpan.FromDays(2));
    }

    // The session is saved through a context whose clock is at "long ago": the database requires a session to
    // be created before it expires, and CreatedAt comes from the context's clock (the app's clock is now).
    private static async Task<(Guid SessionId, string TokenId)> AddOldSessionAndToken(
        AuthServiceApp app, PostgresFixture postgres, DateTimeOffset longAgo)
    {
        var databaseName = new NpgsqlConnectionStringBuilder(app.ConnectionString).Database!;
        await using (var db = postgres.CreateDbContext(databaseName, new FakeTimeProvider(longAgo)))
        {
            await AddSessionAsync(db, longAgo);
        }

        await using var scope = app.Services.CreateAsyncScope();
        var sessionId = await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Sessions.Select(s => s.Id).SingleAsync();
        return (sessionId, await AddExpiredToken(app, longAgo));
    }

    // A refresh token created at that time, expired 8 hours later (still "valid": only its expiry ended it).
    private static async Task<string> AddExpiredToken(AuthServiceApp app, DateTimeOffset createdAt)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        var token = await tokens.CreateAsync(new OpenIddictTokenDescriptor
        {
            CreationDate = createdAt,
            ExpirationDate = createdAt.AddHours(8),
            Status = Statuses.Valid,
            Subject = "someone",
            Type = TokenTypeHints.RefreshToken,
        });
        return (await tokens.GetIdAsync(token))!;
    }

    private static async Task<string> AddRevokedAuthorization(AuthServiceApp app, DateTimeOffset createdAt)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var authorization = await authorizations.CreateAsync(new OpenIddictAuthorizationDescriptor
        {
            CreationDate = createdAt,
            Status = Statuses.Revoked,
            Subject = "someone",
            Type = AuthorizationTypes.AdHoc,
        });
        return (await authorizations.GetIdAsync(authorization))!;
    }

    private static async Task<bool> AuthorizationExists(AuthServiceApp app, string id)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>().FindByIdAsync(id) is not null;
    }

    private static async Task AddSessionAsync(AuthDbContext db, DateTimeOffset longAgo)
    {
        var billing = await db.AddApplication("billing");
        var member = await db.AddRole(billing, "Member");
        var user = await db.AddUser("deniz");
        db.UserApplications.Add(new UserApplication { UserId = user.Id, ApplicationId = billing.Id });
        var session = new Session { UserId = user.Id, ApplicationId = billing.Id, RoleId = member.Id, ExpiresAt = longAgo.AddHours(8) };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
    }

    private static async Task<bool> SessionExists(AuthServiceApp app, Guid id)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Sessions.AnyAsync(s => s.Id == id);
    }

    private static async Task<bool> TokenExists(AuthServiceApp app, string id)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>().FindByIdAsync(id) is not null;
    }
}
