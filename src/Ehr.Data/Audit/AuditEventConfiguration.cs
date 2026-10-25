using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ehr.Data.Audit;

// Maps ehr.audit_events. EhrDbContext picks this up with ApplyConfigurationsFromAssembly.
public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> auditEvent)
    {
        auditEvent.ToTable("audit_events");
        auditEvent.Property(e => e.OccurredAt).HasDefaultValueSql("now()");
        auditEvent.Property(e => e.TransactionId).HasColumnType("xid8").HasDefaultValueSql("pg_current_xact_id()");
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
