using System.Diagnostics.CodeAnalysis;
using Ehr.Data.Audit;
using Ehr.Domain.Phi;

namespace Ehr.Web.CloudAudit;

// What the cloud app may record: the events the on-prem app takes from it (CloudAuditApi), and so the only ones its
// queue takes (CloudAuditQueue). One rule for both sides, because the shipper sends a refused batch again forever: an
// event the on-prem app wouldn't take must fail the request that records it, not jam the queue behind it. A new kind of
// event recorded on the cloud app, such as on another HandledOffSite page, needs its shape here first.
public static class CloudAuditEvents
{
    // Exactly what the cloud app records: PhiReader reads of patients, private drafts/reviews and their proposals, signed
    // notes, message threads or visit summaries (forced RLS decides visibility), or of the patient's part of the audit
    // log, the portal's "Who viewed my record" (step 13c), which names no row; a patient's record downloaded from the
    // portal (step 13b); or a sign-out as StaffSignIn and PatientSignIn record it, with the patient for a patient's. All
    // by an actor, none with a reason or details. A read says which part of the record it was for (step 13c2), one the
    // PHI reader records that kind of read with (Parts), or none, as a cloud app older than that step queued it. The JSON
    // the on-prem app reads can hold null where an event should be.
    public static bool IsAllowed([NotNullWhen(true)] AuditEvent? e) =>
        e is not null
        && e.Id != Guid.Empty
        && e.ActorId is { } actor && actor != Guid.Empty
        && e.Reason is null
        && e.Details is null
        && e.OccurredAt != default
        && (e.Part is null || Parts.TryGetValue((e.Action, e.TableName ?? ""), out var parts) && parts.Contains(e.Part))
        && e.Action switch
        {
            AuditActions.Read => e.PatientId is { } patient && patient != Guid.Empty
                && (e.TableName == PhiReader.PatientsTable && e.RowId == patient
                    || e.TableName is "clinical_notes" or "clinical_drafts" or "ai_note_reviews" or "ai_field_proposals" or "ai_chart_proposals"
                        or PhiReader.ThreadsTable or PhiReader.SummariesTable
                        && e.RowId is { } row && row != Guid.Empty
                    || e.TableName == PhiReader.AuditTable && e.RowId is null),
            // Asking the chart for a patient's sensitive items, off-site as on-site; and a portal download of the record,
            // with them or without.
            AuditActions.ReadSensitive or AuditActions.Download or AuditActions.DownloadSensitive =>
                e.PatientId is { } shown && shown != Guid.Empty && e.TableName == PhiReader.PatientsTable && e.RowId == shown,
            AuditActions.SignOut => e.TableName is null && e.RowId is null && e.PatientId != Guid.Empty,
            _ => false,
        };

    // The parts each kind of read is recorded with (PhiReader), by its action and table: a sign-out has none.
    public static readonly IReadOnlyDictionary<(string Action, string Table), string[]> Parts = new Dictionary<(string, string), string[]>
    {
        [(AuditActions.Read, PhiReader.PatientsTable)] =
        [
            AuditParts.Chart, AuditParts.Lists, AuditParts.Orders, AuditParts.Results, AuditParts.Vitals, AuditParts.Search, AuditParts.Panel,
            AuditParts.Schedule, AuditParts.Inbox, AuditParts.Appointments, AuditParts.Home, AuditParts.Conversations, AuditParts.Access,
            AuditParts.VisitNote, AuditParts.NoteDraft, AuditParts.Offline, AuditParts.Fhir, AuditParts.Summaries, AuditParts.Printable,
            AuditParts.Refills, AuditParts.Forms,
        ],
        [(AuditActions.ReadSensitive, PhiReader.PatientsTable)] =
        [
            AuditParts.Chart, AuditParts.Lists, AuditParts.VisitNote, AuditParts.Fhir, AuditParts.Summaries, AuditParts.Printable, AuditParts.Refills,
            AuditParts.Forms,
        ],
        [(AuditActions.Read, "clinical_notes")] = [AuditParts.Chart, AuditParts.VisitNote, AuditParts.NoteDraft, AuditParts.Offline],
        [(AuditActions.Read, "clinical_drafts")] = [AuditParts.Chart, AuditParts.NoteDraft],
        [(AuditActions.Read, "ai_note_reviews")] = [AuditParts.VisitNote, AuditParts.NoteDraft],
        [(AuditActions.Read, "ai_field_proposals")] = [AuditParts.NoteDraft],
        // A dictation's proposed orders and allergies (step 15): on its review and the forms opened from them, the note being
        // written, and the signed note.
        [(AuditActions.Read, "ai_chart_proposals")] = [AuditParts.VisitNote, AuditParts.NoteDraft],
        [(AuditActions.Read, PhiReader.ThreadsTable)] = [AuditParts.Conversations],
        [(AuditActions.Read, PhiReader.SummariesTable)] = [AuditParts.Summaries, AuditParts.Printable],
        [(AuditActions.Read, PhiReader.AuditTable)] = [AuditParts.Views],
        [(AuditActions.Download, PhiReader.PatientsTable)] = [AuditParts.Download],
        [(AuditActions.DownloadSensitive, PhiReader.PatientsTable)] = [AuditParts.Download],
    };
}
