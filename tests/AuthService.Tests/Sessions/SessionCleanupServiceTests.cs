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
/// The running app cleans up every hour: old finished sessions, and OpenIddict's old tokens (refresh tokens and
/// codes are stored too and would otherwise pile up just the same).
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
        var (sessionId, tokenId) = await AddOldSessionAndToken(app, postgres, now.AddDays(-30));

        // The first cleanup runs one interval after startup; keep moving the clock until it has run.
        var cleaned = false;
        for (var i = 0; i < 50 && !cleaned; i++)
        {
            app.Clock.Advance(SessionCleanupService.Interval);
            await Task.Delay(100);
            cleaned = !await SessionExists(app, sessionId) && !await TokenExists(app, tokenId);
        }

        cleaned.ShouldBeTrue("the old session and the old token should be gone after the hourly cleanup");
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
        var userId = await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Users.Select(u => u.Id).SingleAsync();

        var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        var token = await tokens.CreateAsync(new OpenIddictTokenDescriptor
        {
            CreationDate = longAgo,
            ExpirationDate = longAgo.AddHours(8),
            Status = Statuses.Valid,
            Subject = userId.ToString(),
            Type = TokenTypeHints.RefreshToken,
        });
        return (sessionId, (await tokens.GetIdAsync(token))!);
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
