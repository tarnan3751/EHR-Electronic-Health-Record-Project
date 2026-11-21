using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ehr.Migrations
{
    /// <inheritdoc />
    public partial class RefillRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "refill_requests",
                schema: "ehr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    medication_statement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    medication_display = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ordered_display = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    decline_message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sensitive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refill_requests", x => x.id);
                    table.CheckConstraint("ck_refill_requests_answer", "(status = 'requested') = (decided_at IS NULL) AND (decided_at IS NULL) = (decided_by IS NULL) AND (status = 'completed') = (order_id IS NOT NULL) AND (order_id IS NULL) = (ordered_display IS NULL) AND (status = 'rejected') = (decline_message IS NOT NULL)");
                    table.CheckConstraint("ck_refill_requests_status", "status IN ('requested', 'completed', 'rejected')");
                    table.ForeignKey(
                        name: "fk_refill_requests_medication_statements_medication_statement_",
                        column: x => x.medication_statement_id,
                        principalSchema: "ehr",
                        principalTable: "medication_statements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refill_requests_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "ehr",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refill_requests_patient_users_requested_by",
                        column: x => x.requested_by,
                        principalSchema: "ehr",
                        principalTable: "patient_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refill_requests_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "ehr",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refill_requests_staff_users_decided_by",
                        column: x => x.decided_by,
                        principalSchema: "ehr",
                        principalTable: "staff_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_refill_requests_decided_by",
                schema: "ehr",
                table: "refill_requests",
                column: "decided_by");

            migrationBuilder.CreateIndex(
                name: "ix_refill_requests_medication_statement_id",
                schema: "ehr",
                table: "refill_requests",
                column: "medication_statement_id");

            migrationBuilder.CreateIndex(
                name: "ix_refill_requests_order_id",
                schema: "ehr",
                table: "refill_requests",
                column: "order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refill_requests_patient_id_requested_at",
                schema: "ehr",
                table: "refill_requests",
                columns: new[] { "patient_id", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_refill_requests_requested_by",
                schema: "ehr",
                table: "refill_requests",
                column: "requested_by");

            migrationBuilder.CreateIndex(
                name: "ux_refill_requests_open",
                schema: "ehr",
                table: "refill_requests",
                column: "medication_statement_id",
                unique: true,
                filter: "status = 'requested'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Written by hand, in part. On a database with its policies applied, db/policies/77-refill-requests.sql
            // puts a trigger on the medications whose function changes this table: it goes first, so no change to a
            // medication's sensitivity is left naming a table that is gone. The table's own triggers go with it, and
            // their functions after it: nothing of this step is left for the older schema to carry. Its inbox items
            // stay: their kind was in the inbox's check before this step.
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS medication_statements_follow_refills ON ehr.medication_statements;
                DROP FUNCTION IF EXISTS ehr.follow_refill_medication();
                """);

            migrationBuilder.DropTable(
                name: "refill_requests",
                schema: "ehr");

            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS ehr.guard_refill_request();
                DROP FUNCTION IF EXISTS ehr.reject_refill_request_delete();
                DROP FUNCTION IF EXISTS ehr.deliver_refill_request();
                DROP FUNCTION IF EXISTS ehr.portal_refill_requests(integer);
                DROP FUNCTION IF EXISTS ehr.renews_same_medication(text, text);
                """);
        }
    }
}
