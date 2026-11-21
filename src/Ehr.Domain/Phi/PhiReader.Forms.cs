using Ehr.Data.Audit;
using Ehr.Data.Forms;
using Ehr.Data.Patients;
using Ehr.Data.Scheduling;
using Ehr.Domain.Forms;
using Ehr.Domain.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace Ehr.Domain.Phi;

// The PHI reader's reads of pre-visit forms (the expansion plan's step 13e), kept apart from PhiReader.cs, which every
// step touches. Every form is a sensitive item: a page that shows one records a view of the patient's sensitive items
// (read_sensitive), and one that only counts them records a plain read, as the chart does for its hidden diagnoses.
// Row-level security decides which forms exist for the reader: every one for a clinician; for a portal session, the
// record's own, and only when it may see sensitive items (db/policies/30-rls.sql).
public sealed partial class PhiReader
{
    public const string FormRequestsTable = "questionnaire_requests";

    // The most forms a page lists: more than a patient has in years of visits.
    public const int FormLimit = 100;

    // A patient's pre-visit forms for the clinician's page: with sensitive items shown, every request, newest
    // appointment first, with its answers' score; otherwise only how many there are. And, for asking for another, the
    // patient's booked appointments still to come. One recorded read of the patient, a view of sensitive items when
    // they were shown. Null when the patient isn't the reader's to see.
    public async Task<PatientForms?> FormsAsync(Guid patientId, bool sensitive, CancellationToken cancellationToken)
    {
        var found = await db.Patients.AsNoTracking().Where(p => p.Id == patientId)
            .Select(p => new { Patient = p, Forms = db.QuestionnaireRequests.Count(r => r.PatientId == patientId) })
            .SingleOrDefaultAsync(cancellationToken);
        if (found is null)
        {
            return null;
        }

        var requests = sensitive && found.Forms > 0 ? await FormEntriesAsync(patientId, request: null, cancellationToken) : [];
        // Booked and not yet ended: none is longer than the longest booking, so none that started before that can still
        // be under way.
        var now = DateTimeOffset.UtcNow;
        var since = now.AddMinutes(-AppointmentConfiguration.LongestMinutes);
        var upcoming = (await AppointmentEntriesAsync(
            db.Appointments.Where(a => a.PatientId == patientId && a.Status == AppointmentStatus.Booked && a.StartsAt > since).OrderBy(a => a.StartsAt).Take(10),
            latestFirst: false, cancellationToken)).Where(a => a.StartsAt.AddMinutes(a.Minutes) > now).ToList();
        audit.Record(sensitive ? AuditActions.ReadSensitive : AuditActions.Read, patientId, PatientsTable, patientId);
        await db.SaveChangesAsync(cancellationToken);
        return new PatientForms(found.Patient, requests, sensitive ? 0 : found.Forms, sensitive, upcoming);
    }

    // One form's answers for the clinician: always a view of sensitive items. Null when it isn't this patient's, or the
    // reader can't see it.
    public async Task<FormAnswers?> FormAnswersAsync(Guid patientId, Guid responseId, CancellationToken cancellationToken)
    {
        var request = await db.QuestionnaireResponses.AsNoTracking()
            .Where(r => r.Id == responseId && r.PatientId == patientId).Select(r => (Guid?)r.RequestId).SingleOrDefaultAsync(cancellationToken);
        var patient = request is null ? null : await db.Patients.AsNoTracking().SingleOrDefaultAsync(p => p.Id == patientId, cancellationToken);
        if (request is null || patient is null || (await FormEntriesAsync(patientId, request, cancellationToken)).SingleOrDefault() is not { Response: not null } entry)
        {
            return null;
        }

        audit.Record(AuditActions.ReadSensitive, patientId, PatientsTable, patientId);
        await db.SaveChangesAsync(cancellationToken);
        return new FormAnswers(patient, entry);
    }

