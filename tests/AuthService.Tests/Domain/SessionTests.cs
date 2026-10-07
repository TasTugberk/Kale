using AuthService.Domain;

namespace AuthService.Tests.Domain;

public sealed class SessionTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_session_is_active_until_it_expires()
    {
        var session = NewSession(expiresAt: Start.AddHours(8));

        session.IsActiveAt(Start).ShouldBeTrue();
        session.IsActiveAt(Start.AddHours(8).AddTicks(-1)).ShouldBeTrue();
    }

    [Fact]
    public void A_session_is_not_active_from_its_expiry_time_on()
    {
        var session = NewSession(expiresAt: Start.AddHours(8));

        session.IsActiveAt(Start.AddHours(8)).ShouldBeFalse();
        session.IsActiveAt(Start.AddHours(9)).ShouldBeFalse();
    }

    [Fact]
    public void An_ended_session_is_not_active_even_before_it_expires()
    {
        var session = NewSession(expiresAt: Start.AddHours(8));

        session.End(Start.AddHours(1));

        session.IsActiveAt(Start.AddHours(2)).ShouldBeFalse();
        session.EndedAt.ShouldBe(Start.AddHours(1));
    }

    [Fact]
    public void Ending_twice_keeps_the_first_end_time()
    {
        var session = NewSession(expiresAt: Start.AddHours(8));

        session.End(Start.AddHours(1));
        session.End(Start.AddHours(2));

        session.EndedAt.ShouldBe(Start.AddHours(1));
    }

    private static Session NewSession(DateTimeOffset expiresAt) => new()
    {
        UserId = Guid.CreateVersion7(),
        ApplicationId = Guid.CreateVersion7(),
        RoleId = Guid.CreateVersion7(),
        ExpiresAt = expiresAt,
    };
}
