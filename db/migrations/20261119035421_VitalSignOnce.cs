using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ehr.Migrations
{
    /// <inheritdoc />
    public partial class VitalSignOnce : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ux_observations_vital_sign_once",
                schema: "ehr",
                table: "observations",
                columns: new[] { "patient_id", "code", "effective_at", "encounter_id" },
                unique: true,
                filter: "category = 'vital-signs'")
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_observations_vital_sign_once",
                schema: "ehr",
                table: "observations");
        }
    }
}
