using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ehr.Migrations
{
    /// <inheritdoc />
    public partial class ClinicalNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "assessment",
                schema: "ehr",
                table: "clinical_drafts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "has_unreviewed_proposals",
                schema: "ehr",
                table: "clinical_drafts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "history",
                schema: "ehr",
                table: "clinical_drafts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "plan",
                schema: "ehr",
                table: "clinical_drafts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "structured_version",
                schema: "ehr",
                table: "clinical_drafts",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "clinical_notes",
                schema: "ehr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    draft_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    draft_version = table.Column<long>(type: "bigint", nullable: false),
                    history = table.Column<string>(type: "text", nullable: false),
                    assessment = table.Column<string>(type: "text", nullable: false),
                    plan = table.Column<string>(type: "text", nullable: false),
                    signer_id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "NULLIF(current_setting('app.user_id', true), '')::uuid"),
                    signed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clinical_notes", x => x.id);
                    table.ForeignKey(
                        name: "fk_clinical_notes_clinical_drafts_draft_id",
                        column: x => x.draft_id,
                        principalSchema: "ehr",
                        principalTable: "clinical_drafts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_clinical_notes_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "ehr",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_clinical_notes_staff_users_signer_id",
                        column: x => x.signer_id,
                        principalSchema: "ehr",
                        principalTable: "staff_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_clinical_notes_draft_id",
                schema: "ehr",
                table: "clinical_notes",
                column: "draft_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_clinical_notes_patient_id_signed_at",
                schema: "ehr",
                table: "clinical_notes",
                columns: new[] { "patient_id", "signed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_clinical_notes_signer_id",
                schema: "ehr",
                table: "clinical_notes",
                column: "signer_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "clinical_notes",
                schema: "ehr");

            migrationBuilder.DropColumn(
                name: "assessment",
                schema: "ehr",
                table: "clinical_drafts");

            migrationBuilder.DropColumn(
                name: "has_unreviewed_proposals",
                schema: "ehr",
                table: "clinical_drafts");

            migrationBuilder.DropColumn(
                name: "history",
                schema: "ehr",
                table: "clinical_drafts");

            migrationBuilder.DropColumn(
                name: "plan",
                schema: "ehr",
                table: "clinical_drafts");

            migrationBuilder.DropColumn(
                name: "structured_version",
                schema: "ehr",
                table: "clinical_drafts");
        }
    }
}
