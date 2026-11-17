using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DesktopClients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_openiddict_applications_application",
                table: "openiddict_applications");

            migrationBuilder.AddColumn<string>(
                name: "application_key",
                table: "openiddict_applications",
                type: "character varying(63)",
                maxLength: 63,
                nullable: false,
                computedColumnSql: "split_part(client_id, '.', 1)",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "ix_openiddict_applications_application_key",
                table: "openiddict_applications",
                column: "application_key");

            migrationBuilder.AddCheckConstraint(
                name: "ck_openiddict_applications_client_id",
                table: "openiddict_applications",
                sql: "client_id = application_key OR client_id = application_key || '.desktop'");

            migrationBuilder.AddForeignKey(
                name: "fk_openiddict_applications_application",
                table: "openiddict_applications",
                column: "application_key",
                principalTable: "applications",
                principalColumn: "key",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Without desktop sign-in, a "{key}.desktop" client would break the old 1:1 foreign key: remove the
            // desktop clients first, with their tokens and authorizations (which reference them).
            migrationBuilder.Sql("""
                DELETE FROM openiddict_tokens WHERE application_id IN (SELECT id FROM openiddict_applications WHERE client_id <> application_key);
                DELETE FROM openiddict_authorizations WHERE application_id IN (SELECT id FROM openiddict_applications WHERE client_id <> application_key);
                DELETE FROM openiddict_applications WHERE client_id <> application_key;
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_openiddict_applications_application",
                table: "openiddict_applications");

            migrationBuilder.DropIndex(
                name: "ix_openiddict_applications_application_key",
                table: "openiddict_applications");

            migrationBuilder.DropCheckConstraint(
                name: "ck_openiddict_applications_client_id",
                table: "openiddict_applications");

            migrationBuilder.DropColumn(
                name: "application_key",
                table: "openiddict_applications");

            migrationBuilder.AddForeignKey(
                name: "fk_openiddict_applications_application",
                table: "openiddict_applications",
                column: "client_id",
                principalTable: "applications",
                principalColumn: "key",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
