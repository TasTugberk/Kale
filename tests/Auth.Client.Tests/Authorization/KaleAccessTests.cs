using System.Security.Claims;
using Microsoft.Extensions.Time.Testing;

namespace Auth.Client.Tests.Authorization;

/// <summary>
/// The check in code ("may this user approve?") and the question "what may this user do?": the same session and
/// role checks, and the same caches, as [Authorize(Operation)].
/// </summary>
public sealed class KaleAccessTests
{
    private readonly FakeAuthService _kale = new();
    private readonly KaleAccess<BillingOperations> _access;
    private readonly Guid _roleId = Guid.CreateVersion7();

    public KaleAccessTests()
    {
        var sessions = new SessionAccess(_kale, _kale, new FakeTimeProvider());
        _access = new KaleAccess<BillingOperations>(sessions, OperationCatalog.For<BillingOperations>("billing"));

        // AuthService answers a role's operations with implications already expanded.
        _kale.RoleOperations[_roleId] = ["billing.InvoiceManage", "billing.InvoiceRead"];
    }

    [Fact]
    public async Task A_user_holds_the_operations_of_their_sessions_role()
    {
        var user = UserWithSession("s1", active: true);

        (await _access.HasAsync(user, BillingOperations.InvoiceManage)).ShouldBeTrue();
        (await _access.HasAsync(user, BillingOperations.InvoiceRead)).ShouldBeTrue();
        (await _access.HasAsync(user, BillingOperations.PaymentRead)).ShouldBeFalse();
    }

    [Fact]
    public async Task The_operations_come_back_as_enum_members()
    {
        var operations = await _access.GetOperationsAsync(UserWithSession("s1", active: true));

        operations.ShouldBe([BillingOperations.InvoiceRead, BillingOperations.InvoiceManage], ignoreOrder: true);
    }

    [Fact]
    public async Task An_ended_session_holds_nothing()
    {
        var user = UserWithSession("s1", active: false);

        (await _access.HasAsync(user, BillingOperations.InvoiceRead)).ShouldBeFalse();
        (await _access.GetOperationsAsync(user)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_user_without_a_Kale_session_holds_nothing()
    {
        // E.g. an application's own token, or nobody signed in.
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        (await _access.HasAsync(anonymous, BillingOperations.InvoiceRead)).ShouldBeFalse();
        (await _access.GetOperationsAsync(anonymous)).ShouldBeEmpty();
        _kale.SessionCalls.ShouldBe(0, "nothing to ask Kale about");
    }

    [Fact]
    public async Task Answers_are_cached_like_the_Authorize_checks()
    {
        var user = UserWithSession("s1", active: true);

        await _access.HasAsync(user, BillingOperations.InvoiceRead);
        await _access.GetOperationsAsync(user);

        _kale.SessionCalls.ShouldBe(1);
        _kale.RoleCalls.ShouldBe(1);
    }

    [Fact]
    public async Task When_Kale_cannot_be_reached_it_fails_instead_of_guessing()
    {
        _kale.FailWith = new AuthServiceUnreachableException("down");

        await Should.ThrowAsync<AuthServiceUnreachableException>(() =>
            _access.HasAsync(UserWithSession("s1", active: true), BillingOperations.InvoiceRead));
    }

    private ClaimsPrincipal UserWithSession(string sessionId, bool active)
    {
        _kale.Sessions[sessionId] = new SessionInfo(active, _roleId);
        return new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u1"), new Claim("sid", sessionId)], "Bearer"));
    }
}
