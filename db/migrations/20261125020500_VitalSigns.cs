using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ehr.Migrations
{
    /// <inheritdoc />
    public partial class VitalSigns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "category",
                schema: "ehr",
                table: "observations",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "laboratory");

            migrationBuilder.AddColumn<Guid>(
                name: "encounter_id",
                schema: "ehr",
                table: "observations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_observations_encounter_id",
                schema: "ehr",
                table: "observations",
                column: "encounter_id");

            migrationBuilder.CreateIndex(
                name: "ix_observations_patient_id_category_code_effective_at",
                schema: "ehr",
                table: "observations",
                columns: new[] { "patient_id", "category", "code", "effective_at" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_observations_category",
                schema: "ehr",
                table: "observations",
                sql: "category IN ('laboratory', 'vital-signs')");

            migrationBuilder.AddForeignKey(
                name: "fk_observations_encounters_encounter_id",
                schema: "ehr",
                table: "observations",
                column: "encounter_id",
                principalSchema: "ehr",
                principalTable: "encounters",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // The vital signs a visit carried become observations before its columns go. Written by hand: the
            // generated migration dropped the columns first. On a database that is already in use, row-level security
            // is forced on both tables, which would hide every row from this migration and lose the values, so it is
            // lifted for the tables' owner while the rows are copied, and put back; the audit trigger is paused for
            // the copy, since moving a value between columns is not a clinical write. The names, units and reference
            // ranges are the ones db/reference gives these three codes.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    forced_encounters boolean := (SELECT relforcerowsecurity FROM pg_class WHERE oid = 'ehr.encounters'::regclass);
                    forced_observations boolean := (SELECT relforcerowsecurity FROM pg_class WHERE oid = 'ehr.observations'::regclass);
                    audited boolean := EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'ehr.observations'::regclass AND tgname = 'observations_audit');
                BEGIN
                    ALTER TABLE ehr.encounters NO FORCE ROW LEVEL SECURITY;
                    ALTER TABLE ehr.observations NO FORCE ROW LEVEL SECURITY;
                    IF audited THEN
                        ALTER TABLE ehr.observations DISABLE TRIGGER observations_audit;
                    END IF;

                    INSERT INTO ehr.observations (id, patient_id, encounter_id, category, code, display, status, value, unit, reference_low, reference_high, effective_at, issued_at)
                    SELECT uuidv7(), e.patient_id, e.id, 'vital-signs', v.code, v.display, 'final', v.value, v.unit, v.low, v.high, e.period_start, e.period_start
                    FROM ehr.encounters e
                    CROSS JOIN LATERAL (VALUES
                        ('8480-6', 'Systolic blood pressure', e.systolic_mm_hg, 'mm[Hg]', 90, 129),
                        ('8462-4', 'Diastolic blood pressure', e.diastolic_mm_hg, 'mm[Hg]', 60, 84),
                        ('8867-4', 'Heart rate', e.heart_rate_per_minute, '/min', 60, 100)
                    ) AS v(code, display, value, unit, low, high)
                    WHERE v.value IS NOT NULL;

                    IF audited THEN
                        ALTER TABLE ehr.observations ENABLE TRIGGER observations_audit;
                    END IF;
                    IF forced_encounters THEN
                        ALTER TABLE ehr.encounters FORCE ROW LEVEL SECURITY;
                    END IF;
                    IF forced_observations THEN
                        ALTER TABLE ehr.observations FORCE ROW LEVEL SECURITY;
                    END IF;
                END
                $$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_encounters_vital_signs",
                schema: "ehr",
                table: "encounters");

            migrationBuilder.DropColumn(
                name: "diastolic_mm_hg",
                schema: "ehr",
                table: "encounters");

            migrationBuilder.DropColumn(
                name: "heart_rate_per_minute",
                schema: "ehr",
                table: "encounters");

            migrationBuilder.DropColumn(
                name: "systolic_mm_hg",
                schema: "ehr",
                table: "encounters");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "diastolic_mm_hg",
                schema: "ehr",
                table: "encounters",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "heart_rate_per_minute",
                schema: "ehr",
                table: "encounters",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "systolic_mm_hg",
                schema: "ehr",
                table: "encounters",
                type: "smallint",
                nullable: true);

            // Written by hand, like the copy in Up: the visits get their blood pressure and heart rate back (the
            // latest of each taken at the visit), and then every vital-sign observation is deleted, because the
            // schema this returns to has no category and would show them as laboratory results. Vital signs that
            // were not taken at a visit, and the other six signs, have no place in that schema and are lost: roll
            // back only a database where step 9 was not used.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    forced_encounters boolean := (SELECT relforcerowsecurity FROM pg_class WHERE oid = 'ehr.encounters'::regclass);
                    forced_observations boolean := (SELECT relforcerowsecurity FROM pg_class WHERE oid = 'ehr.observations'::regclass);
                    encounters_audited boolean := EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'ehr.encounters'::regclass AND tgname = 'encounters_audit');
                    observations_audited boolean := EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'ehr.observations'::regclass AND tgname = 'observations_audit');
                BEGIN
                    ALTER TABLE ehr.encounters NO FORCE ROW LEVEL SECURITY;
                    ALTER TABLE ehr.observations NO FORCE ROW LEVEL SECURITY;
                    IF encounters_audited THEN
                        ALTER TABLE ehr.encounters DISABLE TRIGGER encounters_audit;
                    END IF;
                    IF observations_audited THEN
                        ALTER TABLE ehr.observations DISABLE TRIGGER observations_audit;
                    END IF;

                    UPDATE ehr.encounters e
                    SET systolic_mm_hg = (SELECT round(o.value)::smallint FROM ehr.observations o
                                          WHERE o.encounter_id = e.id AND o.code = '8480-6' ORDER BY o.effective_at DESC, o.issued_at DESC LIMIT 1),
                        diastolic_mm_hg = (SELECT round(o.value)::smallint FROM ehr.observations o
                                           WHERE o.encounter_id = e.id AND o.code = '8462-4' ORDER BY o.effective_at DESC, o.issued_at DESC LIMIT 1),
                        heart_rate_per_minute = (SELECT round(o.value)::smallint FROM ehr.observations o
                                                 WHERE o.encounter_id = e.id AND o.code = '8867-4' ORDER BY o.effective_at DESC, o.issued_at DESC LIMIT 1)
                    WHERE EXISTS (SELECT 1 FROM ehr.observations o WHERE o.encounter_id = e.id);

                    DELETE FROM ehr.observations WHERE category = 'vital-signs';

                    IF observations_audited THEN
                        ALTER TABLE ehr.observations ENABLE TRIGGER observations_audit;
                    END IF;
                    IF encounters_audited THEN
                        ALTER TABLE ehr.encounters ENABLE TRIGGER encounters_audit;
                    END IF;
                    IF forced_encounters THEN
                        ALTER TABLE ehr.encounters FORCE ROW LEVEL SECURITY;
                    END IF;
                    IF forced_observations THEN
                        ALTER TABLE ehr.observations FORCE ROW LEVEL SECURITY;
                    END IF;
                END
                $$;
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_observations_encounters_encounter_id",
                schema: "ehr",
                table: "observations");

            migrationBuilder.DropIndex(
                name: "ix_observations_encounter_id",
                schema: "ehr",
                table: "observations");

            migrationBuilder.DropIndex(
                name: "ix_observations_patient_id_category_code_effective_at",
                schema: "ehr",
                table: "observations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_observations_category",
                schema: "ehr",
                table: "observations");

            migrationBuilder.DropColumn(
                name: "category",
                schema: "ehr",
                table: "observations");

            migrationBuilder.DropColumn(
                name: "encounter_id",
                schema: "ehr",
                table: "observations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_encounters_vital_signs",
                schema: "ehr",
                table: "encounters",
                sql: "systolic_mm_hg > 0 AND diastolic_mm_hg > 0 AND heart_rate_per_minute > 0");
        }
    }
}
