using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Roles;
using AuthService.SignIn;
using AuthService.Tests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Tests.SignIn;

/// <summary>
/// Signing in and disabling the user at the same moment: the sign-in must not create a session after the user
/// was disabled (it would come back to life if the user were enabled again).
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class UserSignInTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AuthDbContext _db = null!;
    private UserSignIn _signIn = null!;
    private User _user = null!;

    public async Task InitializeAsync()
    {
        _db = postgres.CreateDbContext();
        await _db.Database.MigrateAsync();
        _signIn = new UserSignIn(_db, new EligibleRoles(_db), new SessionSettings(TimeSpan.FromHours(8)), TimeProvider.System);

        var billing = await _db.AddApplication("billing");
        var member = await _db.AddRole(billing, "Member");
        _user = await _db.AddUser("deniz");
        _db.UserApplications.Add(new UserApplication { UserId = _user.Id, ApplicationId = billing.Id });
        _db.UserRoles.Add(new UserRole { UserId = _user.Id, RoleId = member.Id });
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task A_sign_in_waits_for_a_disable_in_progress_and_is_then_denied()
    {
        // An admin's DisableAsync holds the user's row lock until it commits.
        await using var admin = postgres.CreateDbContext(_db.Database.GetDbConnection().Database);
        await using var transaction = await admin.Database.BeginTransactionAsync();
        await admin.Database.ExecuteSqlAsync($"SELECT 1 FROM users WHERE id = {_user.Id} FOR UPDATE");
        await admin.Database.ExecuteSqlAsync($"UPDATE users SET is_active = false WHERE id = {_user.Id}");

        var signIn = _signIn.StartAsync(_user.Id, "billing", chosenRoleId: null);
        await Task.Delay(500);
        signIn.IsCompleted.ShouldBeFalse("the sign-in must wait for the disable");

        await transaction.CommitAsync();

        (await signIn).ShouldBeOfType<SignInOutcome.Denied>();
        (await _db.Sessions.CountAsync()).ShouldBe(0);
    }
}
