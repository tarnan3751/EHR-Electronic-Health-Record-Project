using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ehr.Migrations
{
    /// <inheritdoc />
    public partial class VitalReadings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_observations_vital_sign_once",
                schema: "ehr",
                table: "observations");

            migrationBuilder.AddColumn<Guid>(
                name: "reading_id",
                schema: "ehr",
                table: "observations",
                type: "uuid",
                nullable: true);

            // Written by hand (the expansion plan's step 9c). Before the new rules can hold, the vital signs already
            // recorded are put in their shape, in this order:
            //   1. Every vital sign of a patient's moment (the time it was taken) gets the visit of the first one
            //      recorded there, as the database now decides it; a check-in between two values could split them.
            //   2. Where a sign has more than one standing value at a moment, the most recently entered (issued_at,
            //      then id) is kept and the others are marked entered in error, "duplicate resolved by migration 9c".
            //   3. A standing blood pressure value whose partner was marked in error alone, under step 9b, is marked
            //      too, "partner value was marked in error before readings existed (migration 9c)" (Tahseen's rule).
            //   4. A standing blood pressure value that never had a partner (only an insert past the apps could leave
            //      one) is marked "blood pressure value without its partner (migration 9c)".
            //   5. Each standing systolic and diastolic of a moment become one reading. Values in error get readings
            //      too: a moment's one systolic and one diastolic in error share one, any other has one of its own.
            // 1 to 4 change patient data and are audited, as the write-audit trigger records any change: with nobody
            // signed in, the audit page shows "System" as who, and the reason is in the change. 5 fills a new column,
            // not audited, as VitalCorrections re-ranged readings. Marked rows have no one recorded as marking them.
            // As in VitalCorrections, forced row-level security is lifted on observations for the owner while it runs,
            // and the body mass index's cascade is paused (marking an older duplicate weight would mark the body mass
            // index that is kept). It reports each count with RAISE NOTICE, which the migration job prints
            // (DatabaseNotices). Run time in the step's log; the migration job allows ten minutes (infra/migrate).
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    forced boolean := (SELECT relforcerowsecurity FROM pg_class WHERE oid = 'ehr.observations'::regclass);
                    audited boolean := EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'ehr.observations'::regclass AND tgname = 'observations_audit');
                    cascading boolean := EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'ehr.observations'::regclass AND tgname = 'observations_mark_body_mass_index');
                    moved bigint;
                    duplicates bigint;
                    partners bigint;
                    unpaired bigint;
                    readings bigint;
                    apart bigint;
                BEGIN
                    ALTER TABLE ehr.observations NO FORCE ROW LEVEL SECURITY;
                    IF cascading THEN
                        ALTER TABLE ehr.observations DISABLE TRIGGER observations_mark_body_mass_index;
                    END IF;

                    WITH split AS (
                        SELECT patient_id, effective_at FROM ehr.observations WHERE category = 'vital-signs'
                        GROUP BY patient_id, effective_at
                        HAVING count(DISTINCT encounter_id) + max(CASE WHEN encounter_id IS NULL THEN 1 ELSE 0 END) > 1),
                    firsts AS (
                        SELECT DISTINCT ON (o.patient_id, o.effective_at) o.patient_id, o.effective_at, o.encounter_id
                        FROM ehr.observations o JOIN split s ON s.patient_id = o.patient_id AND s.effective_at = o.effective_at
                        WHERE o.category = 'vital-signs'
                        ORDER BY o.patient_id, o.effective_at, o.issued_at, o.id)
                    UPDATE ehr.observations o SET encounter_id = f.encounter_id
                    FROM firsts f
                    WHERE o.category = 'vital-signs' AND o.patient_id = f.patient_id AND o.effective_at = f.effective_at
                      AND o.encounter_id IS DISTINCT FROM f.encounter_id;
                    GET DIAGNOSTICS moved = ROW_COUNT;

                    WITH ranked AS (
                        SELECT id, row_number() OVER (PARTITION BY patient_id, code, effective_at ORDER BY issued_at DESC NULLS LAST, id DESC) AS place
                        FROM ehr.observations WHERE category = 'vital-signs' AND status <> 'entered-in-error')
                    UPDATE ehr.observations o
                    SET status = 'entered-in-error', entered_in_error_reason = 'duplicate resolved by migration 9c', entered_in_error_at = clock_timestamp()
                    FROM ranked r WHERE o.id = r.id AND r.place > 1;
                    GET DIAGNOSTICS duplicates = ROW_COUNT;

                    -- Each moment's blood pressure: which of its two values stand, and which are there at all.
                    CREATE TEMPORARY TABLE pressure_moments ON COMMIT DROP AS
                    SELECT patient_id, effective_at,
                        bool_or(code = '8480-6' AND status <> 'entered-in-error') AS systolic, bool_or(code = '8462-4' AND status <> 'entered-in-error') AS diastolic,
                        bool_or(code = '8480-6') AS any_systolic, bool_or(code = '8462-4') AS any_diastolic
                    FROM ehr.observations WHERE category = 'vital-signs' AND code IN ('8480-6', '8462-4')
                    GROUP BY patient_id, effective_at;

                    UPDATE ehr.observations o
                    SET status = 'entered-in-error', entered_in_error_reason = 'partner value was marked in error before readings existed (migration 9c)',
                        entered_in_error_at = clock_timestamp()
                    FROM pressure_moments m
                    WHERE o.patient_id = m.patient_id AND o.effective_at = m.effective_at AND o.category = 'vital-signs' AND o.status <> 'entered-in-error'
                      AND (o.code = '8480-6' AND NOT m.diastolic AND m.any_diastolic OR o.code = '8462-4' AND NOT m.systolic AND m.any_systolic);
                    GET DIAGNOSTICS partners = ROW_COUNT;

                    UPDATE ehr.observations o
                    SET status = 'entered-in-error', entered_in_error_reason = 'blood pressure value without its partner (migration 9c)',
                        entered_in_error_at = clock_timestamp()
                    FROM pressure_moments m
                    WHERE o.patient_id = m.patient_id AND o.effective_at = m.effective_at AND o.category = 'vital-signs' AND o.status <> 'entered-in-error'
                      AND (o.code = '8480-6' AND NOT m.any_diastolic OR o.code = '8462-4' AND NOT m.any_systolic);
                    GET DIAGNOSTICS unpaired = ROW_COUNT;

                    IF audited THEN
                        ALTER TABLE ehr.observations DISABLE TRIGGER observations_audit;
                    END IF;

                    UPDATE ehr.observations o SET reading_id = r.reading
                    FROM (SELECT patient_id, effective_at, uuidv7() AS reading FROM pressure_moments WHERE systolic AND diastolic) r
                    WHERE o.patient_id = r.patient_id AND o.effective_at = r.effective_at AND o.category = 'vital-signs'
                      AND o.code IN ('8480-6', '8462-4') AND o.status <> 'entered-in-error';
                    GET DIAGNOSTICS readings = ROW_COUNT;

                    WITH loose AS (
                        SELECT id, patient_id, effective_at,
                            count(*) FILTER (WHERE code = '8480-6') OVER moment AS systolics, count(*) FILTER (WHERE code = '8462-4') OVER moment AS diastolics
                        FROM ehr.observations
                        WHERE category = 'vital-signs' AND code IN ('8480-6', '8462-4') AND reading_id IS NULL
                        WINDOW moment AS (PARTITION BY patient_id, effective_at)),
                    pairs AS (
                        SELECT patient_id, effective_at, uuidv7() AS reading FROM loose WHERE systolics = 1 AND diastolics = 1 GROUP BY patient_id, effective_at)
                    UPDATE ehr.observations o SET reading_id = coalesce(p.reading, uuidv7())
                    FROM loose l LEFT JOIN pairs p ON p.patient_id = l.patient_id AND p.effective_at = l.effective_at
                    WHERE o.id = l.id;
                    GET DIAGNOSTICS apart = ROW_COUNT;

                    IF audited THEN
                        ALTER TABLE ehr.observations ENABLE TRIGGER observations_audit;
                    END IF;
                    IF cascading THEN
                        ALTER TABLE ehr.observations ENABLE TRIGGER observations_mark_body_mass_index;
                    END IF;
                    IF forced THEN
                        ALTER TABLE ehr.observations FORCE ROW LEVEL SECURITY;
                    END IF;

                    RAISE NOTICE 'Migration 9c: % vital signs moved to the visit of the first value recorded at their moment (audited)', moved;
                    RAISE NOTICE 'Migration 9c: % duplicate values marked entered in error, "duplicate resolved by migration 9c" (audited)', duplicates;
                    RAISE NOTICE 'Migration 9c: % blood pressure values marked entered in error because their partner was, "partner value was marked in error before readings existed (migration 9c)" (audited)', partners;
                    RAISE NOTICE 'Migration 9c: % blood pressure values marked entered in error that never had a partner, "blood pressure value without its partner (migration 9c)" (audited)', unpaired;
                    RAISE NOTICE 'Migration 9c: % standing blood pressure values made into % readings; % values in error given readings', readings, readings / 2, apart;
                END
                $$;
                """);

            migrationBuilder.CreateIndex(
                name: "ux_observations_reading",
                schema: "ehr",
                table: "observations",
                columns: new[] { "reading_id", "code" },
                unique: true,
                filter: "reading_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_observations_vital_sign_once",
                schema: "ehr",
                table: "observations",
                columns: new[] { "patient_id", "code", "effective_at" },
                unique: true,
                filter: "category = 'vital-signs' AND status <> 'entered-in-error'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_observations_reading",
                schema: "ehr",
                table: "observations",
                sql: "(reading_id IS NOT NULL) = (category = 'vital-signs' AND code IS NOT NULL AND code IN ('8480-6', '8462-4'))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Written by hand. The readings' triggers name the column this drops, and the rest of step 9c's rules
            // (db/policies/75-results.sql and 30-rls.sql) mean nothing without it: they go first. The values marked
            // entered in error and the visits moved stay as they are, which the older schema holds. Until the earlier
            // release's db/policies is applied after a rollback, recording a vital sign fails: apply it straight after.
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS observations_reading_inserted ON ehr.observations;
                DROP TRIGGER IF EXISTS observations_reading_changed ON ehr.observations;
                DROP TRIGGER IF EXISTS observations_reading_deleted ON ehr.observations;
                DROP TRIGGER IF EXISTS observations_mark_reading ON ehr.observations;
                DROP FUNCTION IF EXISTS ehr.check_vital_reading();
                DROP FUNCTION IF EXISTS ehr.mark_reading_in_error();
                DROP POLICY IF EXISTS observations_owner_select ON ehr.observations;
                """);

            migrationBuilder.DropIndex(
                name: "ux_observations_reading",
                schema: "ehr",
                table: "observations");

            migrationBuilder.DropIndex(
                name: "ux_observations_vital_sign_once",
                schema: "ehr",
                table: "observations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_observations_reading",
                schema: "ehr",
                table: "observations");

            migrationBuilder.DropColumn(
                name: "reading_id",
                schema: "ehr",
                table: "observations");

            migrationBuilder.CreateIndex(
                name: "ux_observations_vital_sign_once",
                schema: "ehr",
                table: "observations",
                columns: new[] { "patient_id", "code", "effective_at", "encounter_id" },
                unique: true,
                filter: "category = 'vital-signs' AND status <> 'entered-in-error'")
                .Annotation("Npgsql:NullsDistinct", false);
        }
    }
}
