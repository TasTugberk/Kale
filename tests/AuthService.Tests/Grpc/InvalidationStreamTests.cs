using System.Text.Json;
using Auth.Contracts.V1;
using AuthService.Admin;
using AuthService.Applications;
using AuthService.Domain;
using AuthService.Invalidation;
using AuthService.Outbox;
using AuthService.Persistence;
using AuthService.Roles;
using AuthService.Sessions;
using AuthService.Tests.Admin;
using AuthService.Tests.Hosting;
using AuthService.Tests.Persistence;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using DomainSession = AuthService.Domain.Session;

namespace AuthService.Tests.Grpc;

/// <summary>
/// InvalidationStream.Subscribe: a client app's events, from any AuthService instance's save to every stream of
/// that application, through PostgreSQL's NOTIFY.
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class InvalidationStreamTests(PostgresFixture postgres) : IAsyncLifetime
{
    // Generous: other test runs share this machine.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private AuthServiceApp _app = null!;
    private HttpClient _http = null!;
    private GrpcChannel _channel = null!;
    private CreatedApplication _billing = null!;
    private CreatedApplication _hr = null!;
    private readonly List<AsyncServerStreamingCall<InvalidationEvent>> _calls = [];

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
        foreach (var call in _calls)
        {
            call.Dispose();
        }

        _channel.Dispose();
        _http.Dispose();
        await _app.DisposeAsync();
    }

    // ----- Which events reach which stream -----

    [Fact]
    public async Task An_event_saved_by_any_instance_reaches_the_streams_of_its_application_only()
    {
        var billing = await SubscribeAsync(_billing);
        var hr = await SubscribeAsync(_hr);

        var billingEvent = await SaveAsync(OutboxEvents.SessionStopped(Guid.CreateVersion7(), _billing.Application.Id));
        (await NextEventAsync(billing)).EventId.ShouldBe(billingEvent);

        // Events arrive in the order they were saved: if hr's stream had been given billing's event, it would
        // come first.
        var hrEvent = await SaveAsync(OutboxEvents.RoleEdited(Guid.CreateVersion7(), _hr.Application.Id));
        (await NextEventAsync(hr)).EventId.ShouldBe(hrEvent);
    }

    [Fact]
    public async Task Every_stream_of_the_application_gets_the_event()
    {
        var first = await SubscribeAsync(_billing);
        var second = await SubscribeAsync(_billing);

        var saved = await SaveAsync(OutboxEvents.SessionStopped(Guid.CreateVersion7(), _billing.Application.Id));

        (await NextEventAsync(first)).EventId.ShouldBe(saved);
        (await NextEventAsync(second)).EventId.ShouldBe(saved);
    }

    [Fact]
    public async Task Nothing_is_sent_for_a_change_that_was_rolled_back()
    {
        var billing = await SubscribeAsync(_billing);
        await using (var db = OtherInstanceDb())
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            db.OutboxMessages.Add(OutboxEvents.SessionStopped(Guid.CreateVersion7(), _billing.Application.Id));
            await db.SaveChangesAsync();
            await transaction.RollbackAsync();
        }

        var saved = await SaveAsync(OutboxEvents.SessionStopped(Guid.CreateVersion7(), _billing.Application.Id));

        (await NextEventAsync(billing)).EventId.ShouldBe(saved);
    }

    [Fact]
    public async Task A_disabled_user_event_reaches_no_application()
    {
        // Its sessions' own SessionStopped events do the work; it stays in the outbox for audit only.
        var billing = await SubscribeAsync(_billing);
        await SaveAsync(OutboxEvents.UserDisabled(Guid.CreateVersion7()));

        var saved = await SaveAsync(OutboxEvents.SessionStopped(Guid.CreateVersion7(), _billing.Application.Id));

        (await NextEventAsync(billing)).EventId.ShouldBe(saved);
    }

    [Fact]
    public async Task A_stream_does_not_get_events_saved_before_it_connected()
    {
        var early = await SubscribeAsync(_billing);
        var before = await SaveAsync(OutboxEvents.SessionStopped(Guid.CreateVersion7(), _billing.Application.Id));
        (await NextEventAsync(early)).EventId.ShouldBe(before);

        var late = await SubscribeAsync(_billing);
        var after = await SaveAsync(OutboxEvents.SessionStopped(Guid.CreateVersion7(), _billing.Application.Id));

        (await NextEventAsync(late)).EventId.ShouldBe(after);
    }

    [Fact]
    public async Task Stopping_a_session_sends_its_event_to_the_sessions_application()
    {
        var billing = await SubscribeAsync(_billing);
        var session = await AddSessionAsync(_billing.Application);

        await using (var scope = _app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SessionStopper>().StopAsync(session.Id);
        }

        (await NextEventAsync(billing)).SessionStopped.SessionId.ShouldBe(session.Id.ToString());
    }

    [Fact]
    public async Task Editing_a_roles_operations_sends_its_event_to_the_roles_application()
    {
        var billing = await SubscribeAsync(_billing);
        Role role;
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<RoleEditor>();
            role = await roles.CreateAsync(_billing.Application.Id, "Clerk", description: null);
            await roles.SetOperationsAsync(role.Id, []);
        }

        (await NextEventAsync(billing)).RoleEdited.RoleId.ShouldBe(role.Id.ToString());
    }

    // ----- When a stream ends -----

    [Fact]
    public async Task Losing_the_listening_connection_ends_open_streams_and_the_instance_listens_again()
    {
        var billing = await SubscribeAsync(_billing);
        var hr = await SubscribeAsync(_hr);

        // E.g. a database restart or failover. Events saved until the instance listens again never reach it.
        await TerminateListenerConnectionAsync();

        (await StreamEndAsync(billing)).StatusCode.ShouldBe(StatusCode.Unavailable);
        (await StreamEndAsync(hr)).StatusCode.ShouldBe(StatusCode.Unavailable);

        // The client reconnects (SubscribeAsync retries while the instance isn't listening yet).
        var again = await SubscribeAsync(_billing);
        var saved = await SaveAsync(OutboxEvents.SessionStopped(Guid.CreateVersion7(), _billing.Application.Id));
        (await NextEventAsync(again)).EventId.ShouldBe(saved);
    }

    [Fact]
    public async Task Shutting_down_ends_open_streams_right_away()
    {
        var billing = await SubscribeAsync(_billing);

        // What the client sees. The order that matters under Kestrel (ApplicationStopping fires before the server
        // drains its requests, the listener's own stop only after) can't be shown with the test server, which stops
        // everything at once: InvalidationNotificationListenerTests covers it.
        _app.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

        (await StreamEndAsync(billing, TimeSpan.FromSeconds(5))).StatusCode.ShouldBe(StatusCode.Unavailable);
    }

    [Fact]
    public async Task An_unreadable_notification_is_skipped_and_the_streams_stay_open()
    {
        var billing = await SubscribeAsync(_billing);
        await using (var db = OtherInstanceDb())
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_notify({InvalidationNotificationListener.Channel}, 'not json')");
        }

        var saved = await SaveAsync(OutboxEvents.SessionStopped(Guid.CreateVersion7(), _billing.Application.Id));

        (await NextEventAsync(billing)).EventId.ShouldBe(saved);
    }

    [Fact]
    public async Task A_stream_that_cannot_reach_the_database_ends_as_unavailable()
    {
        var billing = await SubscribeAsync(_billing);

        // The stream checks its application before sending: make that check fail. New connections are refused, the
        // listener keeps its open one, and the event is saved on a connection opened before.
        await using var db = OtherInstanceDb();
        await db.Database.OpenConnectionAsync();
        await AllowConnectionsAsync(db.Database.GetDbConnection().Database, allow: false);
        try
        {
            db.OutboxMessages.Add(OutboxEvents.SessionStopped(Guid.CreateVersion7(), _billing.Application.Id));
            await db.SaveChangesAsync();

            // UNAVAILABLE, not UNKNOWN: the client reconnects later, as for any other loss of events.
            (await StreamEndAsync(billing)).StatusCode.ShouldBe(StatusCode.Unavailable);
        }
        finally
        {
            await AllowConnectionsAsync(db.Database.GetDbConnection().Database, allow: true);
        }
    }

    [Fact]
    public async Task A_stream_of_an_application_deactivated_later_ends_at_its_next_event()
    {
        var billing = await SubscribeAsync(_billing);
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationService>().SetActiveAsync(_billing.Application.Id, isActive: false);
        }

        await SaveAsync(OutboxEvents.SessionStopped(Guid.CreateVersion7(), _billing.Application.Id));

        (await StreamEndAsync(billing)).StatusCode.ShouldBe(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task An_instance_that_is_not_listening_for_events_refuses_streams()
    {
        // As right after startup: another instance (same database, so the same keys and applications) running
        // without its listener, which would never pass an event on.
        await using var notListening = new AuthServiceApp(_app.ConnectionString)
        {
            ConfigureServices = services =>
                services.Remove(services.Single(s => s.ImplementationType == typeof(InvalidationNotificationListener))),
        };
        using var channel = GrpcChannel.ForAddress(
            notListening.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = notListening.Server.CreateHandler() });

        using var call = new InvalidationStream.InvalidationStreamClient(channel)
            .Subscribe(new SubscribeRequest(), await AuthorizationFor(_billing));

        (await StreamEndAsync(call)).StatusCode.ShouldBe(StatusCode.Unavailable);
    }

    // ----- Who may subscribe -----

    [Fact]
    public async Task A_call_without_a_token_is_unauthenticated()
    {
        using var call = new InvalidationStream.InvalidationStreamClient(_channel).Subscribe(new SubscribeRequest());

        (await StreamEndAsync(call)).StatusCode.ShouldBe(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task A_users_token_is_refused()
    {
        // An admin's token for "auth" passes AuthService's token validation (it's for the admin API), so this is
        // the user token that gets furthest.
        var adminUiSecret = await AdminSteps.BootstrapAsync(_app);
        var userToken = await AdminSteps.SignInAsync(_app, "ayse", AuthApplication.Key, adminUiSecret);

        using var call = new InvalidationStream.InvalidationStreamClient(_channel)
            .Subscribe(new SubscribeRequest(), new Metadata { { "Authorization", $"Bearer {userToken}" } });

        (await StreamEndAsync(call)).StatusCode.ShouldBe(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task An_inactive_application_is_refused()
    {
        var headers = await AuthorizationFor(_billing);
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationService>().SetActiveAsync(_billing.Application.Id, isActive: false);
        }

        using var call = new InvalidationStream.InvalidationStreamClient(_channel).Subscribe(new SubscribeRequest(), headers);

        (await StreamEndAsync(call)).StatusCode.ShouldBe(StatusCode.PermissionDenied);
    }

    // ----- Helpers -----

    /// <summary>
    /// Connects like a client app: the response headers mean "subscribed". Until the instance listens for events
    /// (right after startup, or after it lost its connection) it refuses with UNAVAILABLE, and a client tries again
    /// later; here the test waits for the instance instead, so every call it makes is a real subscription.
    /// </summary>
    private async Task<AsyncServerStreamingCall<InvalidationEvent>> SubscribeAsync(CreatedApplication application)
    {
        var subscribers = _app.Services.GetRequiredService<InvalidationSubscribers>();
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (!subscribers.IsListening)
        {
            DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, "the instance never started listening");
            await Task.Delay(50);
        }

        var call = new InvalidationStream.InvalidationStreamClient(_channel).Subscribe(new SubscribeRequest(), await AuthorizationFor(application));
        _calls.Add(call);
        await call.ResponseHeadersAsync.WaitAsync(Timeout);
        return call;
    }

    private static async Task<InvalidationEvent> NextEventAsync(AsyncServerStreamingCall<InvalidationEvent> call)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        (await call.ResponseStream.MoveNext(timeout.Token)).ShouldBeTrue();
        return call.ResponseStream.Current;
    }

    private static async Task<RpcException> StreamEndAsync(AsyncServerStreamingCall<InvalidationEvent> call, TimeSpan? within = null)
    {
        using var timeout = new CancellationTokenSource(within ?? Timeout);
        return await Should.ThrowAsync<RpcException>(() => call.ResponseStream.MoveNext(timeout.Token));
    }

    /// <summary>Saves the event as another AuthService instance would (its own context and connection).</summary>
    private async Task<string> SaveAsync(OutboxMessage message)
    {
        await using var db = OtherInstanceDb();
        db.OutboxMessages.Add(message);
        await db.SaveChangesAsync();
        return message.Id.ToString();
    }

    private AuthDbContext OtherInstanceDb() =>
        postgres.CreateDbContext(new NpgsqlConnectionStringBuilder(_app.ConnectionString).Database!);

    /// <summary>
    /// Opens or closes the test database to new connections. PostgreSQL refuses this from inside the database
    /// itself, so it runs from "postgres"; ALTER DATABASE takes no parameters, so the name goes through a setting
    /// and PostgreSQL quotes it (%I).
    /// </summary>
    private async Task AllowConnectionsAsync(string database, bool allow)
    {
        await using var admin = postgres.CreateDbContext("postgres");
        await admin.Database.OpenConnectionAsync();
        await admin.Database.ExecuteSqlAsync($"SELECT set_config('kale_test.database', {database}, false)");
        await admin.Database.ExecuteSqlAsync($"SELECT set_config('kale_test.allow', {(allow ? "true" : "false")}, false)");
        await admin.Database.ExecuteSqlAsync($"""
            DO $$ BEGIN
                EXECUTE format('ALTER DATABASE %I WITH ALLOW_CONNECTIONS %s', current_setting('kale_test.database'), current_setting('kale_test.allow'));
            END $$
            """);
    }

    private async Task TerminateListenerConnectionAsync()
    {
        await using var db = OtherInstanceDb();
        var terminated = await db.Database.SqlQuery<bool>($"""
            SELECT pg_terminate_backend(pid) AS "Value" FROM pg_stat_activity
            WHERE datname = current_database() AND application_name = {InvalidationNotificationListener.ConnectionName}
            """).ToListAsync();
        terminated.ShouldBe([true]);
    }

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

    private async Task<DomainSession> AddSessionAsync(Application application)
    {
        await using var scope = _app.Services.CreateAsyncScope();
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
        return session;
    }
}
