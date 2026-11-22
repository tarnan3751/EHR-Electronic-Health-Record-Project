using Ehr.Data;
using Ehr.Data.Audit;
using Ehr.Data.Patients;
using Ehr.Domain.Audit;
using Ehr.Domain.Phi;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ehr.Domain.Notes;

// Called inside the page's database transaction. PHI reads are audited by PhiReader; writes and signing are
// audited by the database. The insert trigger locks the source draft and validates the exact signed snapshot.
public sealed class ClinicalNotes(EhrDbContext db, PhiReader phi, UserContext user, AuditRecorder audit, NoteWriteAuthority authority)
{
    public async Task<ClinicalDraft?> CreateAsync(Guid patient, CancellationToken cancel)
    {
        if (!authority.IsPrimaryHost)
        {
            throw new InvalidOperationException("Draft creation requires the authoritative on-premises host.");
        }

        if (!await phi.IsAssignedAsync(patient, cancel) || await phi.PatientAsync(patient, AuditParts.NoteDraft, cancel) is null)
        {
            return null;
        }

        var draft = new ClinicalDraft
        {
            Id = Guid.CreateVersion7(),
            PatientId = patient,
            ClinicianId = user.UserId!.Value,
            Body = "",
            Subjective = "",
            Objective = "",
            Assessment = "",
            Plan = "",
            Version = 1,
            StructuredVersion = 1,
        };
        db.AddRange(draft);
        await db.SaveChangesAsync(cancel);
        return draft;
    }

    // The visit types a note can be written for (step 14), in the order the editor lists them: reference data, not PHI.
    public Task<List<Ehr.Data.Reference.NoteTemplate>> TemplatesAsync(CancellationToken cancel) =>
        db.NoteTemplates.AsNoTracking().OrderBy(t => t.Position).ToListAsync(cancel);

    // `template`: the visit type the author chose, or none (step 14). Guidance for the editor only; saved with the draft.
    public async Task<NoteResult> SaveAsync(Guid patient, Guid id, long version, NoteContent content, CancellationToken cancel, string? template = null)
    {
        if (!authority.IsPrimaryHost)
        {
            return new(503, ["The authoritative clinic server must be available to save or sign. Your work is still displayed."]);
        }

        var draft = await phi.ReadDraftAsync(id, patient, cancel);
        if (draft is null)
        {
            return NoteResult.Missing;
        }

        if (await phi.SignedDraftAsync(id, patient, cancel) is not null)
        {
            return new(409, ["This note is signed. Open the signed note from the chart."]);
        }

        if (draft.Version != version)
        {
            return NoteResult.Conflict;
        }

        if (content.Validate(signing: false, NoteContent.OriginOf(draft)) is { Count: > 0 } errors)
        {
            return new(422, errors);
        }

        if (template is not null && !await db.NoteTemplates.AnyAsync(t => t.Code == template, cancel))
        {
            return new(422, ["Choose a visit type from the list."]);
        }

        draft.NoteTemplateCode = template;
        content.ApplyTo(draft);
        await db.SaveChangesAsync(cancel);
        return new(200, [], draft);
    }

    public static IReadOnlyList<string> SigningProblems(ClinicalDraft draft)
    {
        var errors = NoteContent.From(draft).Validate(signing: true).ToList();
        if (draft.StructuredVersion != draft.Version)
        {
            errors.Add("This draft changed in another client. Reopen it, check all four sections, and save before signing.");
        }
        else if (draft.History is not null)
        {
            // Written before notes had four sections (step 14): the database signs it only once its author has saved it so.
            errors.Add("This draft was written with a History section. Open it, move what belongs in Objective, and save before signing.");
        }

        if (draft.HasUnreviewedProposals)
        {
            errors.Add("Review every associated AI proposal before signing. Open the saved AI review to resolve them.");
        }

        return errors;
    }

    public async Task<IReadOnlyList<string>> SigningProblemsAsync(ClinicalDraft draft, CancellationToken cancel)
    {
        var errors = SigningProblems(draft).ToList();
        foreach (var review in await phi.AiReviewsAsync(draft.Id, AuditParts.NoteDraft, cancel))
        {
            var proposals = await phi.AiProposalsAsync(review.Id, cancel);
            var pending = proposals.Where(p => p.State == AiProposalStates.Pending).Select(p => p.Field).ToList();
            if (review.State == AiReviewStates.Recording)
            {
                errors.Add("Finish or discard the unfinished dictation before signing this note.");
            }
            if (review.State == AiReviewStates.Pending || pending.Count > 0)
            {
                errors.Add(pending.Count > 0 ? $"Review the pending fields: {string.Join(", ", pending)}."
                    : "Complete the saved AI review before signing.");
            }
            if (review.State == AiReviewStates.Completed && (review.ImportedVersion is null || review.ImportedVersion > draft.Version))
            {
                errors.Add("The saved AI review does not match this draft. Reopen the review before signing.");
            }
        }

        return errors;
    }

