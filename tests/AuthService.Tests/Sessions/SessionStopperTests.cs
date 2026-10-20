using Auth.Contracts.V1;
using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Sessions;
using AuthService.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace AuthService.Tests.Sessions;

/// <summary>
/// Stopping a session ends it and queues a "session stopped" event in the same save (outbox), so client apps
/// refuse its still-valid token right away instead of after their cache time.
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class SessionStopperTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Start);
    private AuthDbContext _db = null!;
    private SessionStopper _stopper = null!;
    private Session _session = null!;

    public async Task InitializeAsync()
    {
        _db = postgres.CreateDbContext(_clock);
        await _db.Database.MigrateAsync();
        _stopper = new SessionStopper(_db, _clock);

        var billing = await _db.AddApplication("billing");
        var accountant = await _db.AddRole(billing, "Accountant");
        var user = await _db.AddUser("deniz");
        _db.UserApplications.Add(new UserApplication { UserId = user.Id, ApplicationId = billing.Id });
        _session = new Session { UserId = user.Id, ApplicationId = billing.Id, RoleId = accountant.Id, ExpiresAt = Start.AddHours(8) };
        _db.Sessions.Add(_session);
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task Stopping_a_session_ends_it_and_queues_a_session_stopped_event()
    {
        _clock.Advance(TimeSpan.FromMinutes(5));

        (await _stopper.StopAsync(_session.Id)).ShouldBeTrue();

        await using var fresh = postgres.CreateDbContext(_db.Database.GetDbConnection().Database);
        (await fresh.Sessions.SingleAsync()).EndedAt.ShouldBe(Start.AddMinutes(5));
        var message = await fresh.OutboxMessages.SingleAsync();
        message.Type.ShouldBe("SessionStopped");
        var stored = InvalidationEvent.Parser.ParseFrom(message.Payload);
        stored.SessionStopped.SessionId.ShouldBe(_session.Id.ToString());
        stored.EventId.ShouldBe(message.Id.ToString());
    }

    [Fact]
    public async Task Stopping_an_ended_session_changes_nothing_and_queues_no_second_event()
    {
        await _stopper.StopAsync(_session.Id);
        _clock.Advance(TimeSpan.FromMinutes(5));

        (await _stopper.StopAsync(_session.Id)).ShouldBeTrue();

        (await _db.OutboxMessages.CountAsync()).ShouldBe(1);
        (await _db.Sessions.SingleAsync()).EndedAt.ShouldBe(Start);
    }

    [Fact]
    public async Task An_unknown_session_is_reported_and_queues_nothing()
    {
        (await _stopper.StopAsync(Guid.CreateVersion7())).ShouldBeFalse();

        (await _db.OutboxMessages.CountAsync()).ShouldBe(0);
    }
}
