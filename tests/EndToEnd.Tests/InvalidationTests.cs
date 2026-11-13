using System.Net;
using System.Net.Http.Headers;
using AuthService.Applications;
using AuthService.Persistence;
using AuthService.Roles;
using AuthService.Sessions;
using AuthService.Tests.Hosting;
using Auth.Client;
using AuthService.Tests.Persistence;
using AuthService.Tests.SignIn;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;

namespace EndToEnd.Tests;

/// <summary>
/// DESIGN.md: "Admins must be able to stop a session even while the token is valid. Editing a role must take
/// effect on active sessions immediately." AuthService sends the change over the client app's invalidation stream
/// (gRPC) and the client app evicts its cache, so neither waits for the client's cache times (its clock never
/// moves here).
/// </summary>
[Collection(UsesContainers.Name)]
public sealed class InvalidationTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AuthServiceApp _auth = null!;
    private CreatedApplication _billing = null!;
    private ClientApp _client = null!;

    public async Task InitializeAsync()
    {
        _auth = await AuthServiceApp.CreateAsync(postgres);
        await using (var scope = _auth.Services.CreateAsyncScope())
        {
            _billing = await scope.ServiceProvider.GetRequiredService<ApplicationService>()
                .CreateAsync(new NewApplication("billing", "Billing", [Browser.RedirectUri("billing")], []));
        }

        _client = await ClientApp.StartAsync(_auth, _billing.ClientSecret);
        await AuthServiceSteps.WaitUntilAsync(_auth, async db => await db.Operations.CountAsync() == 2);
        await WaitUntilReceivingEventsAsync();
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
    public async Task Signing_in_again_ends_the_earlier_session_and_its_token_is_refused_right_away()
    {
        var first = await SignInWith("billing.InvoiceRead");
        (await Call(HttpMethod.Get, first)).ShouldBe(HttpStatusCode.OK, "and the client app now has the session in its cache");

        var second = (await AuthServiceSteps.SignInAgainAsync(_auth, _billing, await UserNameOf(first)))
            .GetProperty("access_token").GetString()!;

        await Eventually(async () => await Call(HttpMethod.Get, first) == HttpStatusCode.Forbidden);
        (await Call(HttpMethod.Get, second)).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task After_its_stream_ends_the_client_reconnects_and_forgets_its_cache()
    {
        // Events sent while the client wasn't connected are lost, so after reconnecting it must not trust its cache.
        var token = await SignInWith("billing.InvoiceRead");
        (await Call(HttpMethod.Get, token)).ShouldBe(HttpStatusCode.OK);

        // AuthService loses its own feed of events (e.g. a database failover), which ends every stream on it.
        // Meanwhile the session ends, directly, without an event, to stand for one the client missed.
        await AuthServiceSteps.WithAuthDbAsync(_auth, async db => await db.Database.ExecuteSqlAsync($"""
            SELECT pg_terminate_backend(pid) FROM pg_stat_activity
            WHERE datname = current_database() AND application_name = {AuthService.Invalidation.InvalidationNotificationListener.ConnectionName}
            """));
        var sessionId = SessionOf(token);
        await AuthServiceSteps.WithAuthDbAsync(_auth, async db =>
            (await db.Sessions.SingleAsync(s => s.Id == sessionId)).End(_auth.Clock.GetUtcNow()));

        // AuthService listens again and the client reconnects, each within seconds; the client's clock never moves,
        // so only clearing its cache on reconnecting can make this change.
        await Eventually(async () => await Call(HttpMethod.Get, token) == HttpStatusCode.Forbidden, TimeSpan.FromSeconds(30));
    }

    private async Task WaitUntilReceivingEventsAsync()
    {
        var access = _client.Services.GetRequiredService<SessionAccess>();
        await Eventually(() => Task.FromResult(access.ReceivingEvents));
    }

    private async Task<string> SignInWith(string operation) =>
        (await AuthServiceSteps.SignInAsync(_auth, _billing, operation)).GetProperty("access_token").GetString()!;

    private static Guid SessionOf(string token) => Guid.Parse(new JsonWebToken(token).GetClaim("sid").Value);

    private async Task<string> UserNameOf(string token)
    {
        var userId = Guid.Parse(new JsonWebToken(token).Subject);
        await using var scope = _auth.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Users
            .Where(u => u.Id == userId).Select(u => u.UserName!).SingleAsync();
    }

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
