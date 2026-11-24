using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ehr.Migrations
{
    /// <inheritdoc />
    public partial class InboxItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "inbox_items",
                schema: "ehr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_table = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: true),
                    source_id = table.Column<Guid>(type: "uuid", nullable: true),
                    summary = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    done_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbox_items", x => x.id);
                    table.CheckConstraint("ck_inbox_items_kind", "kind IN ('message', 'result', 'refill-request', 'emergency-access-review')");
                    table.CheckConstraint("ck_inbox_items_source", "(source_table IS NULL) = (source_id IS NULL)");
                    table.ForeignKey(
                        name: "fk_inbox_items_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "ehr",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_inbox_items_staff_users_recipient_id",
                        column: x => x.recipient_id,
                        principalSchema: "ehr",
                        principalTable: "staff_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_inbox_items_patient_id",
                schema: "ehr",
                table: "inbox_items",
                column: "patient_id");

            migrationBuilder.CreateIndex(
                name: "ix_inbox_items_recipient_id_created_at",
                schema: "ehr",
                table: "inbox_items",
                columns: new[] { "recipient_id", "created_at" },
                filter: "done_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_inbox_items_recipient_id_done_at",
                schema: "ehr",
                table: "inbox_items",
                columns: new[] { "recipient_id", "done_at" },
                filter: "done_at IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inbox_items",
                schema: "ehr");
        }
    }
}
