using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ehr.Migrations
{
    /// <inheritdoc />
    public partial class AmbientListening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "consent_id",
                schema: "ehr",
                table: "ai_note_reviews",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "mode",
                schema: "ehr",
                table: "ai_note_reviews",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "dictation");

            migrationBuilder.AddColumn<bool>(
                name: "sensitive",
                schema: "ehr",
                table: "ai_note_reviews",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "sensitive",
                schema: "ehr",
                table: "ai_field_proposals",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ambient_consents",
                schema: "ehr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    clinician_id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "NULLIF(current_setting('app.user_id', true), '')::uuid"),
                    speakers = table.Column<short>(type: "smallint", nullable: false),
                    given_by = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ambient_consents", x => x.id);
                    table.UniqueConstraint("ak_ambient_consents_id_patient_id", x => new { x.id, x.patient_id });
                    table.CheckConstraint("ck_ambient_consents_given_by", "given_by IN ('patient', 'representative')");
                    table.CheckConstraint("ck_ambient_consents_speakers", "speakers IN (2, 3)");
                    table.ForeignKey(
                        name: "fk_ambient_consents_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "ehr",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ambient_consents_staff_users_clinician_id",
                        column: x => x.clinician_id,
                        principalSchema: "ehr",
                        principalTable: "staff_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ambient_transcripts",
                schema: "ehr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transcript = table.Column<string>(type: "jsonb", nullable: false),
                    sensitive = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ambient_transcripts", x => x.id);
                    table.CheckConstraint("ck_ambient_transcripts_sensitive", "sensitive");
                    table.CheckConstraint("ck_ambient_transcripts_transcript", "jsonb_typeof(transcript) = 'object' AND octet_length(transcript::text) <= 1048576");
                    table.ForeignKey(
                        name: "fk_ambient_transcripts_ai_note_reviews_id_patient_id",
                        columns: x => new { x.id, x.patient_id },
                        principalSchema: "ehr",
                        principalTable: "ai_note_reviews",
                        principalColumns: new[] { "id", "patient_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_note_reviews_consent_id",
                schema: "ehr",
                table: "ai_note_reviews",
                column: "consent_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ai_note_reviews_consent_id_patient_id",
                schema: "ehr",
                table: "ai_note_reviews",
                columns: new[] { "consent_id", "patient_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_note_reviews_consent",
                schema: "ehr",
                table: "ai_note_reviews",
                sql: "(mode = 'ambient') = (consent_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_note_reviews_mode",
                schema: "ehr",
                table: "ai_note_reviews",
                sql: "mode IN ('dictation', 'ambient')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_note_reviews_sensitive",
                schema: "ehr",
                table: "ai_note_reviews",
                sql: "sensitive = (mode = 'ambient')");

            migrationBuilder.CreateIndex(
                name: "ix_ambient_consents_clinician_id",
                schema: "ehr",
                table: "ambient_consents",
                column: "clinician_id");

            migrationBuilder.CreateIndex(
                name: "ix_ambient_consents_patient_id",
                schema: "ehr",
                table: "ambient_consents",
                column: "patient_id");

            migrationBuilder.CreateIndex(
                name: "ix_ambient_transcripts_id_patient_id",
                schema: "ehr",
                table: "ambient_transcripts",
                columns: new[] { "id", "patient_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_ai_note_reviews_ambient_consents_consent_id_patient_id",
                schema: "ehr",
                table: "ai_note_reviews",
                columns: new[] { "consent_id", "patient_id" },
                principalSchema: "ehr",
                principalTable: "ambient_consents",
                principalColumns: new[] { "id", "patient_id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Written by hand. Undone only while no visit has been listened to: each consent is a record, and a visit's
            // review would become a dictation's with its column. Consents are private to their clinicians, so the owner sees
            // none under forced row-level security: it is lifted on ambient_consents for the check alone, as the table's
            // owner, and put back before anything is refused, in this transaction either way. Then everything of this step
            // in db/policies/56-ambient-listening.sql goes, as the Down of NoteSensitivity and VitalReadings removes theirs:
            // the constraint trigger whose WHEN names the review's mode, so the column can; the guards on reviews and
            // proposals, which name the consents' table and would refuse every dictation's review once it is gone, even
            // after the earlier release's db/policies is applied, which doesn't know them; the guards on the two tables
            // this drops; and the purge. The migration job creates them all again after Up.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    forced boolean := (SELECT relforcerowsecurity FROM pg_class WHERE oid = 'ehr.ambient_consents'::regclass);
                    listened boolean;
                BEGIN
                    IF forced THEN
                        ALTER TABLE ehr.ambient_consents NO FORCE ROW LEVEL SECURITY;
                    END IF;
                    listened := EXISTS (SELECT 1 FROM ehr.ambient_consents);
                    IF forced THEN
                        ALTER TABLE ehr.ambient_consents FORCE ROW LEVEL SECURITY;
                    END IF;
                    IF listened THEN
                        RAISE EXCEPTION 'Migration AmbientListening is not undone: visits have been listened to, and each consent is a record'
                            USING ERRCODE = '55000';
                    END IF;
                END
                $$;
                DROP TRIGGER IF EXISTS ai_note_reviews_transcript_kept ON ehr.ai_note_reviews;
                DROP TRIGGER IF EXISTS ai_note_reviews_ambient ON ehr.ai_note_reviews;
                DROP TRIGGER IF EXISTS ai_field_proposals_ambient ON ehr.ai_field_proposals;
                DROP TRIGGER IF EXISTS ambient_transcripts_guard ON ehr.ambient_transcripts;
                DROP TRIGGER IF EXISTS ambient_consents_guard ON ehr.ambient_consents;
                DROP FUNCTION IF EXISTS ehr.check_ambient_transcript_kept();
                DROP FUNCTION IF EXISTS ehr.guard_ambient_review();
                DROP FUNCTION IF EXISTS ehr.guard_ambient_proposal();
                DROP FUNCTION IF EXISTS ehr.guard_ambient_transcript();
                DROP FUNCTION IF EXISTS ehr.guard_ambient_consent();
                DROP FUNCTION IF EXISTS ehr.purge_ambient_transcripts(interval);
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_ai_note_reviews_ambient_consents_consent_id_patient_id",
                schema: "ehr",
                table: "ai_note_reviews");

            migrationBuilder.DropTable(
                name: "ambient_consents",
                schema: "ehr");

            migrationBuilder.DropTable(
                name: "ambient_transcripts",
                schema: "ehr");

            migrationBuilder.DropIndex(
                name: "ix_ai_note_reviews_consent_id",
                schema: "ehr",
                table: "ai_note_reviews");

            migrationBuilder.DropIndex(
                name: "ix_ai_note_reviews_consent_id_patient_id",
                schema: "ehr",
                table: "ai_note_reviews");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_note_reviews_consent",
                schema: "ehr",
                table: "ai_note_reviews");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_note_reviews_mode",
                schema: "ehr",
                table: "ai_note_reviews");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_note_reviews_sensitive",
                schema: "ehr",
                table: "ai_note_reviews");

            migrationBuilder.DropColumn(
                name: "consent_id",
                schema: "ehr",
                table: "ai_note_reviews");

            migrationBuilder.DropColumn(
                name: "mode",
                schema: "ehr",
                table: "ai_note_reviews");

            migrationBuilder.DropColumn(
                name: "sensitive",
                schema: "ehr",
                table: "ai_note_reviews");

            migrationBuilder.DropColumn(
                name: "sensitive",
                schema: "ehr",
                table: "ai_field_proposals");
        }
    }
}
