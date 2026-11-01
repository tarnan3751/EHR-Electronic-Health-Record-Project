using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ehr.Data.Audit;

// Maps ehr.audit_events. EhrDbContext picks this up with ApplyConfigurationsFromAssembly.
public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> auditEvent)
    {
        auditEvent.ToTable("audit_events");
        // Set by the writer, never by the database (see AuditEvent): no generated columns, so inserts need no RETURNING.
        auditEvent.Property(e => e.Id).ValueGeneratedNever();
        auditEvent.Property(e => e.Action).HasMaxLength(40);
        auditEvent.Property(e => e.TableName).HasMaxLength(63);
        auditEvent.Property(e => e.Source).HasMaxLength(10);
        auditEvent.Property(e => e.Reason).HasMaxLength(500);
        auditEvent.Property(e => e.Details).HasColumnType("jsonb");

        // Who looked at this patient; what did this person do.
        auditEvent.HasIndex(e => new { e.PatientId, e.OccurredAt });
        auditEvent.HasIndex(e => new { e.ActorId, e.OccurredAt });
    }
}
