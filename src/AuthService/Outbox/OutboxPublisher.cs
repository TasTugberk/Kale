using Auth.Contracts;
using AuthService.Persistence;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;

namespace AuthService.Outbox;

/// <summary>
/// Sends queued invalidation events to RabbitMQ and deletes each batch once RabbitMQ has confirmed it. If
/// anything fails midway, the transaction rolls back and the events are sent again later: at least once,
/// never lost.
/// </summary>
public sealed class OutboxPublisher(AuthDbContext db)
{
    // Small batches keep the transaction, and its row locks, short.
    public const int BatchSize = 100;

    /// <summary>Publishes up to <see cref="BatchSize"/> events, oldest first, and returns how many.</summary>
    public async Task<int> PublishPendingAsync(IChannel channel, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // SKIP LOCKED: when several AuthService instances publish at once, each takes rows the others haven't
        // locked, so an event normally goes out once and nobody waits for another instance's batch.
        // Raw SQL because LINQ has no row locks, and the lock must apply to the LIMITed rows: LINQ added on top
        // (OrderBy/Take) would wrap this in a subquery and lock every row first. {BatchSize} is sent as a parameter.
        var messages = await db.OutboxMessages
            .FromSql($"SELECT * FROM outbox_messages ORDER BY id LIMIT {BatchSize} FOR UPDATE SKIP LOCKED")
            .ToListAsync(cancellationToken);

        foreach (var message in messages)
        {
            var properties = new BasicProperties
            {
                MessageId = message.Id.ToString(),
                Type = message.Type,
                ContentType = "application/x-protobuf",
            };

            // The channel has publisher confirms on: this returns once RabbitMQ has the message, and throws if
            // RabbitMQ refused it.
            await channel.BasicPublishAsync(InvalidationExchange.Name, routingKey: "", mandatory: false, properties, message.Payload, cancellationToken);
        }

        db.OutboxMessages.RemoveRange(messages);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return messages.Count;
    }
}
