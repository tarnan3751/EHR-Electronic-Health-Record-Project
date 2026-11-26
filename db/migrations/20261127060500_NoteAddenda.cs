using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ehr.Migrations
{
    /// <inheritdoc />
    public partial class NoteAddenda : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "sensitive",
                schema: "ehr",
                table: "clinical_notes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_clinical_notes_id_patient_id",
                schema: "ehr",
                table: "clinical_notes",
                columns: new[] { "id", "patient_id" });

            migrationBuilder.CreateTable(
                name: "note_addenda",
                schema: "ehr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "NULLIF(current_setting('app.user_id', true), '')::uuid"),
                    text = table.Column<string>(type: "text", nullable: false),
                    signed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    sensitive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_note_addenda", x => x.id);
                    table.CheckConstraint("ck_note_addenda_text", "text ~ '[^[:space:]]' AND char_length(text) <= 16000");
                    table.ForeignKey(
                        name: "fk_note_addenda_clinical_notes_note_id_patient_id",
                        columns: x => new { x.note_id, x.patient_id },
                        principalSchema: "ehr",
                        principalTable: "clinical_notes",
                        principalColumns: new[] { "id", "patient_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_note_addenda_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "ehr",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_note_addenda_staff_users_author_id",
                        column: x => x.author_id,
                        principalSchema: "ehr",
                        principalTable: "staff_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_note_addenda_author_id",
                schema: "ehr",
                table: "note_addenda",
                column: "author_id");

            migrationBuilder.CreateIndex(
                name: "ix_note_addenda_note_id_patient_id",
                schema: "ehr",
                table: "note_addenda",
                columns: new[] { "note_id", "patient_id" });

            migrationBuilder.CreateIndex(
                name: "ix_note_addenda_note_id_signed_at",
                schema: "ehr",
                table: "note_addenda",
                columns: new[] { "note_id", "signed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_note_addenda_patient_id",
                schema: "ehr",
                table: "note_addenda",
                column: "patient_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "note_addenda",
                schema: "ehr");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_clinical_notes_id_patient_id",
                schema: "ehr",
                table: "clinical_notes");

            migrationBuilder.DropColumn(
                name: "sensitive",
                schema: "ehr",
                table: "clinical_notes");
        }
    }
}
