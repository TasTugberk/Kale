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
    public async Task Expired_entries_are_eventually_removed()
    {
        for (var i = 0; i < 1_500; i++)
        {
            _authService.Sessions[$"s-{i}"] = new SessionInfo(IsActive: false, RoleId: Accountant);
            await _access.GetOperationsAsync($"s-{i}");
        }

        _clock.Advance(TimeSpan.FromMinutes(1));
        for (var i = 0; i < 1_000; i++)
        {
            await _access.GetOperationsAsync("s-fresh");
            _access.EvictSession("s-fresh");
        }

        _access.CachedSessionCount.ShouldBeLessThan(100, "expired sessions don't stay in memory forever");
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
