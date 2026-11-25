using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ehr.Migrations
{
    /// <inheritdoc />
    public partial class OrderResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "result_acknowledgments",
                schema: "ehr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    acknowledged_by = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "NULLIF(current_setting('app.user_id', true), '')::uuid"),
                    acknowledged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_result_acknowledgments", x => x.id);
                    table.ForeignKey(
                        name: "fk_result_acknowledgments_orders_order_id_patient_id",
                        columns: x => new { x.order_id, x.patient_id },
                        principalSchema: "ehr",
                        principalTable: "orders",
                        principalColumns: new[] { "id", "patient_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_result_acknowledgments_staff_users_acknowledged_by",
                        column: x => x.acknowledged_by,
                        principalSchema: "ehr",
                        principalTable: "staff_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_observations_order_id_code",
                schema: "ehr",
                table: "observations",
                columns: new[] { "order_id", "code" },
                unique: true,
                filter: "order_id IS NOT NULL AND code IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_result_acknowledgments_acknowledged_by",
                schema: "ehr",
                table: "result_acknowledgments",
                column: "acknowledged_by");

            migrationBuilder.CreateIndex(
                name: "ix_result_acknowledgments_order_id",
                schema: "ehr",
                table: "result_acknowledgments",
                column: "order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_result_acknowledgments_order_id_patient_id",
                schema: "ehr",
                table: "result_acknowledgments",
                columns: new[] { "order_id", "patient_id" });

            migrationBuilder.CreateIndex(
                name: "ix_result_acknowledgments_patient_id",
                schema: "ehr",
                table: "result_acknowledgments",
                column: "patient_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "result_acknowledgments",
                schema: "ehr");

            migrationBuilder.DropIndex(
                name: "ix_observations_order_id_code",
                schema: "ehr",
                table: "observations");
        }
    }
}
