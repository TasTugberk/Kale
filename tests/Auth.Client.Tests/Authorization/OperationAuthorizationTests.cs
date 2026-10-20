using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Auth.Client.Tests.Authorization;

/// <summary>[Authorize(Operation)] through ASP.NET Core's real authorization service, with a fake AuthService.</summary>
public sealed class OperationAuthorizationTests : IDisposable
{
    private static readonly Guid Accountant = Guid.CreateVersion7();

    private readonly FakeAuthService _authService = new();
    private readonly ServiceProvider _services;

    public OperationAuthorizationTests()
    {
        _authService.Sessions["s-1"] = new SessionInfo(IsActive: true, RoleId: Accountant);
        _authService.RoleOperations[Accountant] = ["billing.InvoiceRead", "billing.PaymentRead"];

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(options => options.AddPolicy("app-specific", policy => policy.RequireClaim("department")));
        services.AddKaleOperationAuthorization(OperationCatalog.For<BillingOperations>("billing"));
        services.AddSingleton<ISessionLookup>(_authService);
        services.AddSingleton<IRoleOperationsLookup>(_authService);
        services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        _services = services.BuildServiceProvider();
    }

    public void Dispose() => _services.Dispose();

    [Fact]
    public async Task A_role_holding_the_operation_is_allowed()
    {
        (await Allowed(SignedIn("s-1"), new AuthorizeAttribute(BillingOperations.InvoiceRead))).ShouldBeTrue();
    }

    [Fact]
    public async Task One_attribute_with_several_operations_needs_any_of_them()
    {
        (await Allowed(SignedIn("s-1"), new AuthorizeAttribute(BillingOperations.Admin, BillingOperations.PaymentRead))).ShouldBeTrue();
    }

    [Fact]
    public async Task Stacked_attributes_need_all_of_them()
    {
        var user = SignedIn("s-1");

        (await Allowed(user, new AuthorizeAttribute(BillingOperations.InvoiceRead), new AuthorizeAttribute(BillingOperations.PaymentRead))).ShouldBeTrue();
        (await Allowed(user, new AuthorizeAttribute(BillingOperations.InvoiceRead), new AuthorizeAttribute(BillingOperations.Admin))).ShouldBeFalse();
    }

    [Fact]
    public async Task A_role_without_the_operation_is_refused()
    {
        (await Allowed(SignedIn("s-1"), new AuthorizeAttribute(BillingOperations.InvoiceManage))).ShouldBeFalse();
    }

    [Fact]
    public async Task An_ended_session_is_refused_even_with_a_valid_token()
    {
        _authService.Sessions["s-1"] = new SessionInfo(IsActive: false, RoleId: Accountant);

        (await Allowed(SignedIn("s-1"), new AuthorizeAttribute(BillingOperations.InvoiceRead))).ShouldBeFalse();
    }

    [Fact]
    public async Task An_unknown_session_is_refused()
    {
        (await Allowed(SignedIn("s-unknown"), new AuthorizeAttribute(BillingOperations.InvoiceRead))).ShouldBeFalse();
    }

    [Fact]
    public async Task A_token_without_a_session_is_refused()
    {
        var noSession = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u-1")], authenticationType: "Bearer"));

        (await Allowed(noSession, new AuthorizeAttribute(BillingOperations.InvoiceRead))).ShouldBeFalse();
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        (await Allowed(new ClaimsPrincipal(new ClaimsIdentity()), new AuthorizeAttribute(BillingOperations.InvoiceRead))).ShouldBeFalse();
    }

    [Fact]
    public async Task An_operation_of_another_enum_fails_loudly()
    {
        await Should.ThrowAsync<InvalidOperationException>(() =>
            Allowed(SignedIn("s-1"), new AuthorizeAttribute(OtherOperations.Something)));
    }

    [Fact]
    public async Task A_same_named_member_of_another_enum_is_not_mistaken_for_ours()
    {
        // OtherOperations has an "InvoiceRead" too; it must not grant billing.InvoiceRead.
        await Should.ThrowAsync<InvalidOperationException>(() =>
            Allowed(SignedIn("s-1"), new AuthorizeAttribute(OtherOperations.InvoiceRead)));
    }

    [Fact]
    public async Task When_AuthService_cannot_be_reached_the_request_is_refused()
    {
        // Fails closed: no answer from AuthService means no access, not an error page.
        _authService.FailWith = new InvalidOperationException("AuthService is down");

        (await Allowed(SignedIn("s-1"), new AuthorizeAttribute(BillingOperations.InvoiceRead))).ShouldBeFalse();
    }

    [Fact]
    public async Task A_timeout_from_AuthService_also_refuses_the_request()
    {
        // Not the request's own cancellation (e.g. a gRPC deadline): refuse, don't fail with an error.
        _authService.FailWith = new OperationCanceledException("deadline exceeded");

        (await Allowed(SignedIn("s-1"), new AuthorizeAttribute(BillingOperations.InvoiceRead))).ShouldBeFalse();
    }

    [Fact]
    public async Task The_apps_own_named_policies_keep_working()
    {
        var policy = await _services.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync("app-specific");

        policy.ShouldNotBeNull();
    }

    private static ClaimsPrincipal SignedIn(string sessionId) =>
        new(new ClaimsIdentity([new Claim("sub", "u-1"), new Claim("sid", sessionId)], authenticationType: "Bearer"));

    // Combines the attributes exactly as ASP.NET Core does for an endpoint, then evaluates them.
    private async Task<bool> Allowed(ClaimsPrincipal user, params IAuthorizeData[] attributes)
    {
        var provider = _services.GetRequiredService<IAuthorizationPolicyProvider>();
        var policy = await AuthorizationPolicy.CombineAsync(provider, attributes);
        var result = await _services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, resource: null, policy!);
        return result.Succeeded;
    }
}
