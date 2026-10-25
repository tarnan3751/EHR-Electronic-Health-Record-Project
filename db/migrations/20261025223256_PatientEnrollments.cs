using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ehr.Migrations
{
    /// <inheritdoc />
    public partial class PatientEnrollments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "enrollment_code_expires_at",
                schema: "ehr",
                table: "patients");

            migrationBuilder.DropColumn(
                name: "enrollment_code_hash",
                schema: "ehr",
                table: "patients");

            migrationBuilder.CreateTable(
                name: "patient_enrollments",
                schema: "ehr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    issued_by = table.Column<Guid>(type: "uuid", nullable: true),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    redeemed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_patient_enrollments", x => x.id);
                    table.ForeignKey(
                        name: "fk_patient_enrollments_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "ehr",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_patient_enrollments_code_hash",
                schema: "ehr",
                table: "patient_enrollments",
                column: "code_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_patient_enrollments_patient_id",
                schema: "ehr",
                table: "patient_enrollments",
                column: "patient_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "patient_enrollments",
                schema: "ehr");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "enrollment_code_expires_at",
                schema: "ehr",
                table: "patients",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "enrollment_code_hash",
                schema: "ehr",
                table: "patients",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }
    }
}
