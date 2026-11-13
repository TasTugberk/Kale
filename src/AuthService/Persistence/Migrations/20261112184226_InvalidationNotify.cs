using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InvalidationNotify : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "application_id",
                table: "outbox_messages",
                type: "uuid",
                nullable: true);

            // Hands each event to every AuthService instance (InvalidationNotificationListener, channel
            // "kale_invalidation"), which passes it to the streams of that application. NOTIFY is delivered on commit,
            // in commit order, and never for a rollback. The payload carries the event itself (hex: no line breaks,
            // unlike PostgreSQL's base64), since the row may already be deleted when a listener gets it. An event is a
            // few dozen bytes, far below NOTIFY's 8000-byte limit. Rows without an application (UserDisabled) concern
            // no stream.
            migrationBuilder.Sql("""
                CREATE FUNCTION notify_invalidation_event() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    PERFORM pg_notify('kale_invalidation', json_build_object(
                        'application_id', NEW.application_id,
                        'event', encode(NEW.payload, 'hex'))::text);
                    RETURN NULL;
                END;
                $$;

                CREATE TRIGGER trg_outbox_messages_notify
                AFTER INSERT ON outbox_messages
                FOR EACH ROW WHEN (NEW.application_id IS NOT NULL)
                EXECUTE FUNCTION notify_invalidation_event();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER trg_outbox_messages_notify ON outbox_messages;
                DROP FUNCTION notify_invalidation_event();
                """);

            migrationBuilder.DropColumn(
                name: "application_id",
                table: "outbox_messages");
        }
    }
}
