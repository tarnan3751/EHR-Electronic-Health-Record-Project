using Ehr.Data.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ehr.Data.Patients;

// Maps ehr.break_glass_accesses. EhrDbContext picks this up with ApplyConfigurationsFromAssembly.
public sealed class BreakGlassAccessConfiguration : IEntityTypeConfiguration<BreakGlassAccess>
{
    public void Configure(EntityTypeBuilder<BreakGlassAccess> access)
    {
        access.ToTable("break_glass_accesses");
        access.Property(a => a.Reason).HasMaxLength(500);
        // Does this clinician hold access to this patient right now.
        access.HasIndex(a => new { a.ClinicianId, a.PatientId, a.ExpiresAt });
        access.HasOne<Patient>().WithMany().HasForeignKey(a => a.PatientId).OnDelete(DeleteBehavior.Restrict);
        access.HasOne<StaffUser>().WithMany().HasForeignKey(a => a.ClinicianId).OnDelete(DeleteBehavior.Restrict);
    }
}
