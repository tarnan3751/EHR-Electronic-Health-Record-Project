using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ehr.Migrations
{
    /// <inheritdoc />
    public partial class UnroutedResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_inbox_items_kind",
                schema: "ehr",
                table: "inbox_items");

            migrationBuilder.AddCheckConstraint(
                name: "ck_inbox_items_kind",
                schema: "ehr",
                table: "inbox_items",
                sql: "kind IN ('message', 'result', 'refill-request', 'emergency-access-review', 'addendum', 'summary-withdrawn', 'form-alert', 'unrouted-result')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_inbox_items_kind",
                schema: "ehr",
                table: "inbox_items");

            migrationBuilder.AddCheckConstraint(
                name: "ck_inbox_items_kind",
                schema: "ehr",
                table: "inbox_items",
                sql: "kind IN ('message', 'result', 'refill-request', 'emergency-access-review', 'addendum', 'summary-withdrawn', 'form-alert')");
        }
    }
}
