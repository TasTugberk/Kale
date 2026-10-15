using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApplicationClients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_applications_key",
                table: "applications");

            migrationBuilder.AddUniqueConstraint(
                name: "ak_applications_key",
                table: "applications",
                column: "key");

            migrationBuilder.AddForeignKey(
                name: "fk_openiddict_applications_application",
                table: "openiddict_applications",
                column: "client_id",
                principalTable: "applications",
                principalColumn: "key",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_openiddict_applications_application",
                table: "openiddict_applications");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_applications_key",
                table: "applications");

            migrationBuilder.CreateIndex(
                name: "ix_applications_key",
                table: "applications",
                column: "key",
                unique: true);
        }
    }
}
