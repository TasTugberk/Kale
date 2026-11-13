using Microsoft.Extensions.Time.Testing;

namespace Auth.Client.Tests.Authorization;

/// <summary>
/// DESIGN.md per-request check: session status cached briefly (~20 s), a role's operations cached per role.
/// Events (Phase 5) evict entries; the time limits are the fallback when an event is lost.
/// </summary>
public sealed class SessionAccessTests
{
    private static readonly Guid Accountant = Guid.CreateVersion7();

    private readonly FakeAuthService _authService = new();
    private readonly FakeTimeProvider _clock = new();
    private readonly SessionAccess _access;

    public SessionAccessTests()
    {
        _authService.Sessions["s-1"] = new SessionInfo(IsActive: true, RoleId: Accountant);
        _authService.RoleOperations[Accountant] = ["billing.InvoiceRead"];
        _access = new SessionAccess(_authService, _authService, _clock);
    }

    [Fact]
    public async Task An_active_sessions_role_operations_are_returned()
    {
        (await _access.GetOperationsAsync("s-1")).ShouldBe(["billing.InvoiceRead"]);
    }

    [Fact]
    public async Task The_session_status_is_reused_for_20_seconds_then_fetched_again()
    {
        await _access.GetOperationsAsync("s-1");
        _clock.Advance(TimeSpan.FromSeconds(19));
        await _access.GetOperationsAsync("s-1");
        _authService.SessionCalls.ShouldBe(1);

        _clock.Advance(TimeSpan.FromSeconds(2));
        await _access.GetOperationsAsync("s-1");
        _authService.SessionCalls.ShouldBe(2);
    }

    [Fact]
    public async Task A_roles_operations_are_reused_for_5_minutes_then_fetched_again()
    {
        await _access.GetOperationsAsync("s-1");
        _clock.Advance(TimeSpan.FromMinutes(4));
        await _access.GetOperationsAsync("s-1");
        _authService.RoleCalls.ShouldBe(1);

        _clock.Advance(TimeSpan.FromMinutes(2));
        await _access.GetOperationsAsync("s-1");
        _authService.RoleCalls.ShouldBe(2);
    }

    [Fact]
    public async Task Evicting_a_session_makes_the_next_check_ask_AuthService()
    {
        await _access.GetOperationsAsync("s-1");
        _authService.Sessions["s-1"] = new SessionInfo(IsActive: false, RoleId: Accountant);

        _access.EvictSession("s-1");

        (await _access.GetOperationsAsync("s-1")).ShouldBeNull();
    }

    [Fact]
    public async Task Evicting_a_role_makes_the_next_check_ask_AuthService()
    {
        await _access.GetOperationsAsync("s-1");
        _authService.RoleOperations[Accountant] = ["billing.InvoiceRead", "billing.PaymentRead"];

        _access.EvictRole(Accountant);

        (await _access.GetOperationsAsync("s-1")).ShouldBe(["billing.InvoiceRead", "billing.PaymentRead"], ignoreOrder: true);
    }

    [Fact]
    public async Task An_eviction_during_a_running_lookup_is_not_undone_by_its_stale_answer()
    {
        // A role lookup starts and gets the old operations; the role is edited (event) before it finishes.
        _authService.HoldRoleLookups = new TaskCompletionSource();
        var running = _access.GetOperationsAsync("s-1");
        _authService.RoleOperations[Accountant] = ["billing.PaymentRead"];
        _access.EvictRole(Accountant);
        _authService.HoldRoleLookups.SetResult();
        await running;
        _authService.HoldRoleLookups = null;

        // The stale answer must not have been cached: the next check sees the edit.
        (await _access.GetOperationsAsync("s-1")).ShouldBe(["billing.PaymentRead"]);
    }

    [Fact]
    public async Task An_aborted_request_does_not_cancel_a_session_lookup_another_request_waits_for()
    {
        _authService.HoldSessionLookups = new TaskCompletionSource();
        using var abort = new CancellationTokenSource();
        var aborted = _access.GetOperationsAsync("s-1", abort.Token);
        var waiting = _access.GetOperationsAsync("s-1");

        await abort.CancelAsync();
        _authService.HoldSessionLookups.SetResult();

        await Should.ThrowAsync<OperationCanceledException>(() => aborted);
        (await waiting).ShouldBe(["billing.InvoiceRead"]);
        _authService.SessionCalls.ShouldBe(1);
    }

    [Fact]
    public async Task An_aborted_request_does_not_cancel_a_role_lookup_another_request_waits_for()
    {
        _authService.HoldRoleLookups = new TaskCompletionSource();
        using var abort = new CancellationTokenSource();
        var aborted = _access.GetOperationsAsync("s-1", abort.Token);
        var waiting = _access.GetOperationsAsync("s-1");

        await abort.CancelAsync();
        _authService.HoldRoleLookups.SetResult();

        await Should.ThrowAsync<OperationCanceledException>(() => aborted);
        (await waiting).ShouldBe(["billing.InvoiceRead"]);
        _authService.RoleCalls.ShouldBe(1);
    }

