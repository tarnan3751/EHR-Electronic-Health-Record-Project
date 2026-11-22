using Ehr.Data;
using Ehr.Data.Patients;
using Ehr.Domain.Accounts;
using Ehr.Domain.Phi;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ehr.Domain.Refills;

// Refill requests (the expansion plan's step 13d): the patient, or someone they share their record with whose access
// includes messages, asks the care team in the portal to renew a medication on the chart. Called inside the page's
// database transaction; requests are read through PhiReader. The database decides who may ask about which medication
// (the session's own record, a medication it may read, active and coded as a product the clinic orders), sets who asked
// and when and the medication's name and sensitivity, keeps one open request a medication, refuses a request nobody on
// the care team would be told of, and tells the care team, as it changes (db/policies/30-rls.sql and
// 77-refill-requests.sql). Nothing about the patient comes from
// the request: the page passes the session's patient.
//
// A clinician with the patient on their panel answers a request once: approves it by signing an order for the same
// medication on the order form, which answers it in the same transaction (ClinicalOrders.SignAsync), or declines it here,
// with a message for the patient. The database holds who may, that it happens once, and that an approval is a new order
// for the same medication product, and marks every care-team member's inbox item done. Either way the patient is told by
// email that there is something new in their portal, with nothing about it.
public sealed class RefillRequests(EhrDbContext db, PhiReader phi, PortalNotices notices)
{
    public const int MaxNoteLength = RefillRequestConfiguration.MaxNoteLength;

    public const int MaxDeclineLength = RefillRequestConfiguration.MaxDeclineLength;

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

    // Declines a waiting request of the patient's with a message for them. The caller has read it as this patient's.
    public async Task<RefillDeclined> DeclineAsync(Guid patient, Guid request, string? message, CancellationToken cancellationToken)
    {
        message = message?.ReplaceLineEndings("\n").Trim();
        if (string.IsNullOrEmpty(message))
        {
            return RefillDeclined.NoMessage;
        }

        if (message.Length > MaxDeclineLength)
        {
            return RefillDeclined.TooLong;
        }

        // Only the column the apps may update; the row is found by its ID, under row-level security.
        object row = new RefillRequest { Id = request, PatientId = patient, Status = RefillRequestStatus.Requested, DeclineMessage = message };
        const string savepoint = "refill_declined";
        var transaction = db.Database.CurrentTransaction!;
        await transaction.CreateSavepointAsync(savepoint, cancellationToken);
        try
        {
            db.Attach(row).Property(nameof(RefillRequest.DeclineMessage)).IsModified = true;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.ReleaseSavepointAsync(savepoint, cancellationToken);
            return RefillDeclined.Declined;
        }
        catch (DbUpdateConcurrencyException)
        {
            // No row the database lets this person answer: not on the patient's panel.
            await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
            return RefillDeclined.NotAllowed;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.CheckViolation or PostgresErrorCodes.InsufficientPrivilege } refusal)
        {
            await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
            return refusal.ConstraintName == "refill_requests_answered" ? RefillDeclined.AlreadyAnswered : RefillDeclined.NotAllowed;
        }
        finally
        {
            db.Entry(row).State = EntityState.Detached;
        }
    }

    // Tells the patient, at their own portal account's address, that there is something new in their portal, with
    // nothing about the request (PortalNotices), once a request was answered: at most once a day for answers to refill
    // requests, since the few emails an address may be sent in an hour are also what a password reset draws on. The link
    // is the portal's home, which names nothing.
    public async Task NotifyAnsweredAsync(Guid patient, string portalLink, CancellationToken cancellationToken)
    {
        if (await phi.RefillAnswersTodayAsync(patient, cancellationToken) == 1)
        {
            await notices.SomethingNewAsync(patient, portalLink, cancellationToken);
        }
    }
}

// What became of declining a request.
public enum RefillDeclined
{
    Declined,

    // Someone answered it first.
    AlreadyAnswered,

    // Not a request this clinician may answer: the patient isn't on their panel.
    NotAllowed,

    NoMessage,

    // The message is longer than MaxDeclineLength.
    TooLong,
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

// A medication the portal offers to renew: active and coded as a product the clinic orders, one the session may read,
// with no request of it waiting.
public sealed record RenewableMedication(Guid Id, string Display, string? Dosage, bool Sensitive);

// The portal's refill page for one record, already recorded as one read of the patient: the medications it may ask to
// renew; its requests, latest first, as the portal is given them; how many sensitive ones of either were left out, and
// whether sensitive ones were asked for; and whether the patient has a care team who could answer a request.
public sealed record PatientRefills(
    Ehr.Data.Patients.Patient Patient, IReadOnlyList<RenewableMedication> Medications, IReadOnlyList<PortalRefillRequest> Requests, int SensitiveHidden,
    bool SensitiveShown, bool HasCareTeam);

// A refill request as the clinician's pages show it, already recorded as one read of the patient: the patient and their
// allergies, for the banner; what was asked, when, by whom ("Name" for the patient, "Name (proxy)" for someone they
// share their record with) and with what note; its state and answer, with who answered; whether it is sensitive; its
// medication as the chart has it now; and the clinic's catalog item for that medication's product (its own code), when
// there is one: the one item that renews it.
public sealed record RefillReview(
    Ehr.Data.Patients.Patient Patient, IReadOnlyList<AllergyIntolerance> Allergies, Guid Id, string MedicationDisplay, string? Note, string AskedBy,
    DateTimeOffset RequestedAt, string Status, string? OrderedDisplay, string? DeclineMessage, string? DecidedBy, DateTimeOffset? DecidedAt, bool Sensitive,
    RefillMedication Medication, string? CatalogItem)
{
    public bool Waiting => Status == RefillRequestStatus.Requested;

    // Approving is signing an order for the same product, which only an active medication coded as one may have.
    public bool Renewable => Waiting && Medication.Status == MedicationStatementStatus.Active && CatalogItem is not null;
}

// The medication a request names, as the chart has it now.
public sealed record RefillMedication(string Display, string? Dosage, string Status, string? Code, bool Sensitive);
