using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OneOpenSessionPerUserAndApplication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_sessions_user_id",
                table: "sessions");

            // Existing rows first, or the unique index can't be built. Expired sessions that never ended end as of
            // their expiry, which is what sign-in now does too (the cleanup counts from that time either way).
            migrationBuilder.Sql("""
                UPDATE sessions
                SET ended_at = expires_at, modified_at = now()
                WHERE ended_at IS NULL AND expires_at <= now();
                """);

            // Then, should a user still have several open sessions in one application (sign-in's row lock was
            // the only guard), the newest stays and the others end now. No "session stopped" events (protobuf
            // payloads can't be built in SQL): client apps refuse those tokens once their 20-second session cache
            // runs out, and the user's earlier sign-in should have ended them anyway.
            migrationBuilder.Sql("""
                UPDATE sessions AS older
                SET ended_at = now(), modified_at = now()
                WHERE older.ended_at IS NULL
                  AND EXISTS (
                      SELECT 1 FROM sessions AS newer
                      WHERE newer.user_id = older.user_id
                        AND newer.application_id = older.application_id
                        AND newer.ended_at IS NULL
                        AND (newer.created_at, newer.id) > (older.created_at, older.id));
                """);

            migrationBuilder.CreateIndex(
                name: "ux_sessions_one_open_per_user_and_application",
                table: "sessions",
                columns: new[] { "user_id", "application_id" },
                unique: true,
                filter: "ended_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_sessions_one_open_per_user_and_application",
                table: "sessions");

            migrationBuilder.CreateIndex(
                name: "ix_sessions_user_id",
                table: "sessions",
                column: "user_id",
                filter: "ended_at IS NULL");
        }
    }
}
