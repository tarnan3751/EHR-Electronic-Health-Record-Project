using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ehr.Migrations
{
    /// <inheritdoc />
    public partial class ProxyGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "proxy_grants",
                schema: "ehr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    proxy_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    scope = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    include_sensitive = table.Column<bool>(type: "boolean", nullable: false),
                    granted_by_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "patient"),
                    granted_by = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "NULLIF(current_setting('app.user_id', true), '')::uuid"),
                    granted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_proxy_grants", x => x.id);
                    table.CheckConstraint("ck_proxy_grants_granted_by_kind", "granted_by_kind IN ('patient', 'staff')");
                    table.CheckConstraint("ck_proxy_grants_scope", "scope IN ('record', 'record-and-messages')");
                    table.ForeignKey(
                        name: "fk_proxy_grants_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "ehr",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_proxy_grants_patient_id_proxy_email",
                schema: "ehr",
                table: "proxy_grants",
                columns: new[] { "patient_id", "proxy_email" },
                unique: true,
                filter: "revoked_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_proxy_grants_proxy_email",
                schema: "ehr",
                table: "proxy_grants",
                column: "proxy_email");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "proxy_grants",
                schema: "ehr");
        }
    }
}
