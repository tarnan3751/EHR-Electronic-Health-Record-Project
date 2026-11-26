using Ehr.Data;
using Ehr.Data.Audit;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ehr.Domain.Audit;

// Reviewing emergency access, for administrators (the expansion plan's step 18; UC 24, proposed FR-64): an access as its
// review page shows it, the review, and the reviewing. Row-level security hands an administrator every review and
// anyone else none, and the access's own facts come from ehr.emergency_access_for_review, which answers an
// administrator alone (db/policies/72-emergency-access-review.sql). Administrators read no patient data: the patient is
// an ID here, as on the audit page, and nothing read here is a recorded read. What was done under the access is the
// audit log's record of it, without anything from the record: when, what, to which row, which part a read was for and
// from which site, never an event's details or reason. Whether a review may be made is the database's to say; this
// turns its refusals into words.
public sealed class EmergencyAccessReviews(EhrDbContext db, UserContext user)
{
    // The events of the access shown on its page; the audit page has the rest.
    public const int EventLimit = 100;

    // The window's start, before the grant: the app's clock and the database's differ, and the grant's own events are
    // written at the database's time.
    static readonly TimeSpan Before = TimeSpan.FromMinutes(1);

    public async Task<EmergencyAccessForReview?> ReadAsync(Guid accessId, CancellationToken cancellationToken)
    {
        var access = await db.Database.SqlQuery<AccessRow>(
            $"""
            SELECT patient_id, clinician_id, reason, granted_at, expires_at FROM ehr.emergency_access_for_review({accessId})
            """).SingleOrDefaultAsync(cancellationToken);
        if (access is null)
        {
            return null;
        }

        var review = await db.EmergencyAccessReviews.AsNoTracking().SingleOrDefaultAsync(r => r.Id == accessId, cancellationToken);
        var from = access.GrantedAt - Before;
        var events = await db.AuditEvents.AsNoTracking()
            .Where(e => e.PatientId == access.PatientId && e.ActorId == access.ClinicianId && e.OccurredAt >= from && e.OccurredAt <= access.ExpiresAt)
            .OrderBy(e => e.OccurredAt).ThenBy(e => e.Id)
            .Take(EventLimit + 1)
            .Select(e => new AccessEvent(e.OccurredAt, e.Action, e.TableName, e.RowId, e.Part, e.Source))
            .ToListAsync(cancellationToken);
        var people = new[] { access.ClinicianId, review?.ReviewerId ?? access.ClinicianId };
        var names = await db.StaffUsers.AsNoTracking()
            .Where(u => people.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => $"{u.DisplayName} ({u.Email})", cancellationToken);
        return new(accessId, access.PatientId, access.ClinicianId, names.GetValueOrDefault(access.ClinicianId, access.ClinicianId.ToString()),
            access.Reason, access.GrantedAt, access.ExpiresAt, [.. events.Take(EventLimit)], events.Count > EventLimit,
            review, review is null ? null : names.GetValueOrDefault(review.ReviewerId, review.ReviewerId.ToString()));
    }

    // The reviews of the accesses named, for the audit page's emergency accesses.
    public async Task<IReadOnlyDictionary<Guid, EmergencyAccessReview>> ReviewsAsync(IReadOnlyCollection<Guid> accesses, CancellationToken cancellationToken) =>
        accesses.Count == 0
            ? new Dictionary<Guid, EmergencyAccessReview>()
            : await db.EmergencyAccessReviews.AsNoTracking().Where(r => accesses.Contains(r.Id)).ToDictionaryAsync(r => r.Id, cancellationToken);

    // Records the review, in the page's transaction, as the signed-in administrator. The database sets who and when, and
    // refuses what it must; nothing is saved then.
    public async Task<ReviewOutcome> ReviewAsync(Guid accessId, string outcome, string note, CancellationToken cancellationToken)
    {
        const string savepoint = "emergency_access_review";
        var transaction = db.Database.CurrentTransaction!;
        await transaction.CreateSavepointAsync(savepoint, cancellationToken);
        var review = new EmergencyAccessReview
        {
            Id = accessId,
            ReviewerId = user.UserId!.Value,
            ReviewedAt = DateTimeOffset.UtcNow,
            Outcome = outcome,
            Note = note,
        };
        var entry = db.EmergencyAccessReviews.Add(review);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.ReleaseSavepointAsync(savepoint, cancellationToken);
            return ReviewOutcome.Reviewed;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException refused)
        {
            await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
            return refused switch
            {
                { SqlState: PostgresErrorCodes.UniqueViolation } => ReviewOutcome.AlreadyReviewed,
                { ConstraintName: "emergency_access_reviews_ended" } => ReviewOutcome.NotEnded,
                { ConstraintName: "emergency_access_reviews_own" } => ReviewOutcome.OwnAccess,
                { ConstraintName: "emergency_access_reviews_note" or "ck_emergency_access_reviews_note" }
                    or { SqlState: PostgresErrorCodes.StringDataRightTruncation } => ReviewOutcome.NoteLength,
                { ConstraintName: "ck_emergency_access_reviews_outcome" } => ReviewOutcome.NoOutcome,
                { ConstraintName: "emergency_access_reviews_access" } => ReviewOutcome.NotFound,
                _ => throw new InvalidOperationException("The review was refused.", e),
            };
        }
        finally
        {
            entry.State = EntityState.Detached;
        }
    }

    sealed record AccessRow(Guid PatientId, Guid ClinicianId, string Reason, DateTimeOffset GrantedAt, DateTimeOffset ExpiresAt);
}

// An emergency access to review: whose and which patient's (by ID), the reason given, when it began and ended, what its
// clinician did to that patient while it lasted (up to EventLimit, and whether there were more), and its review if it has
// one, with the reviewer's name.
public sealed record EmergencyAccessForReview(
    Guid Id, Guid PatientId, Guid ClinicianId, string Clinician, string Reason, DateTimeOffset GrantedAt, DateTimeOffset ExpiresAt,
    IReadOnlyList<AccessEvent> Events, bool MoreEvents, EmergencyAccessReview? Review, string? Reviewer)
{
    public bool Ended(DateTimeOffset now) => ExpiresAt <= now;
}

// One audit event under an access, with nothing of the record in it.
public sealed record AccessEvent(DateTimeOffset OccurredAt, string Action, string? TableName, Guid? RowId, string? Part, string Source);

public enum ReviewOutcome
{
    Reviewed,

    AlreadyReviewed,

    NotEnded,

    OwnAccess,

    NoteLength,

    NoOutcome,

    NotFound,
}
