using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ehr.Migrations
{
    /// <inheritdoc />
    public partial class SoapNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "history",
                schema: "ehr",
                table: "clinical_notes",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<string>(
                name: "objective",
                schema: "ehr",
                table: "clinical_notes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "subjective",
                schema: "ehr",
                table: "clinical_notes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "note_template_code",
                schema: "ehr",
                table: "clinical_drafts",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "objective",
                schema: "ehr",
                table: "clinical_drafts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "subjective",
                schema: "ehr",
                table: "clinical_drafts",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "note_templates",
                schema: "ehr",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    title = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    subjective_prompt = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    objective_prompt = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    assessment_prompt = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    plan_prompt = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_note_templates", x => x.code);
                    table.CheckConstraint("ck_note_templates_assessment_prompt", "assessment_prompt ~ '[^[:space:]]'");
                    table.CheckConstraint("ck_note_templates_code", "code ~ '^[a-z][a-z0-9-]{1,39}$'");
                    table.CheckConstraint("ck_note_templates_objective_prompt", "objective_prompt ~ '[^[:space:]]'");
                    table.CheckConstraint("ck_note_templates_plan_prompt", "plan_prompt ~ '[^[:space:]]'");
                    table.CheckConstraint("ck_note_templates_position", "position > 0");
                    table.CheckConstraint("ck_note_templates_subjective_prompt", "subjective_prompt ~ '[^[:space:]]'");
                    table.CheckConstraint("ck_note_templates_title", "title ~ '[^[:space:]]'");
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_clinical_notes_sections",
                schema: "ehr",
                table: "clinical_notes",
                sql: "(history IS NULL AND subjective IS NOT NULL AND objective IS NOT NULL) OR (history IS NOT NULL AND subjective IS NULL AND objective IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_clinical_drafts_note_template_code",
                schema: "ehr",
                table: "clinical_drafts",
                column: "note_template_code");

            migrationBuilder.AddCheckConstraint(
                name: "ck_clinical_drafts_sections",
                schema: "ehr",
                table: "clinical_drafts",
                sql: "history IS NULL OR (subjective IS NULL AND objective IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_note_templates_position",
                schema: "ehr",
                table: "note_templates",
                column: "position",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_note_templates_title",
                schema: "ehr",
                table: "note_templates",
                column: "title",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_clinical_drafts_note_templates_note_template_code",
                schema: "ehr",
                table: "clinical_drafts",
                column: "note_template_code",
                principalSchema: "ehr",
                principalTable: "note_templates",
                principalColumn: "code",
                onDelete: ReferentialAction.Restrict);

            // Written by hand (the expansion plan's step 14). Notes are written in four sections from now on, and nothing
            // already written moves: a signed note keeps its History, Assessment and Plan exactly as signed (its text
            // never changes, 50-clinical-notes.sql), and an unsigned draft keeps its History until its author saves the
            // draft in four sections, which the editor offers with the History in Subjective. So no row changes here,
            // nothing is audited and no desktop is sent a draft again. What stays as it was is reported with RAISE
            // NOTICE, which the migration job prints (DatabaseNotices): the signed notes, which the tables' owner reads
            // under its own policy (30-rls.sql). Drafts are private to their authors, and are not read.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    kept bigint := (SELECT count(*) FROM ehr.clinical_notes);
                BEGIN
                    RAISE NOTICE 'Migration 14: % signed notes keep their History, Assessment and Plan as signed; no row was changed', kept;
                    RAISE NOTICE 'Migration 14: unsigned drafts were not read; each opens in the editor with its History in Subjective, and changes only when its author saves it';
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Written by hand. Undone only while nothing has been written in four sections: a note signed in them can't
            // go back to History without changing what was signed, and a draft's Subjective and Objective would be
            // dropped with their columns. Drafts are private to their authors, so the owner sees none under forced
            // row-level security: it is lifted on clinical_drafts for the check alone, as the owner of the table, and
            // put back before anything is refused, in this transaction either way.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    forced boolean := (SELECT relforcerowsecurity FROM pg_class WHERE oid = 'ehr.clinical_drafts'::regclass);
                    drafts boolean;
                BEGIN
                    IF EXISTS (SELECT 1 FROM ehr.clinical_notes WHERE history IS NULL) THEN
                        RAISE EXCEPTION 'Migration 14 is not undone: notes have been signed in Subjective, Objective, Assessment and Plan, and a signed note''s text never changes'
                            USING ERRCODE = '55000';
                    END IF;
                    IF forced THEN
                        ALTER TABLE ehr.clinical_drafts NO FORCE ROW LEVEL SECURITY;
                    END IF;
                    drafts := EXISTS (SELECT 1 FROM ehr.clinical_drafts WHERE subjective IS NOT NULL OR objective IS NOT NULL);
                    IF forced THEN
                        ALTER TABLE ehr.clinical_drafts FORCE ROW LEVEL SECURITY;
                    END IF;
                    IF drafts THEN
                        RAISE EXCEPTION 'Migration 14 is not undone: drafts have been written in Subjective, Objective, Assessment and Plan'
                            USING ERRCODE = '55000';
                    END IF;
                END
                $$;
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_clinical_drafts_note_templates_note_template_code",
                schema: "ehr",
                table: "clinical_drafts");

            migrationBuilder.DropTable(
                name: "note_templates",
                schema: "ehr");

            migrationBuilder.DropCheckConstraint(
                name: "ck_clinical_notes_sections",
                schema: "ehr",
                table: "clinical_notes");

            migrationBuilder.DropIndex(
                name: "ix_clinical_drafts_note_template_code",
                schema: "ehr",
                table: "clinical_drafts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_clinical_drafts_sections",
                schema: "ehr",
                table: "clinical_drafts");

            migrationBuilder.DropColumn(
                name: "objective",
                schema: "ehr",
                table: "clinical_notes");

            migrationBuilder.DropColumn(
                name: "subjective",
                schema: "ehr",
                table: "clinical_notes");

            migrationBuilder.DropColumn(
                name: "note_template_code",
                schema: "ehr",
                table: "clinical_drafts");

            migrationBuilder.DropColumn(
                name: "objective",
                schema: "ehr",
                table: "clinical_drafts");

            migrationBuilder.DropColumn(
                name: "subjective",
                schema: "ehr",
                table: "clinical_drafts");

            migrationBuilder.AlterColumn<string>(
                name: "history",
                schema: "ehr",
                table: "clinical_notes",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
