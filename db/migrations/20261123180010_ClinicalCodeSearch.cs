using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ehr.Migrations
{
    /// <inheritdoc />
    public partial class ClinicalCodeSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "display",
                schema: "ehr",
                table: "medication_statements",
                type: "character varying(300)",
                maxLength: 300,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AlterColumn<string>(
                name: "display",
                schema: "ehr",
                table: "conditions",
                type: "character varying(300)",
                maxLength: 300,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AlterColumn<string>(
                name: "display",
                schema: "ehr",
                table: "allergy_intolerances",
                type: "character varying(300)",
                maxLength: 300,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.CreateIndex(
                name: "ix_clinical_codes_display",
                schema: "ehr",
                table: "clinical_codes",
                column: "display")
                .Annotation("Npgsql:IndexMethod", "GIN")
                .Annotation("Npgsql:TsVectorConfig", "simple");

            migrationBuilder.CreateIndex(
                name: "ix_clinical_codes_kind_code",
                schema: "ehr",
                table: "clinical_codes",
                columns: new[] { "kind", "code" })
                .Annotation("Npgsql:IndexOperators", new[] { null, "text_pattern_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_clinical_codes_display",
                schema: "ehr",
                table: "clinical_codes");

            migrationBuilder.DropIndex(
                name: "ix_clinical_codes_kind_code",
                schema: "ehr",
                table: "clinical_codes");

            migrationBuilder.AlterColumn<string>(
                name: "display",
                schema: "ehr",
                table: "medication_statements",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(300)",
                oldMaxLength: 300);

            migrationBuilder.AlterColumn<string>(
                name: "display",
                schema: "ehr",
                table: "conditions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(300)",
                oldMaxLength: 300);

            migrationBuilder.AlterColumn<string>(
                name: "display",
                schema: "ehr",
                table: "allergy_intolerances",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(300)",
                oldMaxLength: 300);
        }
    }
}
