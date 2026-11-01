using Ehr.Data.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ehr.Data.Patients;

// Maps ehr.patient_assignments. EhrDbContext picks this up with ApplyConfigurationsFromAssembly.
public sealed class PatientAssignmentConfiguration : IEntityTypeConfiguration<PatientAssignment>
{
    public void Configure(EntityTypeBuilder<PatientAssignment> assignment)
    {
        assignment.ToTable("patient_assignments");
        // A patient is on a clinician's panel once at a time; an ended assignment doesn't stop a new one.
        assignment.HasIndex(a => new { a.PatientId, a.ClinicianId }).IsUnique().HasFilter("ended_at IS NULL");
        // A clinician's panel is read by clinician.
        assignment.HasIndex(a => a.ClinicianId);
        assignment.HasOne<Patient>().WithMany().HasForeignKey(a => a.PatientId).OnDelete(DeleteBehavior.Restrict);
        assignment.HasOne<StaffUser>().WithMany().HasForeignKey(a => a.ClinicianId).OnDelete(DeleteBehavior.Restrict);
    }
}
