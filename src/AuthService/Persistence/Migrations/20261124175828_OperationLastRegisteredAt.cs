using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OperationLastRegisteredAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing operations count as listed at the time of the upgrade, so they get the full grace period
            // (Operations:ObsoleteAfter) instead of becoming obsolete at the first registration that leaves them out.
            // The default only fills the existing rows; AuthService always sets the value itself.
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_registered_at",
                table: "operations",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "last_registered_at",
                table: "operations",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldDefaultValueSql: "now()");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_registered_at",
                table: "operations");
        }
    }
}
