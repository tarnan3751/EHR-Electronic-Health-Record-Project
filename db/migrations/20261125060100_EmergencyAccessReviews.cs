using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ehr.Migrations
{
    /// <inheritdoc />
    public partial class EmergencyAccessReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "emergency_access_reviews",
                schema: "ehr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reviewer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_emergency_access_reviews", x => x.id);
                    table.CheckConstraint("ck_emergency_access_reviews_note", "char_length(note) BETWEEN 10 AND 500");
                    table.CheckConstraint("ck_emergency_access_reviews_outcome", "outcome IN ('appropriate', 'not-appropriate')");
                    table.ForeignKey(
                        name: "fk_emergency_access_reviews_break_glass_accesses_id",
                        column: x => x.id,
                        principalSchema: "ehr",
                        principalTable: "break_glass_accesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_emergency_access_reviews_staff_users_reviewer_id",
                        column: x => x.reviewer_id,
                        principalSchema: "ehr",
                        principalTable: "staff_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_emergency_access_reviews_reviewer_id",
                schema: "ehr",
                table: "emergency_access_reviews",
                column: "reviewer_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_break_glass",
                schema: "ehr",
                table: "audit_events",
                columns: new[] { "occurred_at", "id" },
                descending: new bool[0],
                filter: "action = 'break_glass'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Written by hand. Undone only while no emergency access has been reviewed: each review is a record of the
            // audit. The tables' owner reads the reviews through its own policy. Then everything of this step in
            // db/policies/72-emergency-access-review.sql that would outlive the table goes, as AmbientListening's Down
            // removes its own: the trigger on emergency accesses that delivers them to administrators' inboxes, whose
            // items nothing could close once the reviews are gone, and the step's functions. The migration job creates
            // them all again after Up. 70-inbox.sql's refusal to tick such an item off stays, since no item is made.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM ehr.emergency_access_reviews) THEN
                        RAISE EXCEPTION 'Migration EmergencyAccessReviews is not undone: emergency accesses have been reviewed, and each review is a record'
                            USING ERRCODE = '55000';
                    END IF;
                END
                $$;
                DROP TRIGGER IF EXISTS break_glass_accesses_review_items ON ehr.break_glass_accesses;
                DROP FUNCTION IF EXISTS ehr.deliver_emergency_access_review();
                DROP FUNCTION IF EXISTS ehr.emergency_access_for_review(uuid);
                DROP FUNCTION IF EXISTS ehr.emergency_access_reviewers(uuid);
                DROP TRIGGER IF EXISTS emergency_access_reviews_guard ON ehr.emergency_access_reviews;
                DROP TRIGGER IF EXISTS emergency_access_reviews_immutable ON ehr.emergency_access_reviews;
                DROP TRIGGER IF EXISTS emergency_access_reviews_close_items ON ehr.emergency_access_reviews;
                DROP FUNCTION IF EXISTS ehr.guard_emergency_access_review();
                DROP FUNCTION IF EXISTS ehr.reject_emergency_access_review_change();
                DROP FUNCTION IF EXISTS ehr.close_emergency_access_review_items();
                """);

            migrationBuilder.DropIndex(
                name: "ix_audit_events_break_glass",
                schema: "ehr",
                table: "audit_events");

            migrationBuilder.DropTable(
                name: "emergency_access_reviews",
                schema: "ehr");
        }
    }
}
