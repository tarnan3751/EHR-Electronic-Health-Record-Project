using Ehr.Data.Audit;
using Ehr.Data.Forms;
using Ehr.Data.Inbox;
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

    public const string FormResponsesTable = "questionnaire_responses";

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

        var requests = sensitive && found.Forms > 0 ? await FormEntriesAsync(patientId, request: null, names: true, cancellationToken) : [];
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

    // One form's answers for the clinician: always a view of sensitive items. Behind the chart's gate, or, with
    // heldInInbox, for a clinician who holds an inbox item for this answer to review, open or done, whoever's patient it
    // is: the item must be the reader's own (and row-level security shows a recipient no one else's), and then the
    // patient's care team is named too, for the page to say whose patient this is. Null when it isn't this patient's,
    // the reader can't see it, or, with heldInInbox, holds no such item.
    public async Task<FormAnswers?> FormAnswersAsync(Guid patientId, Guid responseId, bool heldInInbox, CancellationToken cancellationToken)
    {
        var request = await db.QuestionnaireResponses.AsNoTracking()
            .Where(r => r.Id == responseId && r.PatientId == patientId
                && (!heldInInbox || db.InboxItems.Any(i => i.RecipientId == user.UserId && i.Kind == InboxItemKind.FormAlert && i.PatientId == patientId
                    && i.SourceTable == FormResponsesTable && i.SourceId == r.Id)))
            .Select(r => (Guid?)r.RequestId).SingleOrDefaultAsync(cancellationToken);
        var patient = request is null ? null : await db.Patients.AsNoTracking().SingleOrDefaultAsync(p => p.Id == patientId, cancellationToken);
        if (request is null || patient is null
            || (await FormEntriesAsync(patientId, request, names: true, cancellationToken)).SingleOrDefault() is not { Response: not null } entry)
        {
            return null;
        }

        List<string>? careTeam = heldInInbox
            ? await (
                from assignment in db.PatientAssignments.AsNoTracking()
                join clinician in db.StaffUsers on assignment.ClinicianId equals clinician.Id
                where assignment.PatientId == patientId && assignment.EndedAt == null
                orderby clinician.DisplayName
                select clinician.DisplayName).ToListAsync(cancellationToken)
            : null;
        audit.Record(AuditActions.ReadSensitive, patientId, PatientsTable, patientId);
        await db.SaveChangesAsync(cancellationToken);
        return new FormAnswers(patient, entry, careTeam);
    }

    // The portal's pre-visit forms, for the record the session is looking at: every form row-level security gives it,
    // newest appointment first. A session that may not see sensitive items is given none, exactly as for a record with no
    // forms. One recorded read of the patient: a view of sensitive items when the session may see them, which opening
    // this page is the asking for, and a plain read when it may not. Null when the session may read no such patient.
    public async Task<PortalForms?> PortalFormsAsync(Guid patientId, bool maySeeSensitive, CancellationToken cancellationToken)
    {
        var patient = await db.Patients.AsNoTracking().SingleOrDefaultAsync(p => p.Id == patientId, cancellationToken);
        if (patient is null)
        {
            return null;
        }

        var forms = await FormEntriesAsync(patientId, request: null, names: false, cancellationToken);
        audit.Record(maySeeSensitive ? AuditActions.ReadSensitive : AuditActions.Read, patientId, PatientsTable, patientId);
        await db.SaveChangesAsync(cancellationToken);
        return new PortalForms(patient, forms);
    }

    // One pre-visit form of the record the session is looking at, to fill in or to read its answers: a view of sensitive
    // items. Null, with nothing read or recorded but this one statement, when the session can't see it: a form that
    // doesn't exist, another patient's, and one a proxy without sensitive items may not see are all the same nothing.
    public async Task<FormEntry?> PortalFormAsync(Guid patientId, Guid requestId, CancellationToken cancellationToken)
    {
        if ((await FormEntriesAsync(patientId, requestId, names: false, cancellationToken)).SingleOrDefault() is not { } form)
        {
            return null;
        }

        audit.Record(AuditActions.ReadSensitive, patientId, PatientsTable, patientId);
        await db.SaveChangesAsync(cancellationToken);
        return form;
    }

    // How many of the record's forms can be filled in now, for the portal's home: asked for, not answered, before an
    // appointment that is booked or checked in, from 7 days before it until its scheduled end, or, once checked in, while
    // its visit is in progress, up to 12 hours after (the database's rule, which decides when answers are sent). A number
    // about the session's own record, part of the home page's one recorded read; none for a session that may not see
    // sensitive items.
    public async Task<int> OpenFormsAsync(Guid patientId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var latest = now.AddDays(PreVisitForms.OpenDays);
        var earliest = now.AddMinutes(-AppointmentConfiguration.LongestMinutes).AddHours(-PreVisitForms.LateVisitHours);
        var open = await (
            from asked in db.QuestionnaireRequests.AsNoTracking()
            where asked.PatientId == patientId && !db.QuestionnaireResponses.Any(r => r.RequestId == asked.Id)
            join appointment in db.Appointments on asked.AppointmentId equals appointment.Id
            where (appointment.Status == AppointmentStatus.Booked || appointment.Status == AppointmentStatus.Fulfilled)
                && appointment.StartsAt <= latest && appointment.StartsAt > earliest
            select new
            {
                appointment.StartsAt,
                appointment.Minutes,
                appointment.Status,
                VisitInProgress = db.Encounters.Any(e => e.Id == appointment.EncounterId && e.Status == EncounterStatus.InProgress),
            }).ToListAsync(cancellationToken);
        return open.Count(a => a.StartsAt.AddMinutes(a.Minutes) > now
            || a.Status == AppointmentStatus.Fulfilled && a.VisitInProgress && a.StartsAt.AddMinutes(a.Minutes).AddHours(PreVisitForms.LateVisitHours) > now);
    }

    // The forms of a patient, or the one asked for, with what the pages show of each: the questionnaire's title, the
    // appointment and whether its visit is in progress, who asked, the copy of the definition, and the answers with their
    // score and who sent them: the reader, the patient's own account, or someone the record is shared with. Read in one
    // statement. With names, for a clinician's page, a proxy is named in a second, by the name their account gave, never by
    // reading another record; without, for the portal, no account's name is read at all.
    async Task<List<FormEntry>> FormEntriesAsync(Guid patientId, Guid? request, bool names, CancellationToken cancellationToken)
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
            orderby appointment.StartsAt descending, questionnaire.Title, asked.Id
            select new
            {
                asked.Id,
                asked.AppointmentId,
                appointment.StartsAt,
                appointment.Minutes,
                appointment.Status,
                VisitInProgress = db.Encounters.Any(e => e.Id == appointment.EncounterId && e.Status == EncounterStatus.InProgress),
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
                    answered.AnsweredBy,
                    answered.ProxyGrantId,
                },
            }).Take(FormLimit).ToListAsync(cancellationToken);
        var proxies = names ? rows.Where(row => row.Response is { ProxyGrantId: not null }).Select(row => row.Response!.AnsweredBy).Distinct().ToList() : [];
        var proxyNames = proxies.Count == 0
            ? []
            : await db.PatientUsers.AsNoTracking().Where(account => proxies.Contains(account.Id))
                .ToDictionaryAsync(account => account.Id, account => account.DisplayName, cancellationToken);
        return [.. rows.Select(row =>
        {
            var definition = QuestionnaireDefinition.Parse(row.Definition);
            var response = row.Response is not { } r ? null : new FormResponse(r.Id, r.Score, r.Band, r.FollowUp, r.Alert, r.AnsweredAt,
                r.AnsweredBy == user.UserId, r.ProxyGrantId is not null,
                r.ProxyGrantId is null || !names ? null : $"{(proxyNames.GetValueOrDefault(r.AnsweredBy) is { } name && !string.IsNullOrWhiteSpace(name) ? name : "Someone")} (proxy)",
                System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(r.Answers) ?? []);
            return new FormEntry(row.Id, row.AppointmentId, row.StartsAt, row.Minutes, row.Status, row.VisitInProgress, row.Clinician, row.QuestionnaireCode,
                row.Title, definition, row.RequestedBy, row.RequestedAt, response);
        })];
    }
}

