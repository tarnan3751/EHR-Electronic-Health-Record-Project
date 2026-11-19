using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ehr.Migrations
{
    /// <inheritdoc />
    public partial class NoteSensitivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_inbox_items_kind",
                schema: "ehr",
                table: "inbox_items");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "sensitive_changed_at",
                schema: "ehr",
                table: "note_addenda",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "sensitive_marked_by",
                schema: "ehr",
                table: "note_addenda",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sensitive_reason",
                schema: "ehr",
                table: "note_addenda",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "sensitive_changed_at",
                schema: "ehr",
                table: "clinical_notes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "sensitive_marked_by",
                schema: "ehr",
                table: "clinical_notes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sensitive_reason",
                schema: "ehr",
                table: "clinical_notes",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_note_addenda_sensitive_marked_by",
                schema: "ehr",
                table: "note_addenda",
                column: "sensitive_marked_by");

            migrationBuilder.AddCheckConstraint(
                name: "ck_inbox_items_kind",
                schema: "ehr",
                table: "inbox_items",
                sql: "kind IN ('message', 'result', 'refill-request', 'emergency-access-review', 'addendum')");

            migrationBuilder.CreateIndex(
                name: "ix_clinical_notes_sensitive_marked_by",
                schema: "ehr",
                table: "clinical_notes",
                column: "sensitive_marked_by");

            migrationBuilder.AddForeignKey(
                name: "fk_clinical_notes_staff_users_sensitive_marked_by",
                schema: "ehr",
                table: "clinical_notes",
                column: "sensitive_marked_by",
                principalSchema: "ehr",
                principalTable: "staff_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_note_addenda_staff_users_sensitive_marked_by",
                schema: "ehr",
                table: "note_addenda",
                column: "sensitive_marked_by",
                principalSchema: "ehr",
                principalTable: "staff_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // Written by hand. A note signed as sensitive, or an addendum written as one, before this step has nobody
            // recorded as having made it so: its signer or author did. On a database in use, forced row-level security
            // hides the rows from this migration and the immutability triggers refuse every update, the owner's too:
            // both are lifted for these two statements and put back. Nothing a person wrote changes, and no audit
            // event is written, since the user triggers are off.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    forced_notes boolean := (SELECT relforcerowsecurity FROM pg_class WHERE oid = 'ehr.clinical_notes'::regclass);
                    forced_addenda boolean := (SELECT relforcerowsecurity FROM pg_class WHERE oid = 'ehr.note_addenda'::regclass);
                BEGIN
                    ALTER TABLE ehr.clinical_notes NO FORCE ROW LEVEL SECURITY;
                    ALTER TABLE ehr.note_addenda NO FORCE ROW LEVEL SECURITY;
                    ALTER TABLE ehr.clinical_notes DISABLE TRIGGER USER;
                    ALTER TABLE ehr.note_addenda DISABLE TRIGGER USER;

                    UPDATE ehr.clinical_notes SET sensitive_marked_by = signer_id WHERE sensitive AND sensitive_marked_by IS NULL;
                    UPDATE ehr.note_addenda SET sensitive_marked_by = author_id WHERE sensitive AND sensitive_marked_by IS NULL;

                    ALTER TABLE ehr.clinical_notes ENABLE TRIGGER USER;
                    ALTER TABLE ehr.note_addenda ENABLE TRIGGER USER;
                    IF forced_notes THEN
                        ALTER TABLE ehr.clinical_notes FORCE ROW LEVEL SECURITY;
                    END IF;
                    IF forced_addenda THEN
                        ALTER TABLE ehr.note_addenda FORCE ROW LEVEL SECURITY;
                    END IF;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Written by hand. On a database with its policies applied, three triggers of this step name the columns
            // this and the migration before it drop, and the notes' and addenda's immutability triggers cover deletes
            // only (the update rule is this step's). They are removed, and immutability is put back for updates too,
            // as it was before this step, so nothing is left updatable in between. db/policies re-creates them all
            // when it is next applied.
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS clinical_notes_mark_addenda ON ehr.clinical_notes;
                DROP TRIGGER IF EXISTS clinical_notes_log_sensitivity ON ehr.clinical_notes;
                DROP TRIGGER IF EXISTS clinical_notes_sensitivity ON ehr.clinical_notes;
                DROP TRIGGER IF EXISTS note_addenda_sensitivity ON ehr.note_addenda;
                DROP TRIGGER IF EXISTS note_addenda_deliver ON ehr.note_addenda;
                DO $$
                BEGIN
                    IF to_regprocedure('ehr.reject_clinical_note_change()') IS NOT NULL THEN
                        CREATE OR REPLACE TRIGGER clinical_notes_immutable BEFORE UPDATE OR DELETE ON ehr.clinical_notes
                            FOR EACH ROW EXECUTE FUNCTION ehr.reject_clinical_note_change();
                    END IF;
                    IF to_regprocedure('ehr.reject_note_addendum_change()') IS NOT NULL THEN
                        CREATE OR REPLACE TRIGGER note_addenda_immutable BEFORE UPDATE OR DELETE ON ehr.note_addenda
                            FOR EACH ROW EXECUTE FUNCTION ehr.reject_note_addendum_change();
                    END IF;
                END
                $$;
                DELETE FROM ehr.inbox_items WHERE kind = 'addendum';
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_clinical_notes_staff_users_sensitive_marked_by",
                schema: "ehr",
                table: "clinical_notes");

            migrationBuilder.DropForeignKey(
                name: "fk_note_addenda_staff_users_sensitive_marked_by",
                schema: "ehr",
                table: "note_addenda");

            migrationBuilder.DropIndex(
                name: "ix_note_addenda_sensitive_marked_by",
                schema: "ehr",
                table: "note_addenda");

            migrationBuilder.DropCheckConstraint(
                name: "ck_inbox_items_kind",
                schema: "ehr",
                table: "inbox_items");

            migrationBuilder.DropIndex(
                name: "ix_clinical_notes_sensitive_marked_by",
                schema: "ehr",
                table: "clinical_notes");

            migrationBuilder.DropColumn(
                name: "sensitive_changed_at",
                schema: "ehr",
                table: "note_addenda");

            migrationBuilder.DropColumn(
                name: "sensitive_marked_by",
                schema: "ehr",
                table: "note_addenda");

            migrationBuilder.DropColumn(
                name: "sensitive_reason",
                schema: "ehr",
                table: "note_addenda");

            migrationBuilder.DropColumn(
                name: "sensitive_changed_at",
                schema: "ehr",
                table: "clinical_notes");

            migrationBuilder.DropColumn(
                name: "sensitive_marked_by",
                schema: "ehr",
                table: "clinical_notes");

            migrationBuilder.DropColumn(
                name: "sensitive_reason",
                schema: "ehr",
                table: "clinical_notes");

            migrationBuilder.AddCheckConstraint(
                name: "ck_inbox_items_kind",
                schema: "ehr",
                table: "inbox_items",
                sql: "kind IN ('message', 'result', 'refill-request', 'emergency-access-review')");
        }
    }
}
