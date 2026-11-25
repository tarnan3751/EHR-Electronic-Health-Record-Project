using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ehr.Migrations
{
    /// <inheritdoc />
    public partial class ReferenceTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "common",
                schema: "ehr",
                table: "order_catalog_items",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "standard_code",
                schema: "ehr",
                table: "order_catalog_items",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "standard_system",
                schema: "ehr",
                table: "order_catalog_items",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "display_key",
                schema: "ehr",
                table: "order_catalog_items",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                computedColumnSql: "lower(display)",
                stored: true);

            migrationBuilder.CreateTable(
                name: "drug_class_cross_reactions",
                schema: "ehr",
                columns: table => new
                {
                    class_a = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    class_b = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    source = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_drug_class_cross_reactions", x => new { x.class_a, x.class_b });
                });

            migrationBuilder.CreateTable(
                name: "drug_classes",
                schema: "ehr",
                columns: table => new
                {
                    ingredient_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    class_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_drug_classes", x => new { x.ingredient_code, x.class_name });
                });

            migrationBuilder.CreateTable(
                name: "drug_ingredients",
                schema: "ehr",
                columns: table => new
                {
                    product_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ingredient_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_drug_ingredients", x => new { x.product_code, x.ingredient_code });
                });

            migrationBuilder.CreateTable(
                name: "drug_interactions",
                schema: "ehr",
                columns: table => new
                {
                    ingredient_a = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ingredient_b = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_drug_interactions", x => new { x.ingredient_a, x.ingredient_b });
                    table.CheckConstraint("ck_drug_interactions_level", "level IN ('major', 'moderate', 'minor')");
                    table.CheckConstraint("ck_drug_interactions_order", "ingredient_a < ingredient_b");
                });

            migrationBuilder.CreateTable(
                name: "observation_codes",
                schema: "ehr",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    display = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    unit = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    reference_low = table.Column<decimal>(type: "numeric", nullable: true),
                    reference_high = table.Column<decimal>(type: "numeric", nullable: true),
                    range_source = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    copyright = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_observation_codes", x => x.code);
                    table.CheckConstraint("ck_observation_codes_kind", "kind IN ('lab', 'vital', 'survey')");
                });

            migrationBuilder.CreateTable(
                name: "questionnaires",
                schema: "ehr",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    definition = table.Column<string>(type: "jsonb", nullable: false),
                    copyright = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_questionnaires", x => x.code);
                    table.CheckConstraint("ck_questionnaires_definition", "jsonb_typeof(definition) = 'object'");
                });

            migrationBuilder.CreateTable(
                name: "order_result_codes",
                schema: "ehr",
                columns: table => new
                {
                    catalog_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    observation_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_result_codes", x => new { x.catalog_code, x.observation_code });
                    table.ForeignKey(
                        name: "fk_order_result_codes_observation_codes_observation_code",
                        column: x => x.observation_code,
                        principalSchema: "ehr",
                        principalTable: "observation_codes",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_order_result_codes_order_catalog_items_catalog_code",
                        column: x => x.catalog_code,
                        principalSchema: "ehr",
                        principalTable: "order_catalog_items",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                schema: "ehr",
                table: "order_catalog_items",
                keyColumn: "code",
                keyValue: "SYN-IMG-001",
                columns: new[] { "common", "standard_code", "standard_system" },
                values: new object[] { true, null, null });

            migrationBuilder.UpdateData(
                schema: "ehr",
                table: "order_catalog_items",
                keyColumn: "code",
                keyValue: "SYN-IMG-002",
                columns: new[] { "common", "standard_code", "standard_system" },
                values: new object[] { true, null, null });

            migrationBuilder.UpdateData(
                schema: "ehr",
                table: "order_catalog_items",
                keyColumn: "code",
                keyValue: "SYN-IMG-003",
                columns: new[] { "common", "standard_code", "standard_system" },
                values: new object[] { true, null, null });

            migrationBuilder.UpdateData(
                schema: "ehr",
                table: "order_catalog_items",
                keyColumn: "code",
                keyValue: "SYN-IMG-004",
                columns: new[] { "common", "standard_code", "standard_system" },
                values: new object[] { true, null, null });

            migrationBuilder.UpdateData(
                schema: "ehr",
                table: "order_catalog_items",
                keyColumn: "code",
                keyValue: "SYN-IMG-005",
                columns: new[] { "common", "standard_code", "standard_system" },
                values: new object[] { true, null, null });

            migrationBuilder.UpdateData(
                schema: "ehr",
                table: "order_catalog_items",
                keyColumn: "code",
                keyValue: "SYN-IMG-006",
                columns: new[] { "common", "standard_code", "standard_system" },
                values: new object[] { true, null, null });

            migrationBuilder.UpdateData(
                schema: "ehr",
                table: "order_catalog_items",
                keyColumn: "code",
                keyValue: "SYN-LAB-001",
                columns: new[] { "common", "standard_code", "standard_system" },
                values: new object[] { true, "58410-2", "loinc" });

            migrationBuilder.UpdateData(
                schema: "ehr",
                table: "order_catalog_items",
                keyColumn: "code",
                keyValue: "SYN-LAB-002",
                columns: new[] { "common", "standard_code", "standard_system" },
                values: new object[] { true, null, null });

            migrationBuilder.UpdateData(
                schema: "ehr",
                table: "order_catalog_items",
                keyColumn: "code",
                keyValue: "SYN-LAB-003",
                columns: new[] { "common", "standard_code", "standard_system" },
                values: new object[] { true, "24323-8", "loinc" });

            migrationBuilder.UpdateData(
                schema: "ehr",
                table: "order_catalog_items",
                keyColumn: "code",
                keyValue: "SYN-LAB-004",
                columns: new[] { "common", "standard_code", "standard_system" },
                values: new object[] { true, "57698-3", "loinc" });

            migrationBuilder.UpdateData(
                schema: "ehr",
                table: "order_catalog_items",
                keyColumn: "code",
                keyValue: "SYN-LAB-005",
                columns: new[] { "common", "standard_code", "standard_system" },
                values: new object[] { true, null, null });

            migrationBuilder.UpdateData(
                schema: "ehr",
                table: "order_catalog_items",
                keyColumn: "code",
                keyValue: "SYN-LAB-006",
                columns: new[] { "common", "standard_code", "standard_system" },
                values: new object[] { true, "3016-3", "loinc" });

            migrationBuilder.UpdateData(
                schema: "ehr",
                table: "order_catalog_items",
                keyColumn: "code",
                keyValue: "SYN-LAB-007",
                columns: new[] { "common", "standard_code", "standard_system" },
                values: new object[] { true, "24356-8", "loinc" });

            migrationBuilder.UpdateData(
                schema: "ehr",
                table: "order_catalog_items",
                keyColumn: "code",
                keyValue: "SYN-LAB-008",
                columns: new[] { "common", "standard_code", "standard_system" },
                values: new object[] { true, "62292-8", "loinc" });

            migrationBuilder.UpdateData(
                schema: "ehr",
                table: "order_catalog_items",
                keyColumn: "code",
                keyValue: "SYN-MED-001",
                columns: new[] { "common", "standard_code", "standard_system" },
                values: new object[] { true, "314076", "rxnorm" });

            migrationBuilder.UpdateData(
                schema: "ehr",
                table: "order_catalog_items",
                keyColumn: "code",
                keyValue: "SYN-MED-002",
                columns: new[] { "common", "standard_code", "standard_system" },
                values: new object[] { true, "861007", "rxnorm" });

            migrationBuilder.UpdateData(
                schema: "ehr",
                table: "order_catalog_items",
                keyColumn: "code",
                keyValue: "SYN-MED-003",
                columns: new[] { "common", "standard_code", "standard_system" },
                values: new object[] { true, "617310", "rxnorm" });

            migrationBuilder.UpdateData(
                schema: "ehr",
                table: "order_catalog_items",
                keyColumn: "code",
                keyValue: "SYN-MED-004",
                columns: new[] { "common", "standard_code", "standard_system" },
                values: new object[] { true, "197361", "rxnorm" });

            migrationBuilder.UpdateData(
                schema: "ehr",
                table: "order_catalog_items",
                keyColumn: "code",
                keyValue: "SYN-MED-005",
                columns: new[] { "common", "standard_code", "standard_system" },
                values: new object[] { true, "308191", "rxnorm" });

            migrationBuilder.UpdateData(
                schema: "ehr",
                table: "order_catalog_items",
                keyColumn: "code",
                keyValue: "SYN-MED-006",
                columns: new[] { "common", "standard_code", "standard_system" },
                values: new object[] { true, null, null });

            migrationBuilder.UpdateData(
                schema: "ehr",
                table: "order_catalog_items",
                keyColumn: "code",
                keyValue: "SYN-MED-007",
                columns: new[] { "common", "standard_code", "standard_system" },
                values: new object[] { true, "312941", "rxnorm" });

            migrationBuilder.UpdateData(
                schema: "ehr",
                table: "order_catalog_items",
                keyColumn: "code",
                keyValue: "SYN-MED-008",
                columns: new[] { "common", "standard_code", "standard_system" },
                values: new object[] { true, "197805", "rxnorm" });

            migrationBuilder.UpdateData(
                schema: "ehr",
                table: "order_catalog_items",
                keyColumn: "code",
                keyValue: "SYN-REF-001",
                columns: new[] { "common", "standard_code", "standard_system" },
                values: new object[] { true, null, null });

            migrationBuilder.UpdateData(
                schema: "ehr",
                table: "order_catalog_items",
                keyColumn: "code",
                keyValue: "SYN-REF-002",
                columns: new[] { "common", "standard_code", "standard_system" },
                values: new object[] { true, null, null });

            migrationBuilder.UpdateData(
                schema: "ehr",
                table: "order_catalog_items",
                keyColumn: "code",
                keyValue: "SYN-REF-003",
                columns: new[] { "common", "standard_code", "standard_system" },
                values: new object[] { true, null, null });

            migrationBuilder.UpdateData(
                schema: "ehr",
                table: "order_catalog_items",
                keyColumn: "code",
                keyValue: "SYN-REF-004",
                columns: new[] { "common", "standard_code", "standard_system" },
                values: new object[] { true, null, null });

            migrationBuilder.UpdateData(
                schema: "ehr",
                table: "order_catalog_items",
                keyColumn: "code",
                keyValue: "SYN-REF-005",
                columns: new[] { "common", "standard_code", "standard_system" },
                values: new object[] { true, null, null });

            migrationBuilder.UpdateData(
                schema: "ehr",
                table: "order_catalog_items",
                keyColumn: "code",
                keyValue: "SYN-REF-006",
                columns: new[] { "common", "standard_code", "standard_system" },
                values: new object[] { true, null, null });

            migrationBuilder.UpdateData(
                schema: "ehr",
                table: "order_catalog_items",
                keyColumn: "code",
                keyValue: "SYN-REF-007",
                columns: new[] { "common", "standard_code", "standard_system" },
                values: new object[] { true, null, null });

            migrationBuilder.CreateIndex(
                name: "ix_order_catalog_items_display",
                schema: "ehr",
                table: "order_catalog_items",
                column: "display")
                .Annotation("Npgsql:IndexMethod", "GIN")
                .Annotation("Npgsql:TsVectorConfig", "simple");

            migrationBuilder.CreateIndex(
                name: "ix_order_catalog_items_type_display_key",
                schema: "ehr",
                table: "order_catalog_items",
                columns: new[] { "type", "display_key" })
                .Annotation("Npgsql:IndexOperators", new[] { null, "text_pattern_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_drug_classes_class_name",
                schema: "ehr",
                table: "drug_classes",
                column: "class_name");

            migrationBuilder.CreateIndex(
                name: "ix_drug_interactions_ingredient_b",
                schema: "ehr",
                table: "drug_interactions",
                column: "ingredient_b");

            migrationBuilder.CreateIndex(
                name: "ix_order_result_codes_observation_code",
                schema: "ehr",
                table: "order_result_codes",
                column: "observation_code");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "drug_class_cross_reactions",
                schema: "ehr");

            migrationBuilder.DropTable(
                name: "drug_classes",
                schema: "ehr");

            migrationBuilder.DropTable(
                name: "drug_ingredients",
                schema: "ehr");

            migrationBuilder.DropTable(
                name: "drug_interactions",
                schema: "ehr");

            migrationBuilder.DropTable(
                name: "order_result_codes",
                schema: "ehr");

            migrationBuilder.DropTable(
                name: "questionnaires",
                schema: "ehr");

            migrationBuilder.DropTable(
                name: "observation_codes",
                schema: "ehr");

            migrationBuilder.DropIndex(
                name: "ix_order_catalog_items_display",
                schema: "ehr",
                table: "order_catalog_items");

            migrationBuilder.DropIndex(
                name: "ix_order_catalog_items_type_display_key",
                schema: "ehr",
                table: "order_catalog_items");

            migrationBuilder.DropColumn(
                name: "display_key",
                schema: "ehr",
                table: "order_catalog_items");

            migrationBuilder.DropColumn(
                name: "common",
                schema: "ehr",
                table: "order_catalog_items");

            migrationBuilder.DropColumn(
                name: "standard_code",
                schema: "ehr",
                table: "order_catalog_items");

            migrationBuilder.DropColumn(
                name: "standard_system",
                schema: "ehr",
                table: "order_catalog_items");
        }
    }
}
