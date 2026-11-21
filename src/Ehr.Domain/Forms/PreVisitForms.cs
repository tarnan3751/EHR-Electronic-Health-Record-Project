using System.Text.Json;
using Ehr.Data;
using Ehr.Data.Forms;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ehr.Domain.Forms;

// Asking for a pre-visit form and sending its answers (the expansion plan's step 13e). Called inside the page's
// database transaction. Reads nothing: forms are read through PhiReader. Who may ask (a clinician, for a patient on their
// panel, before a booked appointment ends) and who may answer (the portal session of the record, with sensitive items,
// while the form is open) is the pages' rule and the database's, which also fills in every other column, scores the
// answers and refuses any change afterwards (db/policies/85-pre-visit-forms.sql). Each write is in a savepoint of its
// own, so a refusal leaves the page's transaction usable.
public sealed class PreVisitForms(EhrDbContext db)
{
    // A form opens this many days before its appointment, and closes at the appointment's scheduled end: the database's
    // rule, which this only shows.
    public const int OpenDays = 7;

    // The questionnaires a clinician can ask for, by code and title: reference data, not PHI.
    public async Task<IReadOnlyList<(string Code, string Title)>> QuestionnairesAsync(CancellationToken cancellationToken) =>
        [.. (await db.Questionnaires.AsNoTracking().OrderBy(q => q.Title).Select(q => new { q.Code, q.Title }).ToListAsync(cancellationToken))
            .Select(q => (q.Code, q.Title))];

    // A clinician asks for a questionnaire before an appointment. The patient sent is the page's; the database takes the
    // appointment's, and the questionnaire's definition as it is now.
    public Task<FormResult> AskAsync(Guid patient, Guid appointment, string questionnaire, CancellationToken cancellationToken) =>
        SaveAsync(new QuestionnaireRequest
        {
            Id = Guid.CreateVersion7(),
            PatientId = patient,
            AppointmentId = appointment,
            QuestionnaireCode = questionnaire,
        }, cancellationToken);

    // The portal session sends a form's answers: each question's code and the code of the answer chosen. The patient
    // sent is the session's; the database takes the request's, and works out the score.
    public Task<FormResult> SendAsync(Guid patient, Guid request, IReadOnlyDictionary<string, string> answers, CancellationToken cancellationToken) =>
        SaveAsync(new QuestionnaireResponse
        {
            Id = Guid.CreateVersion7(),
            RequestId = request,
            PatientId = patient,
            QuestionnaireCode = "",
            Answers = JsonSerializer.Serialize(answers),
        }, cancellationToken);

    async Task<FormResult> SaveAsync(object row, CancellationToken cancellationToken)
    {
        const string savepoint = "pre_visit_form";
        var transaction = db.Database.CurrentTransaction!;
        await transaction.CreateSavepointAsync(savepoint, cancellationToken);
        var entry = db.Add(row);
        var result = FormResult.Saved;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // That questionnaire is already asked for at that appointment, or the form's answers were already sent.
            result = FormResult.AlreadyDone;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException
        {
            // Refused by row-level security or a guard: not the reader's to ask or answer, not open, or answers that
            // aren't the form's. The database's refusal never says which.
            SqlState: PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.CheckViolation or PostgresErrorCodes.ForeignKeyViolation,
        })
        {
            result = FormResult.Refused;
        }
        finally
        {
            entry.State = EntityState.Detached;
        }

        if (result == FormResult.Saved)
        {
            await transaction.ReleaseSavepointAsync(savepoint, cancellationToken);
        }
        else
        {
            await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
        }

        return result;
    }
}

public enum FormResult
{
    Saved,

    // Already asked for at that appointment, or already answered.
    AlreadyDone,

    Refused,
}
