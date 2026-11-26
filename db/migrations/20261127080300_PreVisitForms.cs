using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ehr.Migrations
{
    /// <inheritdoc />
    public partial class PreVisitForms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_inbox_items_kind",
                schema: "ehr",
                table: "inbox_items");

            migrationBuilder.AddColumn<bool>(
                name: "sensitive",
                schema: "ehr",
                table: "inbox_items",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "questionnaire_requests",
                schema: "ehr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    appointment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    clinician_id = table.Column<Guid>(type: "uuid", nullable: false),
                    questionnaire_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    definition = table.Column<string>(type: "jsonb", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "NULLIF(current_setting('app.user_id', true), '')::uuid"),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    sensitive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_questionnaire_requests", x => x.id);
                    table.CheckConstraint("ck_questionnaire_requests_definition", "jsonb_typeof(definition) = 'object'");
                    table.CheckConstraint("ck_questionnaire_requests_sensitive", "sensitive");
                    table.ForeignKey(
                        name: "fk_questionnaire_requests_appointments_appointment_id",
                        column: x => x.appointment_id,
                        principalSchema: "ehr",
                        principalTable: "appointments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_questionnaire_requests_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "ehr",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_questionnaire_requests_questionnaires_questionnaire_code",
                        column: x => x.questionnaire_code,
                        principalSchema: "ehr",
                        principalTable: "questionnaires",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_questionnaire_requests_staff_users_clinician_id",
                        column: x => x.clinician_id,
                        principalSchema: "ehr",
                        principalTable: "staff_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_questionnaire_requests_staff_users_requested_by",
                        column: x => x.requested_by,
                        principalSchema: "ehr",
                        principalTable: "staff_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "questionnaire_responses",
                schema: "ehr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    questionnaire_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    clinician_id = table.Column<Guid>(type: "uuid", nullable: false),
                    answers = table.Column<string>(type: "jsonb", nullable: false),
                    score = table.Column<int>(type: "integer", nullable: true),
                    band = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    follow_up = table.Column<bool>(type: "boolean", nullable: false),
                    alert = table.Column<bool>(type: "boolean", nullable: false),
                    answered_by = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "NULLIF(current_setting('app.user_id', true), '')::uuid"),
                    proxy_grant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    answered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    sensitive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_questionnaire_responses", x => x.id);
                    table.CheckConstraint("ck_questionnaire_responses_answers", "jsonb_typeof(answers) = 'object'");
                    table.CheckConstraint("ck_questionnaire_responses_score", "score >= 0");
                    table.CheckConstraint("ck_questionnaire_responses_sensitive", "sensitive");
                    table.ForeignKey(
                        name: "fk_questionnaire_responses_patient_users_answered_by",
                        column: x => x.answered_by,
                        principalSchema: "ehr",
                        principalTable: "patient_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_questionnaire_responses_patients_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "ehr",
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_questionnaire_responses_proxy_grants_proxy_grant_id",
                        column: x => x.proxy_grant_id,
                        principalSchema: "ehr",
                        principalTable: "proxy_grants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_questionnaire_responses_questionnaire_requests_request_id",
                        column: x => x.request_id,
                        principalSchema: "ehr",
                        principalTable: "questionnaire_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_questionnaire_responses_questionnaires_questionnaire_code",
                        column: x => x.questionnaire_code,
                        principalSchema: "ehr",
                        principalTable: "questionnaires",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_questionnaire_responses_staff_users_clinician_id",
                        column: x => x.clinician_id,
                        principalSchema: "ehr",
                        principalTable: "staff_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_inbox_items_form_alert_sensitive",
                schema: "ehr",
                table: "inbox_items",
                sql: "kind <> 'form-alert' OR sensitive");

            migrationBuilder.AddCheckConstraint(
                name: "ck_inbox_items_kind",
                schema: "ehr",
                table: "inbox_items",
                sql: "kind IN ('message', 'result', 'refill-request', 'emergency-access-review', 'addendum', 'summary-withdrawn', 'form-alert')");

            migrationBuilder.CreateIndex(
                name: "ix_questionnaire_requests_appointment_id_questionnaire_code",
                schema: "ehr",
                table: "questionnaire_requests",
                columns: new[] { "appointment_id", "questionnaire_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_questionnaire_requests_clinician_id",
                schema: "ehr",
                table: "questionnaire_requests",
                column: "clinician_id");

            migrationBuilder.CreateIndex(
                name: "ix_questionnaire_requests_patient_id_requested_at",
                schema: "ehr",
                table: "questionnaire_requests",
                columns: new[] { "patient_id", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_questionnaire_requests_questionnaire_code",
                schema: "ehr",
                table: "questionnaire_requests",
                column: "questionnaire_code");

            migrationBuilder.CreateIndex(
                name: "ix_questionnaire_requests_requested_by",
                schema: "ehr",
                table: "questionnaire_requests",
                column: "requested_by");

            migrationBuilder.CreateIndex(
                name: "ix_questionnaire_responses_answered_by",
                schema: "ehr",
                table: "questionnaire_responses",
                column: "answered_by");

            migrationBuilder.CreateIndex(
                name: "ix_questionnaire_responses_clinician_id",
                schema: "ehr",
                table: "questionnaire_responses",
                column: "clinician_id");

            migrationBuilder.CreateIndex(
                name: "ix_questionnaire_responses_patient_id_answered_at",
                schema: "ehr",
                table: "questionnaire_responses",
                columns: new[] { "patient_id", "answered_at" });

            migrationBuilder.CreateIndex(
                name: "ix_questionnaire_responses_proxy_grant_id",
                schema: "ehr",
                table: "questionnaire_responses",
                column: "proxy_grant_id");

            migrationBuilder.CreateIndex(
                name: "ix_questionnaire_responses_questionnaire_code",
                schema: "ehr",
                table: "questionnaire_responses",
                column: "questionnaire_code");

            migrationBuilder.CreateIndex(
                name: "ix_questionnaire_responses_request_id",
                schema: "ehr",
                table: "questionnaire_responses",
                column: "request_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "questionnaire_responses",
                schema: "ehr");

            migrationBuilder.DropTable(
                name: "questionnaire_requests",
                schema: "ehr");

            migrationBuilder.DropCheckConstraint(
                name: "ck_inbox_items_form_alert_sensitive",
                schema: "ehr",
                table: "inbox_items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_inbox_items_kind",
                schema: "ehr",
                table: "inbox_items");

            migrationBuilder.DropColumn(
                name: "sensitive",
                schema: "ehr",
                table: "inbox_items");

            migrationBuilder.AddCheckConstraint(
                name: "ck_inbox_items_kind",
                schema: "ehr",
                table: "inbox_items",
                sql: "kind IN ('message', 'result', 'refill-request', 'emergency-access-review', 'addendum', 'summary-withdrawn')");
        }
    }
}
