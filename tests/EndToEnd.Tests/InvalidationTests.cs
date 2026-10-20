using System.Net;
using System.Net.Http.Headers;
using AuthService.Applications;
using AuthService.Persistence;
using AuthService.Roles;
using AuthService.Sessions;
using AuthService.Tests.Hosting;
using AuthService.Tests.Outbox;
using AuthService.Tests.Persistence;
using AuthService.Tests.SignIn;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;

namespace EndToEnd.Tests;

/// <summary>
/// DESIGN.md: "Admins must be able to stop a session even while the token is valid. Editing a role must take
/// effect on active sessions immediately." AuthService publishes the change through RabbitMQ and the client app
/// evicts its cache, so neither waits for the client's cache times (its clock never moves here).
/// </summary>
[Collection(UsesContainers.Name)]
public sealed class InvalidationTests(PostgresFixture postgres, RabbitMqFixture rabbitMq) : IAsyncLifetime
{
    private AuthServiceApp _auth = null!;
    private CreatedApplication _billing = null!;
    private ClientApp _client = null!;

    public async Task InitializeAsync()
    {
        _auth = await AuthServiceApp.CreateAsync(postgres, ("RabbitMq:Uri", rabbitMq.Uri.AbsoluteUri));
        await using (var scope = _auth.Services.CreateAsyncScope())
        {
            _billing = await scope.ServiceProvider.GetRequiredService<ApplicationService>()
                .CreateAsync(new NewApplication("billing", "Billing", [Browser.RedirectUri("billing")], []));
        }

        // As deployed: the address without the password, which comes separately (from a secret store).
        _client = await ClientApp.StartAsync(_auth, _billing.ClientSecret,
            rabbitMqUri: rabbitMq.UriWithoutPassword, rabbitMqPassword: RabbitMqFixture.Password);
        await AuthServiceSteps.WaitUntilAsync(_auth, async db => await db.Operations.CountAsync() == 2);
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        await _auth.DisposeAsync();
    }

    [Fact]
    public async Task Editing_a_role_takes_effect_on_active_sessions_immediately()
    {
        var token = await SignInWith("billing.InvoiceRead");
        (await Call(HttpMethod.Post, token)).ShouldBe(HttpStatusCode.Forbidden);

        await using (var scope = _auth.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<RoleEditor>()
                .SetOperationsAsync(await RoleOf(token), ["billing.InvoiceRead", "billing.InvoiceManage"]);
        }

        await Eventually(async () => await Call(HttpMethod.Post, token) == HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_stopped_session_is_refused_immediately_while_its_token_is_still_valid()
    {
        var token = await SignInWith("billing.InvoiceRead");
        (await Call(HttpMethod.Get, token)).ShouldBe(HttpStatusCode.OK);

        await using (var scope = _auth.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SessionStopper>().StopAsync(SessionOf(token));
        }

        await Eventually(async () => await Call(HttpMethod.Get, token) == HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task After_losing_its_connection_the_client_reconnects_and_forgets_its_cache()
    {
        // E.g. a broker restart.
        await AfterAnInterruptionStaleEntriesAreForgotten(() => rabbitMq.CloseAllConnectionsAsync());
    }

    [Fact]
    public async Task After_losing_its_queue_the_client_reconnects_and_forgets_its_cache()
    {
        // The connection stays open but the broker cancels the consumer (e.g. the queue was deleted).
        await AfterAnInterruptionStaleEntriesAreForgotten(() => rabbitMq.DeleteServerNamedQueuesAsync());
    }

    // Events sent while the client wasn't listening are lost, so after reconnecting it must not trust its cache.
    private async Task AfterAnInterruptionStaleEntriesAreForgotten(Func<Task> interrupt)
    {
        var token = await SignInWith("billing.InvoiceRead");
        (await Call(HttpMethod.Get, token)).ShouldBe(HttpStatusCode.OK);

        // Meanwhile the session ends. Ended directly, without an event, to stand for one the client missed.
        await interrupt();
        var sessionId = SessionOf(token);
        await AuthServiceSteps.WithAuthDbAsync(_auth, async db =>
            (await db.Sessions.SingleAsync(s => s.Id == sessionId)).End(_auth.Clock.GetUtcNow()));

        // The client reconnects within seconds; its cached "active" would otherwise last its full 20 s.
        await Eventually(async () => await Call(HttpMethod.Get, token) == HttpStatusCode.Forbidden, TimeSpan.FromSeconds(15));
    }

    private async Task<string> SignInWith(string operation) =>
        (await AuthServiceSteps.SignInAsync(_auth, _billing, operation)).GetProperty("access_token").GetString()!;

    private static Guid SessionOf(string token) => Guid.Parse(new JsonWebToken(token).GetClaim("sid").Value);

    private async Task<Guid> RoleOf(string token)
    {
        await using var scope = _auth.Services.CreateAsyncScope();
        var sessionId = SessionOf(token);
        return await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Sessions
            .Where(s => s.Id == sessionId).Select(s => s.RoleId).SingleAsync();
    }

    private async Task<HttpStatusCode> Call(HttpMethod method, string token)
    {
        using var request = new HttpRequestMessage(method, new Uri("/invoices", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.Http.SendAsync(request);
        return response.StatusCode;
    }

    // An event takes about a second (the outbox is polled every second); well under the cache times.
    private static async Task Eventually(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The change never reached the client app.");
    }
}
