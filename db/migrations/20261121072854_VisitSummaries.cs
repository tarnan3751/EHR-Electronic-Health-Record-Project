using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ehr.Migrations
{
    /// <inheritdoc />
    public partial class VisitSummaries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_inbox_items_kind",
                schema: "ehr",
                table: "inbox_items");

            migrationBuilder.CreateTable(
                name: "visit_summaries",
                schema: "ehr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    talked_about = table.Column<string>(type: "text", nullable: false),
                    found = table.Column<string>(type: "text", nullable: false),
                    next_steps = table.Column<string>(type: "text", nullable: false),
                    note_signed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    released_by = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "NULLIF(current_setting('app.user_id', true), '')::uuid"),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    sensitive = table.Column<bool>(type: "boolean", nullable: false),
                    sensitive_inherited = table.Column<bool>(type: "boolean", nullable: false),
                    sensitive_marked_by = table.Column<Guid>(type: "uuid", nullable: true),
                    sensitive_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    sensitive_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    withdrawn_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    withdrawn_by = table.Column<Guid>(type: "uuid", nullable: true),
                    withdrawn_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_visit_summaries", x => x.id);
                    table.CheckConstraint("ck_visit_summaries_found", "found ~ '[^[:space:]]' AND char_length(found) <= 4000");
                    table.CheckConstraint("ck_visit_summaries_inherited", "sensitive OR NOT sensitive_inherited");
                    table.CheckConstraint("ck_visit_summaries_next_steps", "next_steps ~ '[^[:space:]]' AND char_length(next_steps) <= 4000");
                    table.CheckConstraint("ck_visit_summaries_talked_about", "talked_about ~ '[^[:space:]]' AND char_length(talked_about) <= 4000");
                    table.CheckConstraint("ck_visit_summaries_withdrawn", "(withdrawn_at IS NULL AND withdrawn_by IS NULL AND withdrawn_reason IS NULL) OR (withdrawn_at IS NOT NULL AND withdrawn_by IS NOT NULL AND withdrawn_reason ~ '[^[:space:]]')");
                    table.ForeignKey(
                        name: "fk_visit_summaries_clinical_notes_note_id_patient_id",
                        columns: x => new { x.note_id, x.patient_id },
                        principalSchema: "ehr",
                        principalTable: "clinical_notes",
                        principalColumns: new[] { "id", "patient_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_visit_summaries_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "ehr",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_visit_summaries_staff_users_released_by",
                        column: x => x.released_by,
                        principalSchema: "ehr",
                        principalTable: "staff_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_visit_summaries_staff_users_sensitive_marked_by",
                        column: x => x.sensitive_marked_by,
                        principalSchema: "ehr",
                        principalTable: "staff_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_visit_summaries_staff_users_withdrawn_by",
                        column: x => x.withdrawn_by,
                        principalSchema: "ehr",
                        principalTable: "staff_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_inbox_items_kind",
                schema: "ehr",
                table: "inbox_items",
                sql: "kind IN ('message', 'result', 'refill-request', 'emergency-access-review', 'addendum', 'summary-withdrawn')");

            migrationBuilder.CreateIndex(
                name: "ix_visit_summaries_note_id",
                schema: "ehr",
                table: "visit_summaries",
                column: "note_id",
                unique: true,
                filter: "withdrawn_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_visit_summaries_note_id_patient_id",
                schema: "ehr",
                table: "visit_summaries",
                columns: new[] { "note_id", "patient_id" });

            migrationBuilder.CreateIndex(
                name: "ix_visit_summaries_patient_id_note_signed_at",
                schema: "ehr",
                table: "visit_summaries",
                columns: new[] { "patient_id", "note_signed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_visit_summaries_released_by",
                schema: "ehr",
                table: "visit_summaries",
                column: "released_by");

            migrationBuilder.CreateIndex(
                name: "ix_visit_summaries_sensitive_marked_by",
                schema: "ehr",
                table: "visit_summaries",
                column: "sensitive_marked_by");

            migrationBuilder.CreateIndex(
                name: "ix_visit_summaries_withdrawn_by",
                schema: "ehr",
                table: "visit_summaries",
                column: "withdrawn_by");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Written by hand, in part. On a database with its policies applied, db/policies/52-visit-summaries.sql
            // puts triggers on the notes and the addenda whose function changes this table: they go first, so no
            // note's or addendum's sensitivity change is left naming a table that is gone. The table's own triggers go
            // with it, and their functions after it: nothing of this step is left for the older schema to carry.
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS clinical_notes_follow_summaries ON ehr.clinical_notes;
                DROP TRIGGER IF EXISTS note_addenda_follow_summaries ON ehr.note_addenda;
                DROP TRIGGER IF EXISTS note_addenda_signed_follow_summaries ON ehr.note_addenda;
                DROP FUNCTION IF EXISTS ehr.follow_summary_sources();
                """);

            migrationBuilder.DropTable(
                name: "visit_summaries",
                schema: "ehr");

            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS ehr.guard_visit_summary();
                DROP FUNCTION IF EXISTS ehr.reject_visit_summary_delete();
                DROP FUNCTION IF EXISTS ehr.deliver_summary_withdrawn();
                DROP FUNCTION IF EXISTS ehr.withdrawn_visit_summaries();
                DROP FUNCTION IF EXISTS ehr.withdrawn_summary_was_viewed(uuid);
                DROP FUNCTION IF EXISTS ehr.visit_summary_problems(uuid, uuid, text[]);
                DROP FUNCTION IF EXISTS ehr.summary_sources_sensitive(uuid, uuid);
                -- Inbox items of this step's kind, which the older schema's check refuses. Forced row-level security
                -- would hide them from this delete.
                DO $$
                DECLARE
                    forced boolean := (SELECT relforcerowsecurity FROM pg_class WHERE oid = 'ehr.inbox_items'::regclass);
                BEGIN
                    ALTER TABLE ehr.inbox_items NO FORCE ROW LEVEL SECURITY;
                    ALTER TABLE ehr.inbox_items DISABLE TRIGGER USER;
                    DELETE FROM ehr.inbox_items WHERE kind = 'summary-withdrawn';
                    ALTER TABLE ehr.inbox_items ENABLE TRIGGER USER;
                    IF forced THEN
                        ALTER TABLE ehr.inbox_items FORCE ROW LEVEL SECURITY;
                    END IF;
                END
                $$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_inbox_items_kind",
                schema: "ehr",
                table: "inbox_items");

            migrationBuilder.AddCheckConstraint(
                name: "ck_inbox_items_kind",
                schema: "ehr",
                table: "inbox_items",
                sql: "kind IN ('message', 'result', 'refill-request', 'emergency-access-review', 'addendum')");
        }
    }
}
