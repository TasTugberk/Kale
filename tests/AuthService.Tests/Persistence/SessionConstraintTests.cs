using AuthService.Domain;
using AuthService.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace AuthService.Tests.Persistence;

[Collection(UsesPostgres.Name)]
public sealed class SessionConstraintTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    private AuthDbContext _db = null!;
    private Application _billing = null!;
    private Role _accountant = null!;
    private User _user = null!;

    public async Task InitializeAsync()
    {
        _db = postgres.CreateDbContext(new FakeTimeProvider(Start));
        await _db.Database.MigrateAsync();

        _billing = await _db.AddApplication("billing");
        _accountant = await _db.AddRole(_billing, "Accountant");
        _user = await _db.AddUser("deniz");
        _db.UserApplications.Add(new UserApplication { UserId = _user.Id, ApplicationId = _billing.Id });
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task A_session_for_a_user_with_access_and_a_role_of_that_application_is_stored()
    {
        _db.Sessions.Add(NewSession(_billing, _accountant.Id));
        await _db.SaveChangesAsync();

        (await _db.Sessions.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Session_role_must_belong_to_the_session_application()
    {
        var hr = await _db.AddApplication("hr");
        var payroll = await _db.AddRole(hr, "Payroll");

        _db.Sessions.Add(NewSession(_billing, payroll.Id));

        await DbAssert.ShouldViolate(_db, PostgresErrorCodes.ForeignKeyViolation, "fk_sessions_role_same_application");
    }

    [Fact]
    public async Task Session_requires_the_user_to_have_access_to_the_application()
    {
        var hr = await _db.AddApplication("hr");
        var payroll = await _db.AddRole(hr, "Payroll");

        // Role and application match, but the user has no user_applications row for hr.
        _db.Sessions.Add(NewSession(hr, payroll.Id));

        await DbAssert.ShouldViolate(_db, PostgresErrorCodes.ForeignKeyViolation, "fk_sessions_user_application");
    }

    [Fact]
    public async Task Session_must_expire_after_it_was_created()
    {
        _db.Sessions.Add(NewSession(_billing, _accountant.Id, expiresAt: Start.AddMinutes(-1)));

        await DbAssert.ShouldViolate(_db, PostgresErrorCodes.CheckViolation, "ck_sessions_expires_after_created");
    }

    [Fact]
    public async Task Deleting_a_role_deletes_its_sessions()
    {
        _db.Sessions.Add(NewSession(_billing, _accountant.Id));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        _db.Roles.Remove(_accountant);
        await _db.SaveChangesAsync();

        (await _db.Sessions.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Removing_a_users_access_deletes_their_sessions_for_that_application()
    {
        _db.Sessions.Add(NewSession(_billing, _accountant.Id));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        await _db.UserApplications.Where(ua => ua.UserId == _user.Id).ExecuteDeleteAsync();

        (await _db.Sessions.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Ending_a_session_is_stored()
    {
        var session = NewSession(_billing, _accountant.Id);
        _db.Sessions.Add(session);
        await _db.SaveChangesAsync();

        session.End(Start.AddHours(1));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var stored = await _db.Sessions.SingleAsync();
        stored.EndedAt.ShouldBe(Start.AddHours(1));
        stored.IsActiveAt(Start.AddHours(2)).ShouldBeFalse();
    }

    private Session NewSession(Application application, Guid roleId, DateTimeOffset? expiresAt = null) => new()
    {
        UserId = _user.Id,
        ApplicationId = application.Id,
        RoleId = roleId,
        ExpiresAt = expiresAt ?? Start.AddHours(8),
    };
}