    // `sensitive` (step 10): the signer marks the note sensitive, for good, as it is signed.
    public async Task<NoteResult> SignAsync(Guid patient, Guid id, long version, bool confirmed, CancellationToken cancel, bool sensitive = false)
    {
        // ReadDraft enforces ownership AND current panel assignment through RLS, even for a retried signature.
        if (!authority.IsPrimaryHost)
        {
            return new(503, ["The authoritative clinic server must be available to save or sign. Your work is still displayed."]);
        }

        var draft = await phi.ReadDraftAsync(id, patient, cancel);
        if (draft is null)
        {
            return NoteResult.Missing;
        }

        if (!confirmed)
        {
            return new(422, ["Confirm that you have reviewed this note before signing."]);
        }

        if (await phi.SignedDraftAsync(id, patient, cancel) is { } existing)
        {
            if (existing.DraftVersion == version && existing.Sensitive != sensitive)
            {
                // A retry that ticks or clears "Sensitive" can't change a note that was already signed (step 10).
                return new(409, [existing.Sensitive
                    ? "This note was already signed, as sensitive. Open the signed note from the chart."
                    : "This note was already signed, and not as sensitive. A signed note can't be changed: open it from the chart."]);
            }

            return existing.DraftVersion == version
                ? new(200, [], Note: existing)
                : NoteResult.Conflict;
        }

        if (draft.Version != version)
        {
            return NoteResult.Conflict;
        }

        if (await SigningProblemsAsync(draft, cancel) is { Count: > 0 } errors)
        {
            return new(422, errors);
        }

        var note = new ClinicalNote
        {
            Id = Guid.CreateVersion7(),
            DraftId = id,
            PatientId = patient,
            DraftVersion = version,
            Subjective = draft.Subjective!,
            Objective = draft.Objective!,
            Assessment = draft.Assessment!,
            Plan = draft.Plan!,
            Sensitive = sensitive,
        };
        db.AddRange(note);
        audit.Record(AuditActions.SignNote, patient, "clinical_notes", note.Id);
        await db.SaveChangesAsync(cancel);
        return new(200, [], Note: note);
    }

