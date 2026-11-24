using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApplicationOldSecrets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "old_secret_hash",
                table: "applications",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "old_secret_valid_until",
                table: "applications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_applications_old_secret",
                table: "applications",
                sql: "(old_secret_hash IS NULL) = (old_secret_valid_until IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_applications_old_secret",
                table: "applications");

            migrationBuilder.DropColumn(
                name: "old_secret_hash",
                table: "applications");

            migrationBuilder.DropColumn(
                name: "old_secret_valid_until",
                table: "applications");
        }
    }
}
