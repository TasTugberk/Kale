using System.Text.Json;
using Auth.Contracts.V1;
using AuthService.Applications;
using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Tests.Hosting;
using AuthService.Tests.Persistence;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using DomainSession = AuthService.Domain.Session;

namespace AuthService.Tests.Grpc;

/// <summary>
/// The gRPC contracts (#13) as client apps call them: authenticated with their client-credentials token, and
/// always answered for the calling application only.
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class GrpcServicesTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AuthServiceApp _app = null!;
    private HttpClient _http = null!;
    private GrpcChannel _channel = null!;
    private CreatedApplication _billing = null!;
    private CreatedApplication _hr = null!;

    // Scopes whose AuthDbContext a test keeps using after setup; disposed at the end.
    private readonly List<AsyncServiceScope> _scopes = [];

    public async Task InitializeAsync()
    {
        _app = await AuthServiceApp.CreateAsync(postgres);
        _http = _app.CreateClient();
        _channel = GrpcChannel.ForAddress(_app.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = _app.Server.CreateHandler() });
        _billing = await CreateApplication("billing");
        _hr = await CreateApplication("hr");
    }

    public async Task DisposeAsync()
    {
        foreach (var scope in _scopes)
        {
            await scope.DisposeAsync();
        }

        _channel.Dispose();
        _http.Dispose();
        await _app.DisposeAsync();
    }

    // ----- Who may call -----

    [Fact]
    public async Task A_call_without_a_token_is_unauthenticated()
    {
        var registration = new OperationRegistration.OperationRegistrationClient(_channel);

        var error = await Should.ThrowAsync<RpcException>(() => registration.RegisterOperationsAsync(Request("InvoiceRead")).ResponseAsync);

        error.StatusCode.ShouldBe(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task A_deactivated_application_is_refused_even_with_a_still_valid_token()
    {
        var headers = await AuthorizationFor(_billing);
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationService>().SetActiveAsync(_billing.Application.Id, isActive: false);
        }

        var registration = new OperationRegistration.OperationRegistrationClient(_channel);
        var error = await Should.ThrowAsync<RpcException>(() =>
            registration.RegisterOperationsAsync(Request("InvoiceRead"), headers).ResponseAsync);

        error.StatusCode.ShouldBe(StatusCode.PermissionDenied);
    }

    // ----- OperationRegistration -----

    [Fact]
    public async Task Registration_stores_the_operations_under_the_calling_application()
    {
        var registration = new OperationRegistration.OperationRegistrationClient(_channel);
        var request = Request("InvoiceRead");
        request.Operations.Add(new OperationDefinition { Name = "InvoiceManage", Implies = { "InvoiceRead" } });

        var response = await registration.RegisterOperationsAsync(request, await AuthorizationFor(_billing));

        response.Added.ShouldBe(["billing.InvoiceRead", "billing.InvoiceManage"], ignoreOrder: true);
    }

    [Fact]
    public async Task An_invalid_registration_is_an_invalid_argument()
    {
        var registration = new OperationRegistration.OperationRegistrationClient(_channel);
        var headers = await AuthorizationFor(_billing);

        var error = await Should.ThrowAsync<RpcException>(() =>
            registration.RegisterOperationsAsync(new RegisterOperationsRequest(), headers).ResponseAsync);

        error.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        error.Status.Detail.ShouldContain("empty");
    }

    // ----- SessionService -----

    [Fact]
    public async Task An_active_session_is_reported_with_its_role_and_user()
    {
        var (session, _) = await AddSession(_billing.Application);
        var sessions = new SessionService.SessionServiceClient(_channel);

        var response = await sessions.GetSessionAsync(
            new GetSessionRequest { SessionId = session.Id.ToString() }, await AuthorizationFor(_billing));

        response.Status.ShouldBe(SessionStatus.Active);
        response.RoleId.ShouldBe(session.RoleId.ToString());
        response.UserId.ShouldBe(session.UserId.ToString());
        response.ExpiresAt.ToDateTimeOffset().ShouldBe(session.ExpiresAt);
    }

    [Fact]
    public async Task An_ended_session_is_reported_as_ended()
    {
        var (session, db) = await AddSession(_billing.Application);
        session.End(_app.Clock.GetUtcNow());
        await db.SaveChangesAsync();
        var sessions = new SessionService.SessionServiceClient(_channel);

        var response = await sessions.GetSessionAsync(
            new GetSessionRequest { SessionId = session.Id.ToString() }, await AuthorizationFor(_billing));

        response.Status.ShouldBe(SessionStatus.Ended);
    }

    [Fact]
    public async Task Another_applications_session_is_not_found()
    {
        var (hrSession, _) = await AddSession(_hr.Application);
        var sessions = new SessionService.SessionServiceClient(_channel);

        var error = await Should.ThrowAsync<RpcException>(async () => await sessions.GetSessionAsync(
            new GetSessionRequest { SessionId = hrSession.Id.ToString() }, await AuthorizationFor(_billing)));

        error.StatusCode.ShouldBe(StatusCode.NotFound);
    }

    [Theory]
    [InlineData("not-a-uuid", StatusCode.InvalidArgument)]
    [InlineData("01a1d0c4-0000-7000-8000-000000000000", StatusCode.NotFound)]
    public async Task A_malformed_or_unknown_session_id_is_refused(string sessionId, StatusCode expected)
    {
        var sessions = new SessionService.SessionServiceClient(_channel);

        var error = await Should.ThrowAsync<RpcException>(async () => await sessions.GetSessionAsync(
            new GetSessionRequest { SessionId = sessionId }, await AuthorizationFor(_billing)));

        error.StatusCode.ShouldBe(expected);
    }

    // ----- RoleService -----

    [Fact]
    public async Task A_roles_effective_operations_include_implications()
    {
        var registration = new OperationRegistration.OperationRegistrationClient(_channel);
        var headers = await AuthorizationFor(_billing);
        var request = Request("InvoiceRead");
        request.Operations.Add(new OperationDefinition { Name = "InvoiceManage", Implies = { "InvoiceRead" } });
        await registration.RegisterOperationsAsync(request, headers);
        var role = await AddRoleWith(_billing.Application, "billing.InvoiceManage");

        var roles = new RoleService.RoleServiceClient(_channel);
        var response = await roles.GetEffectiveOperationsAsync(new GetEffectiveOperationsRequest { RoleId = role.Id.ToString() }, headers);

        response.Operations.ShouldBe(["billing.InvoiceManage", "billing.InvoiceRead"], ignoreOrder: true);
    }

    [Fact]
    public async Task Another_applications_role_is_not_found()
    {
        var hrRole = await AddRoleWith(_hr.Application);
        var roles = new RoleService.RoleServiceClient(_channel);

        var error = await Should.ThrowAsync<RpcException>(async () => await roles.GetEffectiveOperationsAsync(
            new GetEffectiveOperationsRequest { RoleId = hrRole.Id.ToString() }, await AuthorizationFor(_billing)));

        error.StatusCode.ShouldBe(StatusCode.NotFound);
    }

    [Theory]
    [InlineData("not-a-uuid", StatusCode.InvalidArgument)]
    [InlineData("01a1d0c4-0000-7000-8000-000000000000", StatusCode.NotFound)]
    public async Task A_malformed_or_unknown_role_id_is_refused(string roleId, StatusCode expected)
    {
        var roles = new RoleService.RoleServiceClient(_channel);

        var error = await Should.ThrowAsync<RpcException>(async () => await roles.GetEffectiveOperationsAsync(
            new GetEffectiveOperationsRequest { RoleId = roleId }, await AuthorizationFor(_billing)));

        error.StatusCode.ShouldBe(expected);
    }

    // ----- Helpers -----

    private static RegisterOperationsRequest Request(string operation) =>
        new() { Operations = { new OperationDefinition { Name = operation } } };

    private async Task<CreatedApplication> CreateApplication(string key)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationService>()
            .CreateAsync(new NewApplication(key, key, [new Uri($"https://{key}.example/signin-oidc")], []));
    }

    private async Task<Metadata> AuthorizationFor(CreatedApplication application)
    {
        using var response = await _http.PostAsync(new Uri("/connect/token", UriKind.Relative), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = application.Application.Key,
            ["client_secret"] = application.ClientSecret,
        }));
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new Metadata { { "Authorization", $"Bearer {body.RootElement.GetProperty("access_token").GetString()}" } };
    }

    // A user with access and a role in the application, signed in: everything an ACTIVE session needs.
    private async Task<(DomainSession Session, AuthDbContext Db)> AddSession(Application application)
    {
        var scope = _app.Services.CreateAsyncScope();
        _scopes.Add(scope);
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var role = await db.AddRole(application, "Member");
        var user = await db.AddUser($"user-of-{application.Key}");
        db.UserApplications.Add(new UserApplication { UserId = user.Id, ApplicationId = application.Id });
        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        var session = new DomainSession
        {
            UserId = user.Id, ApplicationId = application.Id, RoleId = role.Id, ExpiresAt = _app.Clock.GetUtcNow().AddHours(8),
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        return (session, db);
    }

    private async Task<Role> AddRoleWith(Application application, params string[] operationNames)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var role = await db.AddRole(application, "Role-" + Guid.CreateVersion7().ToString("N")[..8]);
        foreach (var name in operationNames)
        {
            var operation = db.Operations.Single(o => o.Name == name);
            db.RoleOperations.Add(new RoleOperation { ApplicationId = application.Id, RoleId = role.Id, OperationId = operation.Id });
        }

        await db.SaveChangesAsync();
        return role;
    }
}
