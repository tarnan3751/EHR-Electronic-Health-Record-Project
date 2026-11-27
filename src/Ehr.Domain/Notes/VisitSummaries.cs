using Ehr.Data;
using Ehr.Data.Patients;
using Ehr.Domain.Accounts;
using Ehr.Domain.Phi;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ehr.Domain.Notes;

// Visit summaries (the expansion plan's step 13a): what a clinician writes for the patient from a signed note, in
// three plain-language parts, released to the patient portal; withdrawn, with a reason, when wrong. Called inside the
// page's database transaction; summaries are read through PhiReader. The database decides who may (a clinician with the
// patient on their panel, as themselves), sets who released or withdrew one and when, takes the note's date, makes it
// sensitive with the note and its addenda, keeps one standing summary a note, refuses internal codes and identifiers,
// tells the releaser when someone else withdraws theirs, and refuses every other change (db/policies/30-rls.sql and
// 52-visit-summaries.sql). When its rule about codes refuses one, the rule is asked again, so the page can say what to
// change: a summary released is checked once. Nothing is copied from the note: the clinician writes it. Marking one sensitive afterwards is ClinicalNotes.SetSensitivityAsync's, as
// for a note or an addendum.
public sealed class VisitSummaries(EhrDbContext db, PhiReader phi, NoteWriteAuthority authority, PortalNotices notices)
{
    public const int MaxLength = VisitSummaryConfiguration.MaxLength;

    public const int MaxReasonLength = VisitSummaryConfiguration.MaxReasonLength;

    // The three parts, as the form, the note's page and the portal name them.
    public static readonly string[] Parts = ["What we talked about", "What we found", "What happens next"];

