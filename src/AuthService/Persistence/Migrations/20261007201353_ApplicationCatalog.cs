using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApplicationCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "applications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_applications", x => x.id);
                    table.UniqueConstraint("ak_applications_id_key", x => new { x.id, x.key });
                    table.CheckConstraint("ck_applications_key_format", "key ~ '^[a-z][a-z0-9-]{1,62}$'");
                });

            migrationBuilder.CreateTable(
                name: "operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_key = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    obsoleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_operations", x => x.id);
                    table.UniqueConstraint("ak_operations_application_id_id", x => new { x.application_id, x.id });
                    table.CheckConstraint("ck_operations_name_format", "name ~ '^[a-z][a-z0-9-]*\\.[A-Za-z_][A-Za-z0-9_]*$'");
                    table.CheckConstraint("ck_operations_name_has_application_prefix", "starts_with(name, application_key || '.')");
                    table.ForeignKey(
                        name: "fk_operations_application_id_key",
                        columns: x => new { x.application_id, x.application_key },
                        principalTable: "applications",
                        principalColumns: new[] { "id", "key" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_roles", x => x.id);
                    table.UniqueConstraint("ak_roles_application_id_id", x => new { x.application_id, x.id });
                    table.ForeignKey(
                        name: "fk_roles_applications_application_id",
                        column: x => x.application_id,
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "operation_implications",
                columns: table => new
                {
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    implied_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_operation_implications", x => new { x.operation_id, x.implied_operation_id });
                    table.CheckConstraint("ck_operation_implications_not_self", "operation_id <> implied_operation_id");
                    table.ForeignKey(
                        name: "fk_operation_implications_implied_same_application",
                        columns: x => new { x.application_id, x.implied_operation_id },
                        principalTable: "operations",
                        principalColumns: new[] { "application_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_operation_implications_operation_same_application",
                        columns: x => new { x.application_id, x.operation_id },
                        principalTable: "operations",
                        principalColumns: new[] { "application_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "role_operations",
                columns: table => new
                {
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_role_operations", x => new { x.role_id, x.operation_id });
                    table.ForeignKey(
                        name: "fk_role_operations_operation_same_application",
                        columns: x => new { x.application_id, x.operation_id },
                        principalTable: "operations",
                        principalColumns: new[] { "application_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_role_operations_role_same_application",
                        columns: x => new { x.application_id, x.role_id },
                        principalTable: "roles",
                        principalColumns: new[] { "application_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_applications_key",
                table: "applications",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_operation_implications_application_id_implied_operation_id",
                table: "operation_implications",
                columns: new[] { "application_id", "implied_operation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_operation_implications_application_id_operation_id",
                table: "operation_implications",
                columns: new[] { "application_id", "operation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_operations_application_id_application_key",
                table: "operations",
                columns: new[] { "application_id", "application_key" });

            migrationBuilder.CreateIndex(
                name: "ix_operations_application_id_name",
                table: "operations",
                columns: new[] { "application_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_role_operations_application_id_operation_id",
                table: "role_operations",
                columns: new[] { "application_id", "operation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_role_operations_application_id_role_id",
                table: "role_operations",
                columns: new[] { "application_id", "role_id" });

            migrationBuilder.CreateIndex(
                name: "ix_roles_application_id_name",
                table: "roles",
                columns: new[] { "application_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "operation_implications");

            migrationBuilder.DropTable(
                name: "role_operations");

            migrationBuilder.DropTable(
                name: "operations");

            migrationBuilder.DropTable(
                name: "roles");

            migrationBuilder.DropTable(
                name: "applications");
        }
    }
}
