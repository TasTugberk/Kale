using AuthService.Domain;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthService.Persistence.Configurations;

// The table also has a trigger that EF doesn't know about (raw SQL in the InvalidationNotify migration): every
// inserted row with an application calls pg_notify on the channel InvalidationNotificationListener.Channel, so each
// AuthService instance learns about the event and passes it to its client apps' streams. NOTIFY is sent only when
// the transaction commits, in commit order, so a rolled-back change announces nothing. The notification carries
// the event itself because the RabbitMQ publisher deletes the row within about a second.
internal sealed class OutboxMessageConfiguration : BaseEntityConfiguration<OutboxMessage>
{
    protected override void ConfigureEntity(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.Property(m => m.Type).HasMaxLength(50);

        // No foreign key to applications: a message is a record of something that happened, not a relationship,
        // and it's written in the same save as the change that already belongs to the application.
        builder.Property(m => m.ApplicationId);
    }
}
