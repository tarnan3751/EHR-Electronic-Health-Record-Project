using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ehr.Migrations
{
    /// <inheritdoc />
    public partial class InteractionSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_drug_interactions",
                schema: "ehr",
                table: "drug_interactions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_drug_interactions_level",
                schema: "ehr",
                table: "drug_interactions");

            migrationBuilder.AddColumn<string>(
                name: "source",
                schema: "ehr",
                table: "drug_interactions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "ddinter");

            migrationBuilder.AddColumn<string>(
                name: "citation",
                schema: "ehr",
                table: "drug_interactions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "detail",
                schema: "ehr",
                table: "drug_interactions",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "pk_drug_interactions",
                schema: "ehr",
                table: "drug_interactions",
                columns: new[] { "ingredient_a", "ingredient_b", "source" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_drug_interactions_level",
                schema: "ehr",
                table: "drug_interactions",
                sql: "level IN ('major', 'moderate', 'minor', 'high-priority')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_drug_interactions_source",
                schema: "ehr",
                table: "drug_interactions",
                sql: "source IN ('ddinter', 'onc', 'fda-label')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_drug_interactions",
                schema: "ehr",
                table: "drug_interactions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_drug_interactions_level",
                schema: "ehr",
                table: "drug_interactions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_drug_interactions_source",
                schema: "ehr",
                table: "drug_interactions");

            migrationBuilder.DropColumn(
                name: "source",
                schema: "ehr",
                table: "drug_interactions");

            migrationBuilder.DropColumn(
                name: "citation",
                schema: "ehr",
                table: "drug_interactions");

            migrationBuilder.DropColumn(
                name: "detail",
                schema: "ehr",
                table: "drug_interactions");

            migrationBuilder.AddPrimaryKey(
                name: "pk_drug_interactions",
                schema: "ehr",
                table: "drug_interactions",
                columns: new[] { "ingredient_a", "ingredient_b" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_drug_interactions_level",
                schema: "ehr",
                table: "drug_interactions",
                sql: "level IN ('major', 'moderate', 'minor')");
        }
    }
}