    // The forms of a patient, or the one asked for, with what the pages show of each: the questionnaire's title, the
    // appointment, who asked, the copy of the definition, and the answers with their score and who sent them. Read in one
    // statement; a proxy is named by the name their account gave, never by reading another record.
    async Task<List<FormEntry>> FormEntriesAsync(Guid patientId, Guid? request, CancellationToken cancellationToken)
    {
        var rows = await (
            from asked in db.QuestionnaireRequests.AsNoTracking()
            where asked.PatientId == patientId && (request == null || asked.Id == request)
            join appointment in db.Appointments on asked.AppointmentId equals appointment.Id
            join clinician in db.StaffUsers on appointment.ClinicianId equals clinician.Id
            join asker in db.StaffUsers on asked.RequestedBy equals asker.Id
            join questionnaire in db.Questionnaires on asked.QuestionnaireCode equals questionnaire.Code
            join answered in db.QuestionnaireResponses on asked.Id equals answered.RequestId into responses
            from answered in responses.DefaultIfEmpty()
            join account in db.PatientUsers on answered.AnsweredBy equals account.Id into accounts
            from account in accounts.DefaultIfEmpty()
            orderby appointment.StartsAt descending, questionnaire.Title, asked.Id
            select new
            {
                asked.Id,
                asked.AppointmentId,
                appointment.StartsAt,
                appointment.Minutes,
                appointment.Status,
                Clinician = clinician.DisplayName,
                questionnaire.Title,
                asked.QuestionnaireCode,
                asked.Definition,
                RequestedBy = asker.DisplayName,
                asked.RequestedAt,
                Response = answered == null ? null : new
                {
                    answered.Id,
                    answered.Answers,
                    answered.Score,
                    answered.Band,
                    answered.FollowUp,
                    answered.Alert,
                    answered.AnsweredAt,
                    answered.ProxyGrantId,
                    ProxyName = account == null ? null : account.DisplayName,
                },
            }).Take(FormLimit).ToListAsync(cancellationToken);
        return [.. rows.Select(row =>
        {
            var definition = QuestionnaireDefinition.Parse(row.Definition);
            var response = row.Response is not { } r ? null : new FormResponse(r.Id, r.Score, r.Band, r.FollowUp, r.Alert, r.AnsweredAt,
                r.ProxyGrantId is null ? null : $"{(string.IsNullOrWhiteSpace(r.ProxyName) ? "Someone" : r.ProxyName)} (proxy)",
                System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(r.Answers) ?? []);
            return new FormEntry(row.Id, row.AppointmentId, row.StartsAt, row.Minutes, row.Status, row.Clinician, row.QuestionnaireCode, row.Title,
                definition, row.RequestedBy, row.RequestedAt, response);
        })];
    }
}

// A patient's pre-visit forms as the clinician's page shows them: every form when sensitive items were shown, or only
// how many are hidden; and the appointments still to come, for asking for another.
public sealed record PatientForms(Patient Patient, IReadOnlyList<FormEntry> Forms, int Hidden, bool SensitiveShown, IReadOnlyList<AppointmentEntry> Upcoming);

// One pre-visit form: what was asked for which appointment, by whom and when, with the copy of the questionnaire it was
// asked with, and its answers once sent.
public sealed record FormEntry(
    Guid Id, Guid AppointmentId, DateTimeOffset AppointmentStartsAt, int AppointmentMinutes, string AppointmentStatus, string AppointmentClinician,
    string QuestionnaireCode, string Title, QuestionnaireDefinition Definition, string RequestedBy, DateTimeOffset RequestedAt, FormResponse? Response)
{
    // When the patient can fill it in: from 7 days before the appointment until its scheduled end (the database's rule,
    // db/policies/85-pre-visit-forms.sql, which decides).
    public DateTimeOffset OpensAt => AppointmentStartsAt.AddDays(-PreVisitForms.OpenDays);

    public DateTimeOffset ClosesAt => AppointmentStartsAt.AddMinutes(AppointmentMinutes);

    // Whether the appointment still allows answers at all: booked, or checked in.
    public bool AppointmentHeld => AppointmentStatus is Ehr.Data.Scheduling.AppointmentStatus.Booked or Ehr.Data.Scheduling.AppointmentStatus.Fulfilled;
}

// A form's answers: the score, band, follow-up and alert the database worked out, when they were sent, and, when a proxy
// sent them, who: null for the patient. Answers maps each question's code to the code of the answer chosen.
public sealed record FormResponse(
    Guid Id, int? Score, string? Band, bool FollowUp, bool Alert, DateTimeOffset AnsweredAt, string? Proxy, IReadOnlyDictionary<string, string> Answers);

public sealed record FormAnswers(Patient Patient, FormEntry Form);
