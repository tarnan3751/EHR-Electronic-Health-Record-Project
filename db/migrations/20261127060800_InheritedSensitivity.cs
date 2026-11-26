using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ehr.Migrations
{
    /// <inheritdoc />
    public partial class InheritedSensitivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "sensitive_inherited",
                schema: "ehr",
                table: "note_addenda",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddCheckConstraint(
                name: "ck_note_addenda_inherited",
                schema: "ehr",
                table: "note_addenda",
                sql: "sensitive OR NOT sensitive_inherited");

            // Written by hand. Addenda that are sensitive today because their note is, are marked as inherited: those
            // the note's marking marked (the same reason, by the same person), and those signed while their note was
            // already sensitive, whose form offered no mark of their own. Forced row-level security and the
            // immutability triggers are lifted for the one statement, as in NoteSensitivity; no text changes and no
            // audit event is written.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    forced_notes boolean := (SELECT relforcerowsecurity FROM pg_class WHERE oid = 'ehr.clinical_notes'::regclass);
                    forced_addenda boolean := (SELECT relforcerowsecurity FROM pg_class WHERE oid = 'ehr.note_addenda'::regclass);
                BEGIN
                    ALTER TABLE ehr.clinical_notes NO FORCE ROW LEVEL SECURITY;
                    ALTER TABLE ehr.note_addenda NO FORCE ROW LEVEL SECURITY;
                    ALTER TABLE ehr.note_addenda DISABLE TRIGGER USER;

                    UPDATE ehr.note_addenda a
                    SET sensitive_inherited = true, sensitive_marked_by = NULL
                    FROM ehr.clinical_notes n
                    WHERE n.id = a.note_id AND n.sensitive AND a.sensitive
                      AND ((a.sensitive_reason IS NOT NULL AND a.sensitive_reason IS NOT DISTINCT FROM n.sensitive_reason
                            AND a.sensitive_marked_by IS NOT DISTINCT FROM n.sensitive_marked_by
                            -- Marked by the note's marking, which stamps the addendum after the note in the same
                            -- statement: one marked on its own earlier, in the same words, keeps its own mark.
                            AND a.sensitive_changed_at >= n.sensitive_changed_at)
                           OR (a.sensitive_reason IS NULL AND a.signed_at >= coalesce(n.sensitive_changed_at, n.signed_at)));

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
            // Written by hand: the trigger and the policy of this step that name the column go first (db/policies makes
            // them again when it is next applied; the earlier release's makes the policy as it was). An inherited mark
            // has nobody of its own: under the earlier release's rule it is the note's marker's again (the author's,
            // if the note has none), so that the people who could unmark it then still can.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    forced_notes boolean := (SELECT relforcerowsecurity FROM pg_class WHERE oid = 'ehr.clinical_notes'::regclass);
                    forced_addenda boolean := (SELECT relforcerowsecurity FROM pg_class WHERE oid = 'ehr.note_addenda'::regclass);
                BEGIN
                    ALTER TABLE ehr.clinical_notes NO FORCE ROW LEVEL SECURITY;
                    ALTER TABLE ehr.note_addenda NO FORCE ROW LEVEL SECURITY;
                    ALTER TABLE ehr.note_addenda DISABLE TRIGGER USER;

                    UPDATE ehr.note_addenda a
                    SET sensitive_marked_by = coalesce(n.sensitive_marked_by, a.author_id)
                    FROM ehr.clinical_notes n
                    WHERE n.id = a.note_id AND a.sensitive_inherited;

                    ALTER TABLE ehr.note_addenda ENABLE TRIGGER USER;
                    IF forced_notes THEN
                        ALTER TABLE ehr.clinical_notes FORCE ROW LEVEL SECURITY;
                    END IF;
                    IF forced_addenda THEN
                        ALTER TABLE ehr.note_addenda FORCE ROW LEVEL SECURITY;
                    END IF;
                END
                $$;
                DROP TRIGGER IF EXISTS clinical_notes_clear_inherited ON ehr.clinical_notes;
                DROP FUNCTION IF EXISTS ehr.clear_inherited_addenda_marks();
                DROP POLICY IF EXISTS note_addenda_panel_update ON ehr.note_addenda;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_note_addenda_inherited",
                schema: "ehr",
                table: "note_addenda");

            migrationBuilder.DropColumn(
                name: "sensitive_inherited",
                schema: "ehr",
                table: "note_addenda");
        }
    }
}
