using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AuthService.Applications;
using AuthService.Persistence;
using AuthService.Tests.Hosting;
using AuthService.Tests.Persistence;
using AuthService.Tests.SignIn;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;

namespace EndToEnd.Tests;

/// <summary>
/// DESIGN.md end to end: a client app using Auth.Client against the real AuthService. Registration on startup,
/// tokens validated locally with the audience check, and each request checked against the session and the
/// role's operations.
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class ClientAppTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AuthServiceApp _auth = null!;
    private CreatedApplication _billing = null!;
    private CreatedApplication _hr = null!;

    public async Task InitializeAsync()
    {
        _auth = await AuthServiceApp.CreateAsync(postgres);
        await using var scope = _auth.Services.CreateAsyncScope();
        var applications = scope.ServiceProvider.GetRequiredService<ApplicationService>();
        _billing = await applications.CreateAsync(new NewApplication("billing", "Billing", [Browser.RedirectUri("billing")], []));
        _hr = await applications.CreateAsync(new NewApplication("hr", "HR", [Browser.RedirectUri("hr")], []));
    }

    public async Task DisposeAsync() => await _auth.DisposeAsync();

    [Fact]
    public async Task The_app_registers_its_operations_on_startup()
    {
        await using var client = await ClientApp.StartAsync(_auth, _billing.ClientSecret);

        await Eventually(async db => (await db.Operations.CountAsync()) == 2);
        await WithAuthDb(async db =>
        {
            (await db.Operations.Select(o => o.Name).ToListAsync()).ShouldBe(["billing.InvoiceRead", "billing.InvoiceManage"], ignoreOrder: true);
            (await db.OperationImplications.CountAsync()).ShouldBe(1);
        });
    }

    [Fact]
    public async Task Several_instances_starting_at_once_register_once()
    {
        var backchannels = new Backchannel[3];
        var instances = await Task.WhenAll(Enumerable.Range(0, 3).Select(i =>
            ClientApp.StartAsync(_auth, _billing.ClientSecret, watchBackchannel: handler => backchannels[i] = new Backchannel(handler))));
        try
        {
            await Eventually(async db => (await db.Operations.CountAsync()) == 2);

            // Each instance's single call succeeded (none had to retry), and the operations exist once.
            await Eventually(_ => Task.FromResult(backchannels.All(b => b.Calls(RegisterPath) >= 1)));
            backchannels.Select(b => b.Calls(RegisterPath)).ShouldBe([1, 1, 1]);
            await WithAuthDb(async db => (await db.Operations.CountAsync()).ShouldBe(2));
        }
        finally
        {
            foreach (var instance in instances)
            {
                await instance.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task A_user_whose_role_holds_the_operation_gets_in_and_others_do_not()
    {
        await using var client = await RegisteredClient();
        var token = await SignInWithRole("billing", "billing.InvoiceRead");

        (await Call(client, HttpMethod.Get, token)).ShouldBe(HttpStatusCode.OK);
        (await Call(client, HttpMethod.Post, token)).ShouldBe(HttpStatusCode.Forbidden, "InvoiceRead doesn't include InvoiceManage");
    }

    [Fact]
    public async Task An_implied_operation_counts()
    {
        await using var client = await RegisteredClient();
        var token = await SignInWithRole("billing", "billing.InvoiceManage");

        (await Call(client, HttpMethod.Get, token)).ShouldBe(HttpStatusCode.OK, "InvoiceManage implies InvoiceRead");
    }

    [Fact]
    public async Task A_token_for_another_application_is_rejected()
    {
        await using var client = await RegisteredClient();
        var hrToken = await SignInWithRole("hr", role: null);

        (await Call(client, HttpMethod.Get, hrToken)).ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_id_token_is_not_accepted_as_an_access_token()
    {
        // Same signature and audience as an access token, but it only says who signed in, not what they may call.
        await using var client = await RegisteredClient();
        var idToken = (await SignInTokens("billing", "billing.InvoiceRead")).GetProperty("id_token").GetString();

        (await Call(client, HttpMethod.Get, idToken)).ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_request_without_a_token_is_unauthenticated()
    {
        await using var client = await RegisteredClient();

        (await Call(client, HttpMethod.Get, token: null)).ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_stopped_session_is_refused_while_its_token_is_still_valid()
    {
        await using var client = await RegisteredClient();
        var token = await SignInWithRole("billing", "billing.InvoiceRead");
        (await Call(client, HttpMethod.Get, token)).ShouldBe(HttpStatusCode.OK);

        var sessionId = Guid.Parse(new JsonWebToken(token).GetClaim("sid").Value);
        await WithAuthDb(async db => (await db.Sessions.SingleAsync(s => s.Id == sessionId)).End(_auth.Clock.GetUtcNow()));

        // The token is valid for 10 minutes; the package notices once its 20-second cache of the status runs out
        // (Phase 5's events make it immediate).
        client.Clock.Advance(TimeSpan.FromSeconds(21));
        (await Call(client, HttpMethod.Get, token)).ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_endpoint_using_another_operations_enum_stops_the_app_from_starting()
    {
        var error = await Should.ThrowAsync<InvalidOperationException>(() => ClientApp.StartAsync(_auth, _billing.ClientSecret,
            app => app.MapGet("/wrong", [Auth.Client.Authorize(UnrelatedOperations.Something)] () => "x")));

        error.Message.ShouldContain(nameof(UnrelatedOperations));
    }

    [Fact]
    public async Task Registration_is_retried_until_AuthService_answers()
    {
        Backchannel? backchannel = null;
        await using var client = await ClientApp.StartAsync(_auth, _billing.ClientSecret,
            watchBackchannel: handler => backchannel = new Backchannel(handler, RegisterPath, failures: 2));

        // Each retry waits on the app's clock, so move it along while waiting.
        await Eventually(async db => (await db.Operations.CountAsync()) == 2, eachAttempt: () => client.Clock.Advance(TimeSpan.FromSeconds(5)));
        backchannel!.Calls(RegisterPath).ShouldBe(3);
    }

    [Fact]
    public async Task The_apps_token_for_AuthService_is_reused_and_renewed_shortly_before_it_expires()
    {
        Backchannel? backchannel = null;
        await using var client = await ClientApp.StartAsync(_auth, _billing.ClientSecret, watchBackchannel: handler => backchannel = new Backchannel(handler));
        await Eventually(async db => (await db.Operations.CountAsync()) == 2);
        var token = await SignInWithRole("billing", "billing.InvoiceRead");

        // Registration and the first request's session and role lookups: three calls, one token.
        (await Call(client, HttpMethod.Get, token)).ShouldBe(HttpStatusCode.OK);
        backchannel!.Calls(TokenPath).ShouldBe(1);

        // AuthService's tokens last 10 minutes; at 9.5 the app gets a new one before looking up the session again.
        client.Clock.Advance(TimeSpan.FromMinutes(9.5));
        (await Call(client, HttpMethod.Get, token)).ShouldBe(HttpStatusCode.OK);
        backchannel.Calls(TokenPath).ShouldBe(2);
    }

    [Fact]
    public async Task Outside_development_plain_http_to_AuthService_stops_the_app_before_it_sends_its_secret()
    {
        Backchannel? backchannel = null;

        var error = await Should.ThrowAsync<InvalidOperationException>(() => ClientApp.StartAsync(_auth, _billing.ClientSecret,
            watchBackchannel: handler => backchannel = new Backchannel(handler), environment: "Production"));

        error.Message.ShouldContain("AllowInsecureHttpDevOnly");
        backchannel!.Calls(TokenPath).ShouldBe(0);
    }

    // ----- Helpers -----

    private const string TokenPath = "/connect/token";
    private const string RegisterPath = "/kale.auth.v1.OperationRegistration/RegisterOperations";

    private async Task<ClientApp> RegisteredClient()
    {
        var client = await ClientApp.StartAsync(_auth, _billing.ClientSecret);
        await Eventually(async db => (await db.Operations.CountAsync(o => o.ApplicationKey == "billing")) == 2);
        return client;
    }

    // Signs a user in to the given application through AuthService's real flow; returns the access token.
    private async Task<string> SignInWithRole(string applicationKey, string? role) =>
        (await SignInTokens(applicationKey, role)).GetProperty("access_token").GetString()!;

    private Task<JsonElement> SignInTokens(string applicationKey, string? role) =>
        AuthServiceSteps.SignInAsync(_auth, applicationKey == "billing" ? _billing : _hr, role);

    private static async Task<HttpStatusCode> Call(ClientApp client, HttpMethod method, string? token)
    {
        using var request = new HttpRequestMessage(method, new Uri("/invoices", UriKind.Relative));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await client.Http.SendAsync(request);
        return response.StatusCode;
    }

    private Task WithAuthDb(Func<AuthDbContext, Task> action) => AuthServiceSteps.WithAuthDbAsync(_auth, action);

    private Task Eventually(Func<AuthDbContext, Task<bool>> condition, Action? eachAttempt = null) =>
        AuthServiceSteps.WaitUntilAsync(_auth, condition, eachAttempt);
}
