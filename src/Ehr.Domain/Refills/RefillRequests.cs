using Ehr.Data;
using Ehr.Data.Patients;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ehr.Domain.Refills;

// Refill requests (the expansion plan's step 13d): the patient, or someone they share their record with whose access
// includes messages, asks the care team in the portal to renew a medication on the chart. Called inside the page's
// database transaction; requests are read through PhiReader. The database decides who may ask about which medication
// (the session's own record, a medication it may read, active and coded), sets who asked and when and the medication's
// name and sensitivity, keeps one open request a medication, refuses a request nobody on the care team would be told
// of, and tells the care team (db/policies/30-rls.sql and 77-refill-requests.sql). Nothing about the patient comes from
// the request: the page passes the session's patient.
public sealed class RefillRequests(EhrDbContext db)
{
    public const int MaxNoteLength = RefillRequestConfiguration.MaxNoteLength;

    // Asks to renew a medication of the patient's: Sent when the request was saved, and why not otherwise.
    public async Task<RefillAsked> AskAsync(Guid patient, Guid medication, string? note, CancellationToken cancellationToken)
    {
        note = note?.ReplaceLineEndings("\n").Trim();
        if (note?.Length > MaxNoteLength)
        {
            return RefillAsked.TooLong;
        }

        object request = new RefillRequest
        {
            Id = Guid.CreateVersion7(),
            PatientId = patient,
            MedicationStatementId = medication,
            Note = string.IsNullOrEmpty(note) ? null : note,
            Status = RefillRequestStatus.Requested,
        };
        const string savepoint = "refill_request";
        var transaction = db.Database.CurrentTransaction!;
        await transaction.CreateSavepointAsync(savepoint, cancellationToken);
        try
        {
            db.Add(request);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.ReleaseSavepointAsync(savepoint, cancellationToken);
            return RefillAsked.Sent;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.CheckViolation or PostgresErrorCodes.InsufficientPrivilege
                or PostgresErrorCodes.ForeignKeyViolation,
        } refusal)
        {
            await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
            return refusal switch
            {
                { SqlState: PostgresErrorCodes.UniqueViolation } => RefillAsked.AlreadyAsked,
                { ConstraintName: "refill_requests_care_team" } => RefillAsked.NoCareTeam,
                _ => RefillAsked.NotAvailable,
            };
        }
        finally
        {
            db.Entry(request).State = EntityState.Detached;
        }
    }
}

// What became of asking to renew a medication.
public enum RefillAsked
{
    Sent,

    // A request for that medication is waiting for the care team already.
    AlreadyAsked,

    // Not a medication this session may ask to renew any more: stopped, entered in error, made sensitive, or another's.
    NotAvailable,

    // Nobody has the patient on their panel who could answer, so nothing was saved.
    NoCareTeam,

    // The note is longer than MaxNoteLength.
    TooLong,
}

// A medication the portal offers to renew: active and coded, one the session may read, with no request of it waiting.
public sealed record RenewableMedication(Guid Id, string Display, string? Dosage, bool Sensitive);

// The portal's refill page for one record, already recorded as one read of the patient: the medications it may ask to
// renew; its requests, latest first, as the portal is given them; how many sensitive ones of either were left out, and
// whether sensitive ones were asked for; and whether the patient has a care team who could answer a request.
public sealed record PatientRefills(
    Ehr.Data.Patients.Patient Patient, IReadOnlyList<RenewableMedication> Medications, IReadOnlyList<PortalRefillRequest> Requests, int SensitiveHidden,
    bool SensitiveShown, bool HasCareTeam);
