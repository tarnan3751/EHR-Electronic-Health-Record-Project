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

        if (!await phi.IsAssignedAsync(patient, cancel) || await phi.PatientAsync(patient, cancel) is null)
        {
            return null;
        }

        var draft = new ClinicalDraft
        {
            Id = Guid.CreateVersion7(),
            PatientId = patient,
            ClinicianId = user.UserId!.Value,
            Body = "",
            History = "",
            Assessment = "",
            Plan = "",
            Version = 1,
            StructuredVersion = 1,
        };
        db.AddRange(draft);
        await db.SaveChangesAsync(cancel);
        return draft;
    }

    public async Task<NoteResult> SaveAsync(Guid patient, Guid id, long version, NoteContent content, CancellationToken cancel)
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

        if (content.Validate(signing: false) is { Count: > 0 } errors)
        {
            return new(422, errors);
        }

        content.ApplyTo(draft);
        await db.SaveChangesAsync(cancel);
        return new(200, [], draft);
    }

    public static IReadOnlyList<string> SigningProblems(ClinicalDraft draft)
    {
        var errors = NoteContent.From(draft).Validate(signing: true).ToList();
        if (draft.StructuredVersion != draft.Version)
        {
            errors.Add("This draft changed in another client. Reopen it, check all three fields, and save before signing.");
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
        foreach (var review in await phi.AiReviewsAsync(draft.Id, cancel))
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
            History = draft.History!,
            Assessment = draft.Assessment!,
            Plan = draft.Plan!,
            Sensitive = sensitive,
        };
        db.AddRange(note);
        audit.Record(AuditActions.SignNote, patient, "clinical_notes", note.Id);
        await db.SaveChangesAsync(cancel);
        return new(200, [], Note: note);
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
