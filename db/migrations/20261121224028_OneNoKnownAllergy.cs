using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ehr.Migrations
{
    /// <inheritdoc />
    public partial class OneNoKnownAllergy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A double click could record "No known allergies" twice before this index: keep each patient's first
            // active one and make the others inactive, so the index can be built. Forced row-level security hides
            // every row from the migration's role, so it is lifted for the statement and put back, as in ChartLists.
            // The update goes through the triggers like any other: the history and the write audit show it, made by
            // nobody signed in.
            migrationBuilder.Sql(
                """
                ALTER TABLE ehr.allergy_intolerances NO FORCE ROW LEVEL SECURITY;
                UPDATE ehr.allergy_intolerances SET clinical_status = 'inactive'
                WHERE code = 'no-known-allergy' AND clinical_status = 'active'
                  AND id NOT IN (
                      SELECT DISTINCT ON (patient_id) id FROM ehr.allergy_intolerances
                      WHERE code = 'no-known-allergy' AND clinical_status = 'active'
                      ORDER BY patient_id, recorded_at, id);
                DO $$
                BEGIN
                    IF (SELECT relrowsecurity FROM pg_class WHERE oid = 'ehr.allergy_intolerances'::regclass) THEN
                        ALTER TABLE ehr.allergy_intolerances FORCE ROW LEVEL SECURITY;
                    END IF;
                END
                $$;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_allergy_intolerances_one_no_known_allergy",
                schema: "ehr",
                table: "allergy_intolerances",
                column: "patient_id",
                unique: true,
                filter: "code = 'no-known-allergy' AND clinical_status = 'active'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_allergy_intolerances_one_no_known_allergy",
                schema: "ehr",
                table: "allergy_intolerances");
        }
    }
}
