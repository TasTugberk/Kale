using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Roles;
using AuthService.Sessions;
using AuthService.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace AuthService.Tests.Sessions;

/// <summary>
/// The full definition of an ACTIVE session (sessions.proto, CLAUDE.md): used by GetSession and by token refresh.
/// Each test turns off exactly one condition.
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class SessionCheckTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Start);
    private AuthDbContext _db = null!;
    private SessionCheck _check = null!;
    private Application _billing = null!;
    private Role _accountant = null!;
    private User _user = null!;
    private UserApplication _access = null!;
    private Session _session = null!;

    public async Task InitializeAsync()
    {
        _db = postgres.CreateDbContext(_clock);
        await _db.Database.MigrateAsync();
        _check = new SessionCheck(_db, new EligibleRoles(_db), _clock);

        _billing = await _db.AddApplication("billing");
        _accountant = await _db.AddRole(_billing, "Accountant");
        _user = await _db.AddUser("deniz");
        _access = new UserApplication { UserId = _user.Id, ApplicationId = _billing.Id };
        _db.UserApplications.Add(_access);
        _db.UserRoles.Add(new UserRole { UserId = _user.Id, RoleId = _accountant.Id });
        _session = new Session { UserId = _user.Id, ApplicationId = _billing.Id, RoleId = _accountant.Id, ExpiresAt = Start.AddHours(8) };
        _db.Sessions.Add(_session);
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task A_session_is_active_when_everything_holds()
    {
        (await _check.IsActiveAsync(_session)).ShouldBeTrue();
    }

    [Fact]
    public async Task An_ended_session_is_not_active()
    {
        _session.End(Start.AddMinutes(5));
        await _db.SaveChangesAsync();

        (await _check.IsActiveAsync(_session)).ShouldBeFalse();
    }

    [Fact]
    public async Task An_expired_session_is_not_active()
    {
        _clock.Advance(TimeSpan.FromHours(8));

        (await _check.IsActiveAsync(_session)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_disabled_users_session_is_not_active()
    {
        _user.IsActive = false;
        await _db.SaveChangesAsync();

        (await _check.IsActiveAsync(_session)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_session_is_not_active_once_the_users_access_is_deactivated()
    {
        _access.IsActive = false;
        await _db.SaveChangesAsync();

        (await _check.IsActiveAsync(_session)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_session_of_a_deactivated_application_is_not_active()
    {
        _billing.IsActive = false;
        await _db.SaveChangesAsync();

        (await _check.IsActiveAsync(_session)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_session_is_not_active_once_its_role_is_taken_away()
    {
        await _db.UserRoles.Where(ur => ur.UserId == _user.Id).ExecuteDeleteAsync();

        (await _check.IsActiveAsync(_session)).ShouldBeFalse();
    }
}
