using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ehr.Migrations
{
    /// <inheritdoc />
    public partial class OrderSafety : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "safety_override_reason",
                schema: "ehr",
                table: "orders",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "safety_warnings",
                schema: "ehr",
                table: "orders",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "safety_override_reason",
                schema: "ehr",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "safety_warnings",
                schema: "ehr",
                table: "orders");
        }
    }
}
