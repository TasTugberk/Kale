using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Auth.Client.Tests.Authorization;

/// <summary>
/// [Authorize(Operation)] through ASP.NET Core's real authorization service and middleware result handler, with a
/// fake AuthService. A refusal says why: 401 (the session is over: sign in again), 503 (AuthService couldn't be
/// asked) or 403 (the role lacks the operation).
/// </summary>
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
        services.AddAuthentication(TestScheme.Name).AddScheme<AuthenticationSchemeOptions, TestScheme>(TestScheme.Name, configureOptions: null);
        _services = services.BuildServiceProvider();
    }

    public void Dispose() => _services.Dispose();

    [Fact]
    public async Task A_role_holding_the_operation_is_allowed()
    {
        (await StatusFor(SignedIn("s-1"), new AuthorizeAttribute(BillingOperations.InvoiceRead))).ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task One_attribute_with_several_operations_needs_any_of_them()
    {
        (await StatusFor(SignedIn("s-1"), new AuthorizeAttribute(BillingOperations.Admin, BillingOperations.PaymentRead))).ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task Stacked_attributes_need_all_of_them()
    {
        var user = SignedIn("s-1");

        (await StatusFor(user, new AuthorizeAttribute(BillingOperations.InvoiceRead), new AuthorizeAttribute(BillingOperations.PaymentRead)))
            .ShouldBe(StatusCodes.Status200OK);
        (await StatusFor(user, new AuthorizeAttribute(BillingOperations.InvoiceRead), new AuthorizeAttribute(BillingOperations.Admin)))
            .ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task A_role_without_the_operation_is_forbidden()
    {
        (await StatusFor(SignedIn("s-1"), new AuthorizeAttribute(BillingOperations.InvoiceManage))).ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task An_ended_session_gets_401_even_with_a_valid_token()
    {
        _authService.Sessions["s-1"] = new SessionInfo(IsActive: false, RoleId: Accountant);

        // 401, not 403: the caller should sign in again, not show "not allowed".
        (await StatusFor(SignedIn("s-1"), new AuthorizeAttribute(BillingOperations.InvoiceRead))).ShouldBe(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task An_unknown_session_gets_401()
    {
        (await StatusFor(SignedIn("s-unknown"), new AuthorizeAttribute(BillingOperations.InvoiceRead))).ShouldBe(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task A_session_whose_role_is_gone_gets_401()
    {
        // Deleting a role stops its sessions; until the client hears of it, the role is simply unknown.
        _authService.RoleOperations.Remove(Accountant);

        (await StatusFor(SignedIn("s-1"), new AuthorizeAttribute(BillingOperations.InvoiceRead))).ShouldBe(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task A_token_without_a_session_is_forbidden()
    {
        // E.g. an application's own client-credentials token: valid, but not a user's session.
        var noSession = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u-1")], authenticationType: "Bearer"));

        (await StatusFor(noSession, new AuthorizeAttribute(BillingOperations.InvoiceRead))).ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task An_anonymous_caller_gets_401()
    {
        (await StatusFor(new ClaimsPrincipal(new ClaimsIdentity()), new AuthorizeAttribute(BillingOperations.InvoiceRead)))
            .ShouldBe(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task An_operation_of_another_enum_fails_loudly()
    {
        await Should.ThrowAsync<InvalidOperationException>(() =>
            StatusFor(SignedIn("s-1"), new AuthorizeAttribute(OtherOperations.Something)));
    }

    [Fact]
    public async Task A_same_named_member_of_another_enum_is_not_mistaken_for_ours()
    {
        // OtherOperations has an "InvoiceRead" too; it must not grant billing.InvoiceRead.
        await Should.ThrowAsync<InvalidOperationException>(() =>
            StatusFor(SignedIn("s-1"), new AuthorizeAttribute(OtherOperations.InvoiceRead)));
    }

    [Fact]
    public async Task When_AuthService_cannot_be_reached_and_nothing_is_cached_the_request_gets_503()
    {
        // Fails closed: no answer from AuthService means no access. 503 says it's an outage, not the user's rights.
        _authService.FailWith = new AuthServiceUnreachableException("AuthService is down");

        (await StatusFor(SignedIn("s-1"), new AuthorizeAttribute(BillingOperations.InvoiceRead))).ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task While_AuthService_cannot_be_reached_a_cached_answer_still_counts()
    {
        _authService.Sessions["s-1"] = new SessionInfo(IsActive: false, RoleId: Accountant);
        await StatusFor(SignedIn("s-1"), new AuthorizeAttribute(BillingOperations.InvoiceRead));
        _authService.FailWith = new AuthServiceUnreachableException("AuthService is down");

        (await StatusFor(SignedIn("s-1"), new AuthorizeAttribute(BillingOperations.InvoiceRead))).ShouldBe(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task When_AuthService_answers_with_an_error_the_request_is_forbidden()
    {
        // E.g. AuthService refused this application's own token (deactivated, secret changed): an answer, not an
        // outage, so it stays a plain refusal.
        _authService.FailWith = new InvalidOperationException("refused");

        (await StatusFor(SignedIn("s-1"), new AuthorizeAttribute(BillingOperations.InvoiceRead))).ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task A_timeout_from_AuthService_also_refuses_the_request()
    {
        // Not the request's own cancellation (e.g. a gRPC deadline): refuse, don't fail with an error.
        _authService.FailWith = new OperationCanceledException("deadline exceeded");

        (await StatusFor(SignedIn("s-1"), new AuthorizeAttribute(BillingOperations.InvoiceRead))).ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task The_apps_own_named_policies_keep_working()
    {
        var policy = await _services.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync("app-specific");

        policy.ShouldNotBeNull();
    }

    private static ClaimsPrincipal SignedIn(string sessionId) =>
        new(new ClaimsIdentity([new Claim("sub", "u-1"), new Claim("sid", sessionId)], authenticationType: "Bearer"));

    // What the authorization middleware does for an endpoint: combine its attributes as ASP.NET Core does,
    // authenticate, authorize, then let the registered result handler answer. 200 when the request reached the
    // endpoint.
    private async Task<int> StatusFor(ClaimsPrincipal user, params IAuthorizeData[] attributes)
    {
        var policy = (await AuthorizationPolicy.CombineAsync(_services.GetRequiredService<IAuthorizationPolicyProvider>(), attributes))!;
        // One scope per request, as ASP.NET Core does: authentication handlers belong to their request.
        await using var scope = _services.CreateAsyncScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider, User = user };
        var evaluator = _services.GetRequiredService<IPolicyEvaluator>();
        var authentication = await evaluator.AuthenticateAsync(policy, http);
        var result = await evaluator.AuthorizeAsync(policy, authentication, http, resource: http);

        var reachedEndpoint = false;
        await _services.GetRequiredService<IAuthorizationMiddlewareResultHandler>().HandleAsync(
            _ =>
            {
                reachedEndpoint = true;
                return Task.CompletedTask;
            },
            http,
            policy,
            result);
        return reachedEndpoint ? StatusCodes.Status200OK : http.Response.StatusCode;
    }

    // Stands in for the app's scheme (JWT bearer, Kale.Auth.Web's cookie): AuthenticationHandler's defaults answer
    // a challenge with 401 and a forbid with 403.
    private sealed class TestScheme(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Name = "Test";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }
}
