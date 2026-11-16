using Ehr.Data;
using Ehr.Data.Audit;
using Ehr.Data.Patients;
using Ehr.Domain.Audit;
using Ehr.Domain.Phi;

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

        draft.History = content.History;
        draft.Assessment = content.Assessment;
        draft.Plan = content.Plan;
        draft.Body = content.Body;
        draft.StructuredVersion = ++draft.Version;
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
            errors.Add("Review every associated AI proposal before signing. AI review is not available in this editor yet.");
        }

        return errors;
    }

    public async Task<NoteResult> SignAsync(Guid patient, Guid id, long version, bool confirmed, CancellationToken cancel)
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
            return existing.DraftVersion == version
                ? new(200, [], Note: existing)
                : NoteResult.Conflict;
        }

        if (draft.Version != version)
        {
            return NoteResult.Conflict;
        }

        if (SigningProblems(draft) is { Count: > 0 } errors)
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
        };
        db.AddRange(note);
        audit.Record(AuditActions.SignNote, patient, "clinical_notes", note.Id);
        await db.SaveChangesAsync(cancel);
        return new(200, [], Note: note);
    }
}

public sealed record NoteResult(int Status, IReadOnlyList<string> Errors, ClinicalDraft? Draft = null, ClinicalNote? Note = null)
{
    public static NoteResult Missing => new(404, ["This draft is unavailable. Check your patient panel and draft ownership."]);
    public static NoteResult Conflict => new(409, ["This draft changed elsewhere. Your text is still here. Open the saved draft in another tab and compare before saving again."]);
}

// Host configuration, never supplied by an HTTP request. PostgreSQL additionally refuses replica/read-only writes.
public sealed record NoteWriteAuthority(bool IsPrimaryHost);
