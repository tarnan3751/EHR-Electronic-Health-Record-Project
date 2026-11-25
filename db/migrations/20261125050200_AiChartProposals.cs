using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ehr.Migrations
{
    /// <inheritdoc />
    public partial class AiChartProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_chart_proposals",
                schema: "ehr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    review_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<short>(type: "smallint", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    allergy_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_chart_proposals", x => x.id);
                    table.CheckConstraint("ck_ai_chart_proposals_decision", "(state = 'pending') = (decided_at IS NULL) AND (decided_at IS NULL) = (decided_by IS NULL) AND (state = 'ordered') = (order_id IS NOT NULL) AND (state = 'recorded') = (allergy_id IS NOT NULL) AND (order_id IS NULL OR kind = 'order') AND (allergy_id IS NULL OR kind = 'allergy_addition')");
                    table.CheckConstraint("ck_ai_chart_proposals_kind", "kind IN ('order', 'allergy_addition')");
                    table.CheckConstraint("ck_ai_chart_proposals_ordinal", "ordinal BETWEEN 0 AND 19");
                    table.CheckConstraint("ck_ai_chart_proposals_state", "state IN ('pending', 'ordered', 'recorded', 'dismissed')");
                    table.ForeignKey(
                        name: "fk_ai_chart_proposals_ai_note_reviews_review_id_patient_id",
                        columns: x => new { x.review_id, x.patient_id },
                        principalSchema: "ehr",
                        principalTable: "ai_note_reviews",
                        principalColumns: new[] { "id", "patient_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ai_chart_proposals_allergy_intolerances_allergy_id",
                        column: x => x.allergy_id,
                        principalSchema: "ehr",
                        principalTable: "allergy_intolerances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ai_chart_proposals_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "ehr",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ai_chart_proposals_staff_users_decided_by",
                        column: x => x.decided_by,
                        principalSchema: "ehr",
                        principalTable: "staff_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_chart_proposals_allergy_id",
                schema: "ehr",
                table: "ai_chart_proposals",
                column: "allergy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ai_chart_proposals_decided_by",
                schema: "ehr",
                table: "ai_chart_proposals",
                column: "decided_by");

            migrationBuilder.CreateIndex(
                name: "ix_ai_chart_proposals_order_id",
                schema: "ehr",
                table: "ai_chart_proposals",
                column: "order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ai_chart_proposals_review_id_ordinal",
                schema: "ehr",
                table: "ai_chart_proposals",
                columns: new[] { "review_id", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ai_chart_proposals_review_id_patient_id",
                schema: "ehr",
                table: "ai_chart_proposals",
                columns: new[] { "review_id", "patient_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_chart_proposals",
                schema: "ehr");
        }
    }
}
