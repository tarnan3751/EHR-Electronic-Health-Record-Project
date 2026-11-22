using Ehr.Data.Accounts;
using Ehr.Data.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ehr.Data.Patients;

// Maps ehr.refill_requests (step 13d). EhrDbContext picks this up with ApplyConfigurationsFromAssembly.
public sealed class RefillRequestConfiguration : IEntityTypeConfiguration<RefillRequest>
{
    // The most the patient's note holds: a line or two for the care team, not a message.
    public const int MaxNoteLength = 500;

    // The most a clinician's message declining a request holds.
    public const int MaxDeclineLength = 1000;

    public void Configure(EntityTypeBuilder<RefillRequest> request)
    {
        request.ToTable("refill_requests", table =>
        {
            table.HasCheckConstraint("ck_refill_requests_status", $"status IN ({Codes.List(RefillRequestStatus.All)})");
            // Waiting with no answer; approved with its order and what was ordered; declined with its message; answered
            // with who and when.
            table.HasCheckConstraint("ck_refill_requests_answer",
                $"(status = '{RefillRequestStatus.Requested}') = (decided_at IS NULL) AND (decided_at IS NULL) = (decided_by IS NULL) "
                + $"AND (status = '{RefillRequestStatus.Completed}') = (order_id IS NOT NULL) AND (order_id IS NULL) = (ordered_display IS NULL) "
                + $"AND (status = '{RefillRequestStatus.Rejected}') = (decline_message IS NOT NULL)");
        });

        request.Property(r => r.MedicationDisplay).HasMaxLength(300);
        // As long as a medication's code can be.
        request.Property(r => r.MedicationCode).HasMaxLength(20);
        request.Property(r => r.Note).HasMaxLength(MaxNoteLength);
        request.Property(r => r.Status).HasMaxLength(20);
        // As long as a catalog item's name can be.
        request.Property(r => r.OrderedDisplay).HasMaxLength(200);
        request.Property(r => r.DeclineMessage).HasMaxLength(MaxDeclineLength);
        request.HasOne<Patient>().WithMany().HasForeignKey(r => r.PatientId).OnDelete(DeleteBehavior.Restrict);
        request.HasOne<MedicationStatement>().WithMany().HasForeignKey(r => r.MedicationStatementId).OnDelete(DeleteBehavior.Restrict);
        request.HasOne<PatientUser>().WithMany().HasForeignKey(r => r.RequestedBy).OnDelete(DeleteBehavior.Restrict);
        request.HasOne<Order>().WithMany().HasForeignKey(r => r.OrderId).OnDelete(DeleteBehavior.Restrict);
        request.HasOne<StaffUser>().WithMany().HasForeignKey(r => r.DecidedBy).OnDelete(DeleteBehavior.Restrict);
        // The portal's list: a patient's, newest first.
        request.HasIndex(r => new { r.PatientId, r.RequestedAt });
        // A medication's requests, whatever their state: what follows the medication's sensitivity reads them by it.
        request.HasIndex(r => r.MedicationStatementId);
        // One open request for a medication: a second waits until the first is answered.
        request.HasIndex(r => r.MedicationStatementId, "Open").IsUnique().HasFilter($"status = '{RefillRequestStatus.Requested}'")
            .HasDatabaseName("ux_refill_requests_open");
        // An order answers one request.
        request.HasIndex(r => r.OrderId).IsUnique();
    }
}
