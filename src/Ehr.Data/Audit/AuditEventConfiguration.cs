using Ehr.Data.Patients;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ehr.Data.Audit;

// Maps ehr.audit_events. EhrDbContext picks this up with ApplyConfigurationsFromAssembly.
public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> auditEvent)
    {
        // A part only on a read, and only one of the list (AuditParts). The migration that added it (AuditEventParts) adds
        // the check without reading the table, which every row met then, so a large log isn't locked while it's read.
        auditEvent.ToTable("audit_events", table => table.HasCheckConstraint(
            "ck_audit_events_part",
            $"part IS NULL OR action IN ({Codes.List(AuditActions.Reads)}) AND part IN ({Codes.List(AuditParts.All)})"));
        // Set by the writer, never by the database (see AuditEvent): no generated columns, so inserts need no RETURNING.
        auditEvent.Property(e => e.Id).ValueGeneratedNever();
        auditEvent.Property(e => e.Action).HasMaxLength(40);
        auditEvent.Property(e => e.TableName).HasMaxLength(63);
        auditEvent.Property(e => e.Source).HasMaxLength(10);
        auditEvent.Property(e => e.Reason).HasMaxLength(500);
        auditEvent.Property(e => e.Details).HasColumnType("jsonb");
        auditEvent.Property(e => e.Part).HasMaxLength(24);

        // Who looked at this patient; what did this person do.
        auditEvent.HasIndex(e => new { e.PatientId, e.OccurredAt });
        auditEvent.HasIndex(e => new { e.ActorId, e.OccurredAt });
    }
}
