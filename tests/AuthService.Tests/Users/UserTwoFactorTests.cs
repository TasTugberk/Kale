using Auth.Contracts.V1;
using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Tests.Hosting;
using AuthService.Tests.Persistence;
using AuthService.Tests.SignIn;
using AuthService.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.Tests.Users;

/// <summary>
/// An admin resets a user's two-step sign-in (a lost phone and lost recovery codes): it's off, the old key and
/// recovery codes stop working, the security stamp changes, and the user's open sessions stop like after a new password.
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class UserTwoFactorTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AuthServiceApp _app = null!;
    private User _user = null!;

    public async Task InitializeAsync()
    {
        _app = await AuthServiceApp.CreateAsync(postgres);
        await using var scope = _app.Services.CreateAsyncScope();
        _user = await scope.ServiceProvider.GetRequiredService<UserCreator>().CreateAsync("deniz", "Deniz", "Correct-horse-9");
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task A_reset_turns_it_off_and_the_old_key_recovery_codes_and_stamp_stop_working()
    {
        var key = await TwoFactorSignInTests.TurnOnTwoFactor(_app, _user);
        var (recoveryCodes, stamp) = await WithUsers(async (users, user) =>
            ((await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))!.ToList(), user.SecurityStamp));

        await Reset(_user.Id);

        await WithUsers(async (users, user) =>
        {
            user.TwoFactorEnabled.ShouldBeFalse();
            user.SecurityStamp.ShouldNotBe(stamp);
            (await users.GetAuthenticatorKeyAsync(user)).ShouldNotBe(key);
            (await users.CountRecoveryCodesAsync(user)).ShouldBe(0);
            (await users.RedeemTwoFactorRecoveryCodeAsync(user, recoveryCodes[0])).Succeeded.ShouldBeFalse();
            return 0;
        });
    }

    [Fact]
    public async Task A_reset_stops_the_users_open_sessions_with_their_events()
    {
        var session = await AddSession(_user);

        await Reset(_user.Id);

        await using var db = Fresh();
        (await db.Sessions.SingleAsync(s => s.Id == session.Id)).EndedAt.ShouldBe(_app.Clock.GetUtcNow());
        (await db.OutboxMessages.ToListAsync()).Select(m => InvalidationEvent.Parser.ParseFrom(m.Payload).SessionStopped.SessionId)
            .ShouldBe([session.Id.ToString()]);
    }

    [Fact]
    public async Task A_reset_keeps_changes_made_to_the_user_in_between()
    {
        // Identity's saves write the whole row: the reset must start from the row as it is under its lock.
        await using var scope = _app.Services.CreateAsyncScope();
        var twoFactor = scope.ServiceProvider.GetRequiredService<UserTwoFactor>();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        (await db.Users.SingleAsync(u => u.Id == _user.Id)).DisplayName.ShouldBe("Deniz");
        await using (var other = Fresh())
        {
            (await other.Users.SingleAsync(u => u.Id == _user.Id)).DisplayName = "Deniz Yılmaz";
            await other.SaveChangesAsync();
        }

        await twoFactor.ResetAsync(_user.Id);

        await using var fresh = Fresh();
        (await fresh.Users.SingleAsync(u => u.Id == _user.Id)).DisplayName.ShouldBe("Deniz Yılmaz");
    }

    [Fact]
    public async Task An_unknown_user_is_not_found()
    {
        await Should.ThrowAsync<NotFoundException>(() => Reset(Guid.CreateVersion7()));
    }

    private async Task Reset(Guid userId)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<UserTwoFactor>().ResetAsync(userId);
    }

    private async Task<T> WithUsers<T>(Func<UserManager<User>, User, Task<T>> action)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        return await action(users, (await users.FindByIdAsync(_user.Id.ToString()))!);
    }

    private async Task<Session> AddSession(User user)
    {
        await using var db = Fresh();
        var application = await db.AddApplication("billing");
        var role = await db.AddRole(application, "Member");
        db.UserApplications.Add(new UserApplication { UserId = user.Id, ApplicationId = application.Id });
        var session = new Session
        {
            UserId = user.Id, ApplicationId = application.Id, RoleId = role.Id, ExpiresAt = _app.Clock.GetUtcNow().AddHours(8),
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    private AuthDbContext Fresh() => postgres.CreateDbContext(
        new Npgsql.NpgsqlConnectionStringBuilder(_app.ConnectionString).Database!, _app.Clock);
}