    [Fact]
    public async Task An_aborted_request_stops_waiting_for_its_lookup()
    {
        _authService.HoldSessionLookups = new TaskCompletionSource();
        using var abort = new CancellationTokenSource();
        var aborted = _access.GetOperationsAsync("s-1", abort.Token);

        await abort.CancelAsync();

        // The lookup is still held. The time limit turns "still waiting" into a failure instead of a hang.
        await Should.ThrowAsync<OperationCanceledException>(() => aborted.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task A_session_lookup_that_does_not_answer_within_5_seconds_is_given_up()
    {
        _authService.HoldSessionLookups = new TaskCompletionSource();
        var request = _access.GetOperationsAsync("s-1");

        _clock.Advance(TimeSpan.FromSeconds(4.9));
        request.IsCompleted.ShouldBeFalse();
        _clock.Advance(TimeSpan.FromSeconds(0.1));

        // Nobody aborted the request: the lookup itself was cancelled. The time limit turns a hang into a failure.
        await Should.ThrowAsync<AuthServiceUnreachableException>(() => request.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task A_role_lookup_that_does_not_answer_within_5_seconds_is_given_up()
    {
        _authService.HoldRoleLookups = new TaskCompletionSource();
        var request = _access.GetOperationsAsync("s-1");

        _clock.Advance(TimeSpan.FromSeconds(4.9));
        request.IsCompleted.ShouldBeFalse();
        _clock.Advance(TimeSpan.FromSeconds(0.1));

        await Should.ThrowAsync<AuthServiceUnreachableException>(() => request.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Expired_entries_are_eventually_removed()
    {
        for (var i = 0; i < 1_500; i++)
        {
            _authService.Sessions[$"s-{i}"] = new SessionInfo(IsActive: false, RoleId: Accountant);
            await _access.GetOperationsAsync($"s-{i}");
        }

        // Past the time a stale answer may still be used too.
        _clock.Advance(TimeSpan.FromMinutes(6));
        for (var i = 0; i < 1_000; i++)
        {
            await _access.GetOperationsAsync("s-fresh");
            _access.EvictSession("s-fresh");
        }

        _access.CachedSessionCount.ShouldBeLessThan(100, "expired sessions don't stay in memory forever");
    }

    [Fact]
    public async Task While_events_arrive_the_session_status_is_reused_for_2_minutes()
    {
        // A stopped session's event evicts it at once, so the time limit is only the fallback for a lost event.
        _access.ReceivingEvents = true;
        await _access.GetOperationsAsync("s-1");
        _clock.Advance(TimeSpan.FromSeconds(119));
        await _access.GetOperationsAsync("s-1");
        _authService.SessionCalls.ShouldBe(1);

        _clock.Advance(TimeSpan.FromSeconds(2));
        await _access.GetOperationsAsync("s-1");
        _authService.SessionCalls.ShouldBe(2);
    }

    [Fact]
    public async Task When_events_stop_arriving_a_session_status_older_than_20_seconds_is_fetched_again()
    {
        _access.ReceivingEvents = true;
        await _access.GetOperationsAsync("s-1");
        _clock.Advance(TimeSpan.FromSeconds(30));

        _access.ReceivingEvents = false;
        await _access.GetOperationsAsync("s-1");

        _authService.SessionCalls.ShouldBe(2);
    }

    [Fact]
    public async Task While_AuthService_is_unreachable_the_last_answers_are_used_for_5_more_minutes()
    {
        await _access.GetOperationsAsync("s-1");
        _authService.FailWith = new AuthServiceUnreachableException("down");

        // The session's answer expired 4m59s ago, the role's 19 s ago: both still usable.
        _clock.Advance(TimeSpan.FromSeconds(20) + TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        (await _access.GetOperationsAsync("s-1")).ShouldBe(["billing.InvoiceRead"]);

        // Then fail closed, as without any answer.
        _clock.Advance(TimeSpan.FromSeconds(1));
        await Should.ThrowAsync<AuthServiceUnreachableException>(() => _access.GetOperationsAsync("s-1"));
    }

    [Fact]
    public async Task A_stale_role_answer_is_used_while_AuthService_is_unreachable()
    {
        await _access.GetOperationsAsync("s-1");

        // The role's answer expired 4 minutes ago.
        _clock.Advance(TimeSpan.FromMinutes(9));
        _authService.FailWith = new AuthServiceUnreachableException("down");

        (await _access.GetRoleOperationsAsync(Accountant)).ShouldBe(["billing.InvoiceRead"]);
    }

    [Fact]
    public async Task An_answer_from_AuthService_that_is_not_an_outage_never_brings_back_a_stale_answer()
    {
        // E.g. AuthService refused this application's token: that is an answer, not an outage.
        await _access.GetOperationsAsync("s-1");
        _clock.Advance(TimeSpan.FromSeconds(21));
        _authService.FailWith = new InvalidOperationException("refused");

        await Should.ThrowAsync<InvalidOperationException>(() => _access.GetOperationsAsync("s-1"));
    }

    [Fact]
    public async Task An_evicted_answer_is_never_used_even_while_AuthService_is_unreachable()
    {
        await _access.GetOperationsAsync("s-1");
        _access.EvictSession("s-1");
        _authService.FailWith = new AuthServiceUnreachableException("down");

        await Should.ThrowAsync<AuthServiceUnreachableException>(() => _access.GetOperationsAsync("s-1"));
    }

    [Fact]
    public async Task An_ended_or_unknown_session_has_no_operations()
    {
        _authService.Sessions["s-2"] = new SessionInfo(IsActive: false, RoleId: Accountant);

        (await _access.GetOperationsAsync("s-2")).ShouldBeNull();
        (await _access.GetOperationsAsync("s-unknown")).ShouldBeNull();
        _authService.RoleCalls.ShouldBe(0, "no point loading operations for a session that isn't active");
    }
}
