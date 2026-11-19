using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ehr.Migrations
{
    /// <inheritdoc />
    public partial class VitalCorrections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_observations_vital_sign_once",
                schema: "ehr",
                table: "observations");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "entered_in_error_at",
                schema: "ehr",
                table: "observations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "entered_in_error_by",
                schema: "ehr",
                table: "observations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "entered_in_error_reason",
                schema: "ehr",
                table: "observations",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "vital_reference_ranges",
                schema: "ehr",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    band = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    from_months = table.Column<int>(type: "integer", nullable: false),
                    to_months = table.Column<int>(type: "integer", nullable: true),
                    low = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    high = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    source = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vital_reference_ranges", x => new { x.code, x.band });
                    table.CheckConstraint("ck_vital_reference_ranges_ages", "from_months >= 0 AND (to_months IS NULL OR to_months > from_months)");
                    table.CheckConstraint("ck_vital_reference_ranges_band", "band IN ('infant', 'young-child', 'older-child', 'adolescent', 'adult')");
                    table.CheckConstraint("ck_vital_reference_ranges_range", "low <= high AND (low IS NOT NULL OR high IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_vital_reference_ranges_observation_codes_code",
                        column: x => x.code,
                        principalSchema: "ehr",
                        principalTable: "observation_codes",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_observations_entered_in_error_by",
                schema: "ehr",
                table: "observations",
                column: "entered_in_error_by");

            migrationBuilder.CreateIndex(
                name: "ux_observations_vital_sign_once",
                schema: "ehr",
                table: "observations",
                columns: new[] { "patient_id", "code", "effective_at", "encounter_id" },
                unique: true,
                filter: "category = 'vital-signs' AND status <> 'entered-in-error'")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.AddCheckConstraint(
                name: "ck_observations_entered_in_error",
                schema: "ehr",
                table: "observations",
                sql: "(entered_in_error_reason IS NULL AND entered_in_error_by IS NULL AND entered_in_error_at IS NULL) OR status = 'entered-in-error'");

            migrationBuilder.AddForeignKey(
                name: "fk_observations_staff_users_entered_in_error_by",
                schema: "ehr",
                table: "observations",
                column: "entered_in_error_by",
                principalSchema: "ehr",
                principalTable: "staff_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // Written by hand. Vital signs recorded before this step carry the adults' reference range whatever the
            // patient's age. They get the range for the patient's age on the day each was taken, from the same
            // figures db/reference/35-vital-ranges.sql loads (that file runs after the migrations, so they are
            // repeated here). As in the VitalSigns migration: forced row-level security is lifted for the owner
            // while the rows are corrected, and the audit trigger is paused, since no value changes.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    forced_patients boolean := (SELECT relforcerowsecurity FROM pg_class WHERE oid = 'ehr.patients'::regclass);
                    forced_observations boolean := (SELECT relforcerowsecurity FROM pg_class WHERE oid = 'ehr.observations'::regclass);
                    audited boolean := EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'ehr.observations'::regclass AND tgname = 'observations_audit');
                BEGIN
                    ALTER TABLE ehr.patients NO FORCE ROW LEVEL SECURITY;
                    ALTER TABLE ehr.observations NO FORCE ROW LEVEL SECURITY;
                    IF audited THEN
                        ALTER TABLE ehr.observations DISABLE TRIGGER observations_audit;
                    END IF;

                    CREATE TEMPORARY TABLE vital_ranges_then (code text, from_months int, to_months int, low numeric, high numeric) ON COMMIT DROP;
                    INSERT INTO vital_ranges_then VALUES
                        ('8867-4', 0, 12::int, 91::numeric, 186::numeric),
                        ('8867-4', 12, 72::int, 66::numeric, 173::numeric),
                        ('8867-4', 72, 144::int, 56::numeric, 155::numeric),
                        ('8867-4', 144, 216::int, 48::numeric, 138::numeric),
                        ('8867-4', 216, NULL::int, 60::numeric, 100::numeric),
                        ('9279-1', 0, 12::int, 19::numeric, 76::numeric),
                        ('9279-1', 12, 72::int, 14::numeric, 60::numeric),
                        ('9279-1', 72, 144::int, 13::numeric, 46::numeric),
                        ('9279-1', 144, 216::int, 11::numeric, 35::numeric),
                        ('9279-1', 216, NULL::int, 12::numeric, 20::numeric),
                        ('8480-6', 0, 12::int, 70::numeric, NULL::numeric),
                        ('8480-6', 12, 72::int, 72::numeric, 103::numeric),
                        ('8480-6', 72, 144::int, 82::numeric, 110::numeric),
                        ('8480-6', 144, 216::int, 90::numeric, 119::numeric),
                        ('8480-6', 216, NULL::int, 90::numeric, 129::numeric),
                        ('8462-4', 12, 72::int, NULL::numeric, 63::numeric),
                        ('8462-4', 72, 144::int, NULL::numeric, 73::numeric),
                        ('8462-4', 144, 216::int, NULL::numeric, 79::numeric),
                        ('8462-4', 216, NULL::int, 60::numeric, 79::numeric);

                    WITH aged AS (
                        SELECT o.id, o.code,
                            greatest(0, (extract(year FROM age((o.effective_at AT TIME ZONE 'UTC')::date, p.birth_date)) * 12
                                + extract(month FROM age((o.effective_at AT TIME ZONE 'UTC')::date, p.birth_date)))::int) AS months
                        FROM ehr.observations o JOIN ehr.patients p ON p.id = o.patient_id
                        WHERE o.category = 'vital-signs' AND o.code IN (SELECT code FROM vital_ranges_then))
                    UPDATE ehr.observations o
                    SET reference_low = r.low, reference_high = r.high
                    FROM aged a
                    LEFT JOIN vital_ranges_then r ON r.code = a.code AND a.months >= r.from_months AND (r.to_months IS NULL OR a.months < r.to_months)
                    WHERE o.id = a.id AND (o.reference_low, o.reference_high) IS DISTINCT FROM (r.low, r.high);

                    IF audited THEN
                        ALTER TABLE ehr.observations ENABLE TRIGGER observations_audit;
                    END IF;
                    IF forced_patients THEN
                        ALTER TABLE ehr.patients FORCE ROW LEVEL SECURITY;
                    END IF;
                    IF forced_observations THEN
                        ALTER TABLE ehr.observations FORCE ROW LEVEL SECURITY;
                    END IF;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_observations_staff_users_entered_in_error_by",
                schema: "ehr",
                table: "observations");

            migrationBuilder.DropTable(
                name: "vital_reference_ranges",
                schema: "ehr");

            migrationBuilder.DropIndex(
                name: "ix_observations_entered_in_error_by",
                schema: "ehr",
                table: "observations");

            migrationBuilder.DropIndex(
                name: "ux_observations_vital_sign_once",
                schema: "ehr",
                table: "observations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_observations_entered_in_error",
                schema: "ehr",
                table: "observations");

            migrationBuilder.DropColumn(
                name: "entered_in_error_at",
                schema: "ehr",
                table: "observations");

            migrationBuilder.DropColumn(
                name: "entered_in_error_by",
                schema: "ehr",
                table: "observations");

            migrationBuilder.DropColumn(
                name: "entered_in_error_reason",
                schema: "ehr",
                table: "observations");

            migrationBuilder.CreateIndex(
                name: "ux_observations_vital_sign_once",
                schema: "ehr",
                table: "observations",
                columns: new[] { "patient_id", "code", "effective_at", "encounter_id" },
                unique: true,
                filter: "category = 'vital-signs'")
                .Annotation("Npgsql:NullsDistinct", false);
        }
    }
}
