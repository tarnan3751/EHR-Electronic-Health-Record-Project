using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ehr.Migrations
{
    /// <inheritdoc />
    public partial class ProxyOnlyAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "patient_id",
                schema: "ehr",
                table: "patient_users",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "display_name",
                schema: "ehr",
                table: "patient_users",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "proxy_invitations",
                schema: "ehr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    grant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    code_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    redeemed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_proxy_invitations", x => x.id);
                    table.ForeignKey(
                        name: "fk_proxy_invitations_proxy_grants_grant_id",
                        column: x => x.grant_id,
                        principalSchema: "ehr",
                        principalTable: "proxy_grants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_proxy_invitations_code_hash",
                schema: "ehr",
                table: "proxy_invitations",
                column: "code_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_proxy_invitations_grant_id",
                schema: "ehr",
                table: "proxy_invitations",
                column: "grant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "proxy_invitations",
                schema: "ehr");

            migrationBuilder.DropColumn(
                name: "display_name",
                schema: "ehr",
                table: "patient_users");

            migrationBuilder.AlterColumn<Guid>(
                name: "patient_id",
                schema: "ehr",
                table: "patient_users",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
