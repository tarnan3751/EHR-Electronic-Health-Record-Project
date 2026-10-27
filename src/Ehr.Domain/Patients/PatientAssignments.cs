using Ehr.Data;
using Ehr.Data.Patients;
using Microsoft.EntityFrameworkCore;

namespace Ehr.Domain.Patients;

// The signed-in clinician's panel (PatientAssignment): which patients are theirs, putting one on it, taking one off.
// A clinician assigns patients to themselves and ends their own assignments; row-level security refuses anything
// else, and the write-audit trigger records both. Listing the panel's patients is a PHI read, which the PHI reader
// does and records; this class only ever handles IDs.
public sealed class PatientAssignments(EhrDbContext db, UserContext user)
{
    // The IDs of the patients on the clinician's panel.
    public async Task<HashSet<Guid>> PanelIdsAsync(CancellationToken cancellationToken) =>
        [.. await Current().Select(a => a.PatientId).ToListAsync(cancellationToken)];

    public Task<bool> IsOnPanelAsync(Guid patientId, CancellationToken cancellationToken) =>
        Current().AnyAsync(a => a.PatientId == patientId, cancellationToken);

    // Puts the patient on the panel. Nothing changes for one already on it.
    public async Task AssignAsync(Guid patientId, CancellationToken cancellationToken)
    {
        if (await IsOnPanelAsync(patientId, cancellationToken))
        {
            return;
        }

        db.PatientAssignments.Add(new PatientAssignment
        {
            Id = Guid.CreateVersion7(),
            PatientId = patientId,
            ClinicianId = user.UserId!.Value,
            AssignedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    // Takes the patient off the panel: the assignment ends, and stays as history.
    public Task EndAsync(Guid patientId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return Current().Where(a => a.PatientId == patientId)
            .ExecuteUpdateAsync(set => set.SetProperty(a => a.EndedAt, now), cancellationToken);
    }

    IQueryable<PatientAssignment> Current() =>
        db.PatientAssignments.Where(a => a.ClinicianId == user.UserId && a.EndedAt == null);
}