    // Marks a signed note, or one of its addenda, sensitive after signing, or unmarks it, with a reason (step 10b); or one
    // of its visit summaries (step 13a), under the addenda's rules. The text never changes. The database decides who may
    // (a clinician with the patient on their panel marks; only the person who made it sensitive, or the note's signer,
    // unmarks), sets who and when, and marks a note's addenda and summaries with it (db/policies/30-rls.sql,
    // 50-clinical-notes.sql and 52-visit-summaries.sql). The caller has read the note as this patient's, and the
    // addendum or summary as the note's.
    public async Task<SensitivityResult> SetSensitivityAsync(Guid patient, Guid note, Guid? addendum, bool sensitive, string? reason, CancellationToken cancel, Guid? summary = null)
    {
        if (!authority.IsPrimaryHost)
        {
            return new(503, ["The authoritative clinic server must be available to change this. Your reason is still displayed."]);
        }

        reason = reason?.Trim();
        if (string.IsNullOrEmpty(reason))
        {
            return new(422, ["Say why, so the record shows it."]);
        }

        if (reason.Length > 500)
        {
            return new(422, [$"The reason can be up to 500 characters. This one has {reason.Length}."]);
        }

        if (!await phi.IsAssignedAsync(patient, cancel))
        {
            return new(403, ["Add this patient to your panel to change this."]);
        }

        // Only the two columns the apps may update; the row is found by its ID, under row-level security.
        object row = (addendum, summary) switch
        {
            ({ } id, _) => new NoteAddendum { Id = id, NoteId = note, PatientId = patient, Text = "", Sensitive = sensitive, SensitiveReason = reason },
            (_, { } id) => new VisitSummary { Id = id, NoteId = note, PatientId = patient, TalkedAbout = "", Found = "", NextSteps = "", Sensitive = sensitive, SensitiveReason = reason },
            _ => new ClinicalNote { Id = note, PatientId = patient, Assessment = "", Plan = "", Sensitive = sensitive, SensitiveReason = reason },
        };
        const string savepoint = "note_sensitivity";
        var transaction = db.Database.CurrentTransaction!;
        await transaction.CreateSavepointAsync(savepoint, cancel);
        try
        {
            var entry = db.Attach(row);
            entry.Property(nameof(ClinicalNote.Sensitive)).IsModified = true;
            entry.Property(nameof(ClinicalNote.SensitiveReason)).IsModified = true;
            await db.SaveChangesAsync(cancel);
            await transaction.ReleaseSavepointAsync(savepoint, cancel);
            return new(200, []);
        }
        catch (DbUpdateConcurrencyException)
        {
            // No row the database lets this person update: unmarking is for the person who marked it or the note's
            // signer (the policy decides, on the row as it stood); the panel was checked above.
            await transaction.RollbackToSavepointAsync(savepoint, cancel);
            return new(403, [sensitive
                ? "This couldn't be marked. Open the note again."
                : "Only the person who marked it sensitive, or the note's signer, can unmark it."]);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.CheckViolation } refusal)
        {
            await transaction.RollbackToSavepointAsync(savepoint, cancel);
            return refusal.SqlState == PostgresErrorCodes.InsufficientPrivilege
                ? new(403, ["Only the person who marked it sensitive, or the note's signer, can unmark it."])
                : new(409, [(addendum, summary, sensitive) switch
                {
                    (not null, _, false) => "This can't be unmarked: an addendum to a sensitive note is sensitive, or it has already been unmarked.",
                    (_, not null, false) => "This can't be unmarked: a summary of a sensitive note or addendum is sensitive, or it has already been unmarked.",
                    _ => "This has already been changed. Open the note again.",
                }]);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected })
        {
            // Someone changed the note's sensitivity and an addendum's at the same moment, and the database refused
            // this one of the two. Nothing was changed.
            await transaction.RollbackToSavepointAsync(savepoint, cancel);
            return new(409, ["Someone else was changing this note at the same moment. Nothing was changed. Open the note again."]);
        }
        finally
        {
            db.Entry(row).State = EntityState.Detached;
        }
    }

    // Signs an addendum to a signed note (step 10): a new text attached to the note, which stays as it was. The
    // database decides who may (a clinician with the patient on their panel, as themselves), sets who signed and when,
    // and makes an addendum to a sensitive note sensitive (db/policies/30-rls.sql and 50-clinical-notes.sql).
    public async Task<AddendumResult> AddAddendumAsync(Guid patient, Guid note, string? text, bool sensitive, CancellationToken cancel)
    {
        if (!authority.IsPrimaryHost)
        {
            return new(503, ["The authoritative clinic server must be available to sign an addendum. Your text is still displayed."]);
        }

        text = text?.ReplaceLineEndings("\n").Trim();
        if (string.IsNullOrEmpty(text))
        {
            return new(422, ["Write the addendum before signing it."]);
        }

        if (text.Length > NoteAddendumConfiguration.MaxLength)
        {
            return new(422, [$"An addendum can be up to {NoteAddendumConfiguration.MaxLength:N0} characters. This one has {text.Length:N0}."]);
        }

        if (!await phi.IsAssignedAsync(patient, cancel))
        {
            return new(403, ["Add this patient to your panel to sign an addendum."]);
        }

        object addendum = new NoteAddendum { Id = Guid.CreateVersion7(), NoteId = note, PatientId = patient, AuthorId = user.UserId ?? Guid.Empty, Text = text, Sensitive = sensitive };
        const string savepoint = "note_addendum";
        var transaction = db.Database.CurrentTransaction!;
        await transaction.CreateSavepointAsync(savepoint, cancel);
        try
        {
            db.Add(addendum);
            await db.SaveChangesAsync(cancel);
            await transaction.ReleaseSavepointAsync(savepoint, cancel);
            return new(200, []);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.CheckViolation or PostgresErrorCodes.ForeignKeyViolation,
        } refusal)
        {
            await transaction.RollbackToSavepointAsync(savepoint, cancel);
            return refusal.SqlState == PostgresErrorCodes.InsufficientPrivilege
                ? new(403, ["Add this patient to your panel to sign an addendum."])
                : new(404, ["This note is unavailable."]);
        }
        finally
        {
            db.Entry(addendum).State = EntityState.Detached;
        }
    }
}

// What became of marking or unmarking: 200 done; 422 with what to fix; 403 not allowed (off the panel, or not the
// person who may unmark); 409 it is already as asked, or an addendum of a sensitive note; 503 off the clinic's server.
public sealed record SensitivityResult(int Status, IReadOnlyList<string> Errors);

// What became of an addendum: 200 signed; 422 with what to fix; 403 the patient isn't on the clinician's panel; 404
// the note isn't this patient's; 503 off the clinic's server.
public sealed record AddendumResult(int Status, IReadOnlyList<string> Errors);

public sealed record NoteResult(int Status, IReadOnlyList<string> Errors, ClinicalDraft? Draft = null, ClinicalNote? Note = null)
{
    public static NoteResult Missing => new(404, ["This draft is unavailable. Check your patient panel and draft ownership."]);
    public static NoteResult Conflict => new(409, ["This draft changed elsewhere. Your text is still here. Open the saved draft in another tab and compare before saving again."]);
}

// Host configuration, never supplied by an HTTP request. PostgreSQL additionally refuses replica/read-only writes.
public sealed record NoteWriteAuthority(bool IsPrimaryHost);
