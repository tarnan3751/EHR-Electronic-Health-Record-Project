using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ehr.Migrations
{
    /// <inheritdoc />
    public partial class ChartSections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "allergy_intolerances",
                schema: "ehr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    display = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    clinical_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    category = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    criticality = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    reaction = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_allergy_intolerances", x => x.id);
                    table.CheckConstraint("ck_allergy_intolerances_category", "category IN ('food', 'medication', 'environment', 'biologic')");
                    table.CheckConstraint("ck_allergy_intolerances_clinical_status", "clinical_status IN ('active', 'inactive', 'resolved')");
                    table.CheckConstraint("ck_allergy_intolerances_criticality", "criticality IN ('low', 'high', 'unable-to-assess')");
                    table.ForeignKey(
                        name: "fk_allergy_intolerances_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "ehr",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "conditions",
                schema: "ehr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    display = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    clinical_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    onset_date = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_conditions", x => x.id);
                    table.CheckConstraint("ck_conditions_clinical_status", "clinical_status IN ('active', 'recurrence', 'relapse', 'inactive', 'remission', 'resolved')");
                    table.ForeignKey(
                        name: "fk_conditions_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "ehr",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "encounters",
                schema: "ehr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    class_code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    period_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    period_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    systolic_mm_hg = table.Column<short>(type: "smallint", nullable: true),
                    diastolic_mm_hg = table.Column<short>(type: "smallint", nullable: true),
                    heart_rate_per_minute = table.Column<short>(type: "smallint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_encounters", x => x.id);
                    table.CheckConstraint("ck_encounters_status", "status IN ('planned', 'arrived', 'triaged', 'in-progress', 'onleave', 'finished', 'cancelled', 'entered-in-error', 'unknown')");
                    table.CheckConstraint("ck_encounters_vital_signs", "systolic_mm_hg > 0 AND diastolic_mm_hg > 0 AND heart_rate_per_minute > 0");
                    table.ForeignKey(
                        name: "fk_encounters_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "ehr",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "medication_statements",
                schema: "ehr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    display = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    dosage = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    effective_start = table.Column<DateOnly>(type: "date", nullable: true),
                    effective_end = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_medication_statements", x => x.id);
                    table.CheckConstraint("ck_medication_statements_status", "status IN ('active', 'completed', 'entered-in-error', 'intended', 'stopped', 'on-hold', 'unknown', 'not-taken')");
                    table.ForeignKey(
                        name: "fk_medication_statements_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "ehr",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_allergy_intolerances_patient_id",
                schema: "ehr",
                table: "allergy_intolerances",
                column: "patient_id");

            migrationBuilder.CreateIndex(
                name: "ix_conditions_patient_id",
                schema: "ehr",
                table: "conditions",
                column: "patient_id");

            migrationBuilder.CreateIndex(
                name: "ix_encounters_patient_id",
                schema: "ehr",
                table: "encounters",
                column: "patient_id");

            migrationBuilder.CreateIndex(
                name: "ix_medication_statements_patient_id",
                schema: "ehr",
                table: "medication_statements",
                column: "patient_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "allergy_intolerances",
                schema: "ehr");

            migrationBuilder.DropTable(
                name: "conditions",
                schema: "ehr");

            migrationBuilder.DropTable(
                name: "encounters",
                schema: "ehr");

            migrationBuilder.DropTable(
                name: "medication_statements",
                schema: "ehr");
        }
    }
}
