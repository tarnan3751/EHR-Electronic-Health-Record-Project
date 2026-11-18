using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ehr.Migrations
{
    /// <inheritdoc />
    public partial class Appointments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "appointments",
                schema: "ehr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    clinician_id = table.Column<Guid>(type: "uuid", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    minutes = table.Column<int>(type: "integer", nullable: false),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    cancel_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    encounter_id = table.Column<Guid>(type: "uuid", nullable: true),
                    booked_by_id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "NULLIF(current_setting('app.user_id', true), '')::uuid"),
                    booked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_appointments", x => x.id);
                    table.CheckConstraint("ck_appointments_cancelled", "(status = 'cancelled') = (cancel_reason IS NOT NULL)");
                    table.CheckConstraint("ck_appointments_minutes", "minutes BETWEEN 5 AND 240");
                    table.CheckConstraint("ck_appointments_status", "status IN ('booked', 'cancelled', 'fulfilled', 'noshow')");
                    table.CheckConstraint("ck_appointments_visit", "(status = 'fulfilled') = (encounter_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_appointments_encounters_encounter_id",
                        column: x => x.encounter_id,
                        principalSchema: "ehr",
                        principalTable: "encounters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_appointments_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "ehr",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_appointments_staff_users_booked_by_id",
                        column: x => x.booked_by_id,
                        principalSchema: "ehr",
                        principalTable: "staff_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_appointments_staff_users_clinician_id",
                        column: x => x.clinician_id,
                        principalSchema: "ehr",
                        principalTable: "staff_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_appointments_booked_by_id",
                schema: "ehr",
                table: "appointments",
                column: "booked_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_appointments_clinician_id_starts_at",
                schema: "ehr",
                table: "appointments",
                columns: new[] { "clinician_id", "starts_at" });

            migrationBuilder.CreateIndex(
                name: "ix_appointments_encounter_id",
                schema: "ehr",
                table: "appointments",
                column: "encounter_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_appointments_patient_id_starts_at",
                schema: "ehr",
                table: "appointments",
                columns: new[] { "patient_id", "starts_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "appointments",
                schema: "ehr");
        }
    }
}
