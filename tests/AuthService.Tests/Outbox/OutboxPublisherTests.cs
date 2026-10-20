using Auth.Contracts.V1;
using AuthService.Outbox;
using AuthService.Persistence;
using AuthService.Tests.Hosting;
using AuthService.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace AuthService.Tests.Outbox;

/// <summary>
/// The outbox publisher sends queued invalidation events to RabbitMQ's fanout exchange and deletes each one
/// only after RabbitMQ confirmed it, so an event is sent at least once and never lost.
/// </summary>
[Collection(UsesPostgresAndRabbitMq.Name)]
public sealed class OutboxPublisherTests(PostgresFixture postgres, RabbitMqFixture rabbitMq) : IAsyncLifetime
{
    private AuthDbContext _db = null!;
    private IConnection _connection = null!;
    private IChannel _channel = null!;
    private string _listenerQueue = null!;

    public async Task InitializeAsync()
    {
        _db = postgres.CreateDbContext();
        await _db.Database.MigrateAsync();

        // Publishes with confirms, as the app does.
        _connection = await rabbitMq.ConnectAsync();
        _channel = await _connection.CreateChannelAsync(new CreateChannelOptions(
            publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true));
        await _channel.ExchangeDeclareAsync(OutboxPublisher.Exchange, ExchangeType.Fanout, durable: true);

        // Stands in for a client app instance: its own queue, bound to the exchange.
        _listenerQueue = (await _channel.QueueDeclareAsync()).QueueName;
        await _channel.QueueBindAsync(_listenerQueue, OutboxPublisher.Exchange, routingKey: "");
    }

    public async Task DisposeAsync()
    {
        await _channel.DisposeAsync();
        await _connection.DisposeAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task Queued_events_are_published_oldest_first_and_then_deleted()
    {
        var sessionIds = new[] { Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7() };
        var messages = sessionIds.Select(OutboxEvents.SessionStopped).OrderBy(m => m.Id).ToList();

        // Saved newest first, one by one, so the table's own row order is the opposite of the id order.
        foreach (var message in Enumerable.Reverse(messages))
        {
            _db.OutboxMessages.Add(message);
            await _db.SaveChangesAsync();
        }

        (await new OutboxPublisher(_db).PublishPendingAsync(_channel)).ShouldBe(3);

        // In id order: UUID v7, so oldest first (ids made within the same millisecond have no order among them).
        var received = await ReceiveAll();
        received.Select(m => m.BasicProperties.MessageId).ShouldBe(messages.Select(m => m.Id.ToString()));
        received.Select(m => InvalidationEvent.Parser.ParseFrom(m.Body.Span).SessionStopped.SessionId)
            .ShouldBe(sessionIds.Select(id => id.ToString()), ignoreOrder: true);
        (await _db.OutboxMessages.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Rows_another_instance_is_publishing_are_skipped_not_waited_for()
    {
        _db.OutboxMessages.Add(OutboxEvents.SessionStopped(Guid.CreateVersion7()));
        await _db.SaveChangesAsync();
        await using var otherInstance = postgres.CreateDbContext(_db.Database.GetDbConnection().Database);
        await using var transaction = await otherInstance.Database.BeginTransactionAsync();
        await otherInstance.Database.ExecuteSqlAsync($"SELECT 1 FROM outbox_messages FOR UPDATE");

        var published = await new OutboxPublisher(_db).PublishPendingAsync(_channel).WaitAsync(TimeSpan.FromSeconds(5));

        published.ShouldBe(0);
        (await ReceiveAll()).ShouldBeEmpty();
    }

    [Fact]
    public async Task When_RabbitMQ_refuses_an_event_it_stays_in_the_outbox()
    {
        _db.OutboxMessages.Add(OutboxEvents.SessionStopped(Guid.CreateVersion7()));
        await _db.SaveChangesAsync();

        // Publishing to an exchange that doesn't exist is refused by the broker.
        await _channel.ExchangeDeleteAsync(OutboxPublisher.Exchange);
        try
        {
            // The broker answers with 404 NOT_FOUND and closes the channel.
            await Should.ThrowAsync<AlreadyClosedException>(() => new OutboxPublisher(_db).PublishPendingAsync(_channel));
        }
        finally
        {
            await using var setup = await _connection.CreateChannelAsync();
            await setup.ExchangeDeclareAsync(OutboxPublisher.Exchange, ExchangeType.Fanout, durable: true);
        }

        _db.ChangeTracker.Clear();
        (await _db.OutboxMessages.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task The_running_app_publishes_an_event_soon_after_it_is_saved()
    {
        await using var app = await StartAppAsync();
        var sessionId = await QueueSessionStoppedAsync(app);

        var message = await ReceiveOne(TimeSpan.FromSeconds(10));

        InvalidationEvent.Parser.ParseFrom(message.Body.Span).SessionStopped.SessionId.ShouldBe(sessionId.ToString());
    }

    [Fact]
    public async Task After_losing_its_connection_the_app_reconnects_and_keeps_publishing()
    {
        await using var app = await StartAppAsync();
        await QueueSessionStoppedAsync(app);
        await ReceiveOne(TimeSpan.FromSeconds(10));

        // E.g. a broker restart. This drops the test's own connection too (and its queue), so open it again.
        await rabbitMq.CloseAllConnectionsAsync();
        await DisposeAsync();
        await InitializeAsync();
        var sessionId = await QueueSessionStoppedAsync(app);

        var message = await ReceiveOne(TimeSpan.FromSeconds(15));

        InvalidationEvent.Parser.ParseFrom(message.Body.Span).SessionStopped.SessionId.ShouldBe(sessionId.ToString());
    }

    // The app as deployed: the address without the password, which comes from a file (a mounted Podman secret).
    private async Task<AuthServiceApp> StartAppAsync()
    {
        var passwordFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(passwordFile, RabbitMqFixture.Password + "\n");
        var app = await AuthServiceApp.CreateAsync(postgres,
            ("RabbitMq:Uri", rabbitMq.UriWithoutPassword.AbsoluteUri), ("RabbitMq:PasswordFile", passwordFile));
        app.CreateClient().Dispose();

        // Read at startup.
        File.Delete(passwordFile);
        return app;
    }

    private static async Task<Guid> QueueSessionStoppedAsync(AuthServiceApp app)
    {
        var sessionId = Guid.CreateVersion7();
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        db.OutboxMessages.Add(OutboxEvents.SessionStopped(sessionId));
        await db.SaveChangesAsync();
        return sessionId;
    }

    private async Task<List<BasicGetResult>> ReceiveAll()
    {
        var received = new List<BasicGetResult>();
        while (await _channel.BasicGetAsync(_listenerQueue, autoAck: true) is { } message)
        {
            received.Add(message);
        }

        return received;
    }

    private async Task<BasicGetResult> ReceiveOne(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await _channel.BasicGetAsync(_listenerQueue, autoAck: true) is { } message)
            {
                return message;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("No event arrived.");
    }
}
