using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ehr.Migrations
{
    /// <inheritdoc />
    public partial class AuditEventParts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Written by hand, in part, so the audit log is held as briefly as it can be (step 13c2). Every PHI read
            // inserts an event, so while this migration waits for its lock on ehr.audit_events, or holds it, those
            // inserts wait behind it, on the cloud copy too, where the lock is replayed. It waits 5 seconds at most: a
            // lock it can't have by then fails the migration job, which can run again, and the inserts go on (the
            // timeout is set for the transaction the migration runs in, and nothing beyond it). The index first:
            // building it holds back only changes to the panels, though they then wait through the log's wait too, so
            // "Add to my patients" can wait as long. Then the log, whose lock lasts until the commit: a column with no
            // default, which changes the catalog and rewrites nothing, and the check added NOT VALID, so the table
            // isn't read for it. It holds for every row written from now on; every row already there has no part, which
            // it allows, so reading them would prove nothing.
            migrationBuilder.Sql("SELECT set_config('lock_timeout', '5s', true);");

            migrationBuilder.CreateIndex(
                name: "ix_patient_assignments_history",
                schema: "ehr",
                table: "patient_assignments",
                columns: new[] { "patient_id", "clinician_id" });

            migrationBuilder.AddColumn<string>(
                name: "part",
                schema: "ehr",
                table: "audit_events",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.Sql("""
                ALTER TABLE ehr.audit_events ADD CONSTRAINT ck_audit_events_part
                    CHECK (part IS NULL
                           OR action IN ('read', 'read_sensitive', 'download', 'download_sensitive')
                           AND part IN ('chart', 'lists', 'orders', 'results', 'vitals', 'search', 'panel',
                                        'schedule', 'inbox', 'appointments', 'home', 'conversations', 'access',
                                        'visit-note', 'note-draft', 'offline', 'fhir', 'summaries', 'printable',
                                        'download', 'views', 'refills', 'forms')) NOT VALID;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_patient_assignments_history",
                schema: "ehr",
                table: "patient_assignments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_events_part",
                schema: "ehr",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "part",
                schema: "ehr",
                table: "audit_events");
        }
    }
}