// A patient's pre-visit forms as the clinician's page shows them: every form when sensitive items were shown, or only
// how many are hidden; and the appointments still to come, for asking for another.
public sealed record PatientForms(Patient Patient, IReadOnlyList<FormEntry> Forms, int Hidden, bool SensitiveShown, IReadOnlyList<AppointmentEntry> Upcoming);

// One pre-visit form: what was asked for which appointment, by whom and when, with the copy of the questionnaire it was
// asked with, and its answers once sent.
public sealed record FormEntry(
    Guid Id, Guid AppointmentId, DateTimeOffset AppointmentStartsAt, int AppointmentMinutes, string AppointmentStatus, bool VisitInProgress,
    string AppointmentClinician, string QuestionnaireCode, string Title, QuestionnaireDefinition Definition, string RequestedBy, DateTimeOffset RequestedAt,
    FormResponse? Response)
{
    // When the patient can fill it in: from 7 days before the appointment until its scheduled end, or, once checked in
    // and while its visit is in progress, until 12 hours after it (the database's rule, db/policies/85-pre-visit-forms.sql,
    // which decides). A visit that ends closes it then.
    public DateTimeOffset OpensAt => AppointmentStartsAt.AddDays(-PreVisitForms.OpenDays);

    public DateTimeOffset ScheduledEnd => AppointmentStartsAt.AddMinutes(AppointmentMinutes);

    public DateTimeOffset ClosesAt => VisitUnderWay ? ScheduledEnd.AddHours(PreVisitForms.LateVisitHours) : ScheduledEnd;

    // Whether the appointment still allows answers at all: booked, or checked in.
    public bool AppointmentHeld => AppointmentStatus is Ehr.Data.Scheduling.AppointmentStatus.Booked or Ehr.Data.Scheduling.AppointmentStatus.Fulfilled;

    // Checked in, and its visit still in progress: open past the scheduled end.
    public bool VisitUnderWay => AppointmentStatus == Ehr.Data.Scheduling.AppointmentStatus.Fulfilled && VisitInProgress;

    // Whether answers can no longer be sent at `now`, or can be, on the pages' reckoning; the database's clock decides when
    // they are.
    public bool ClosedAt(DateTimeOffset now) => !AppointmentHeld || now >= ClosesAt;

    public bool OpenAt(DateTimeOffset now) => Response is null && now >= OpensAt && !ClosedAt(now);
}

// A form's answers: the score, band, follow-up and alert the database worked out, when they were sent, and who sent them:
// whether the reader did, and whether someone the record is shared with did (a proxy), named as "Name (proxy)" only on a
// clinician's page and null in the portal's. Answers maps each question's code to the code of the answer chosen.
public sealed record FormResponse(
    Guid Id, int? Score, string? Band, bool FollowUp, bool Alert, DateTimeOffset AnsweredAt, bool ByReader, bool ByProxy, string? Proxy,
    IReadOnlyDictionary<string, string> Answers);

// A form's answers for the clinician's page, and, when they were opened from the reader's inbox, the clinicians with the
// patient on their panel (null otherwise).
public sealed record FormAnswers(Patient Patient, FormEntry Form, IReadOnlyList<string>? CareTeam);

// The record's pre-visit forms as the portal lists them.
public sealed record PortalForms(Patient Patient, IReadOnlyList<FormEntry> Forms);