    // Releases a summary of a signed note this patient's: the new summary's ID when it was released. `sensitive`: the
    // clinician marks it sensitive as it is released (its own mark); it is sensitive anyway when the note or an addendum
    // is (Tahseen's decision of 21 November 2026).
    public async Task<SummaryResult> ReleaseAsync(Guid patient, Guid note, string? talkedAbout, string? found, string? nextSteps, bool sensitive, CancellationToken cancel)
    {
        if (!authority.IsPrimaryHost)
        {
            return new(503, ["Releasing a summary needs the clinic's server. Your text is still here."]);
        }

        string[] parts = [.. new[] { talkedAbout, found, nextSteps }.Select(part => part?.ReplaceLineEndings("\n").Trim() ?? "")];
        var errors = new List<string>();
        var errorParts = new List<int?>();
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0)
            {
                errors.Add($"Write “{Parts[i]}”.");
                errorParts.Add(i);
            }
            else if (parts[i].Length > MaxLength)
            {
                errors.Add($"“{Parts[i]}” can be up to {MaxLength:N0} characters. It has {parts[i].Length:N0}.");
                errorParts.Add(i);
            }
        }

        if (errors.Count > 0)
        {
            return new(422, errors) { ErrorParts = errorParts };
        }

        if (!await phi.IsAssignedAsync(patient, cancel))
        {
            return new(403, ["To write a visit summary, add this patient to My patients."]);
        }

        object summary = new VisitSummary
        {
            Id = Guid.CreateVersion7(),
            NoteId = note,
            PatientId = patient,
            TalkedAbout = parts[0],
            Found = parts[1],
            NextSteps = parts[2],
            Sensitive = sensitive,
        };
        const string savepoint = "visit_summary";
        var transaction = db.Database.CurrentTransaction!;
        await transaction.CreateSavepointAsync(savepoint, cancel);
        try
        {
            db.Add(summary);
            await db.SaveChangesAsync(cancel);
            await transaction.ReleaseSavepointAsync(savepoint, cancel);
            return new(200, [], ((VisitSummary)summary).Id);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.CheckViolation or PostgresErrorCodes.ForeignKeyViolation
                or PostgresErrorCodes.UniqueViolation,
        } refusal)
        {
            await transaction.RollbackToSavepointAsync(savepoint, cancel);
            if (refusal.SqlState == PostgresErrorCodes.CheckViolation && refusal.ConstraintName == "visit_summaries_plain")
            {
                // The rule refused it: asked again, only now, to say what it found and where.
                var problems = await VisitSummaryChecks.ProblemsAsync(db, patient, note, parts[0], parts[1], parts[2], cancel);
                return problems.Count > 0
                    ? new(422, [.. problems.Select(Explain)]) { ErrorParts = [.. problems.Select(problem => (int?)(problem.Part - 1))] }
                    : new(422, ["It has a code, a record number or an identifier in it. Write it in plain words."]);
            }

            return refusal.SqlState switch
            {
                PostgresErrorCodes.InsufficientPrivilege => new(403, ["To write a visit summary, add this patient to My patients."]),
                PostgresErrorCodes.UniqueViolation => new(409, ["A summary of this note has already been released. Withdraw it first to write another."]),
                _ => new(404, ["This note is unavailable."]),
            };
        }
        finally
        {
            db.Entry(summary).State = EntityState.Detached;
        }
    }

    // Withdraws a released summary, with a reason. The caller has read it as a summary of this patient's note. Viewed, in
    // the result, says whether the portal had recorded a view of it by the patient or a proxy, which the database
    // answers for this withdrawal alone: then the patient is told (NotifyPatientAsync).
    public async Task<SummaryResult> WithdrawAsync(Guid patient, Guid summary, string? reason, CancellationToken cancel)
    {
        if (!authority.IsPrimaryHost)
        {
            return new(503, ["Withdrawing a summary needs the clinic's server. Your reason is still here."]);
        }

        reason = reason?.Trim();
        if (string.IsNullOrEmpty(reason))
        {
            return new(422, ["Say why, so the record shows it."]);
        }

        if (reason.Length > MaxReasonLength)
        {
            return new(422, [$"The reason can be up to {MaxReasonLength} characters. This one has {reason.Length}."]);
        }

        if (!await phi.IsAssignedAsync(patient, cancel))
        {
            return new(403, ["To withdraw a visit summary, add this patient to My patients."]);
        }

        // Only the column the apps may update; the row is found by its ID, under row-level security.
        object row = new VisitSummary { Id = summary, PatientId = patient, TalkedAbout = "", Found = "", NextSteps = "", WithdrawnReason = reason };
        const string savepoint = "visit_summary_withdrawn";
        var transaction = db.Database.CurrentTransaction!;
        await transaction.CreateSavepointAsync(savepoint, cancel);
        try
        {
            db.Attach(row).Property(nameof(VisitSummary.WithdrawnReason)).IsModified = true;
            await db.SaveChangesAsync(cancel);
            await transaction.ReleaseSavepointAsync(savepoint, cancel);
            return new(200, [], summary) { Viewed = await VisitSummaryChecks.WithdrawnWasViewedAsync(db, summary, cancel) };
        }
        catch (DbUpdateConcurrencyException)
        {
            // No row the database lets this person change: the panel was checked above, so it is no longer there for them.
            await transaction.RollbackToSavepointAsync(savepoint, cancel);
            return new(403, ["To withdraw a visit summary, add this patient to My patients."]);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.CheckViolation or PostgresErrorCodes.InsufficientPrivilege } refusal)
        {
            await transaction.RollbackToSavepointAsync(savepoint, cancel);
            return refusal.SqlState == PostgresErrorCodes.InsufficientPrivilege
                ? new(403, ["To withdraw a visit summary, add this patient to My patients."])
                : new(409, ["This summary has already been withdrawn. Nothing was changed."]);
        }
        finally
        {
            db.Entry(row).State = EntityState.Detached;
        }
    }

    // Tells the patient, at their own portal account's address, that there is something new in their portal, with
    // nothing about the summary (PortalNotices). Sent when a summary is released, at most once a day
    // (NotifyReleasedAsync: the few emails an address may be sent in an hour, CappedEmailSender, are also what a password
    // reset draws on), and when one the patient or a proxy had viewed is withdrawn, whatever was sent that day (Tahseen's
    // decision of 21 November 2026).
    public Task NotifyPatientAsync(Guid patient, string portalLink, CancellationToken cancel) => notices.SomethingNewAsync(patient, portalLink, cancel);

    // Once a summary was released: the notice, unless another summary was released for the patient earlier in the
    // clinic's day (PortalNotices.StartOfToday).
    public async Task NotifyReleasedAsync(Guid patient, string portalLink, CancellationToken cancel)
    {
        if (await phi.SummariesReleasedSinceAsync(patient, notices.StartOfToday, cancel) == 1)
        {
            await notices.SomethingNewAsync(patient, portalLink, cancel);
        }
    }

    // What the page says about one thing the rule found, naming it and where it is.
    static string Explain(VisitSummaryProblem problem)
    {
        var part = Parts[problem.Part - 1];
        return problem.Kind switch
        {
            "identifier" => $"“{problem.Found}” in {part} looks like an internal identifier. Leave it out.",
            "record-number" => $"“{problem.Found}” in {part} is the patient's medical record number. Leave it out.",
            "diagnosis-code" => $"“{problem.Found}” in {part} is a diagnosis code. Say what it means in plain words.",
            "medication-code" => $"“{problem.Found}” in {part} is a medication code. Name the medicine in plain words.",
            "lab-code" => $"“{problem.Found}” in {part} is a lab test code. Name the test in plain words.",
            "substance-code" => $"“{problem.Found}” in {part} is a substance code. Name it in plain words.",
            "order-code" => $"“{problem.Found}” in {part} is one of the clinic's order codes. Say what was ordered in plain words.",
            "note-section" => $"{part} repeats the note's {problem.Found} word for word. Write it for the patient in your own words.",
            _ => $"“{problem.Found}” in {part} can't be shown to the patient.",
        };
    }
}

// What became of releasing or withdrawing a summary: 200 done, with its ID; 422 with what to fix; 403 the patient isn't
// on the clinician's panel; 404 the note isn't this patient's; 409 already released, or already withdrawn; 503 off the
// clinic's server.
public sealed record SummaryResult(int Status, IReadOnlyList<string> Errors, Guid? Summary = null)
{
    // Withdrawn after the patient or a proxy had viewed it in the portal.
    public bool Viewed { get; init; }

    // For each error, the part of the summary it is about (0 What we talked about, 1 What we found, 2 What happens next),
    // or null for one about none of them: the form marks those parts alone, each tied to its messages.
    public IReadOnlyList<int?> ErrorParts { get; init; } = [];
}
