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
    // Exactly what the cloud app records: PhiReader reads of patients, private drafts/reviews, signed notes, message
    // threads or visit summaries (forced RLS decides visibility), or of the patient's part of the audit log, the portal's
    // "Who viewed my record" (step 13c), which names no row; a patient's record downloaded from the portal (step 13b); or
    // a sign-out as StaffSignIn and PatientSignIn record it, with the patient for a patient's. All by an actor, none with
    // a reason or details. The JSON the on-prem app reads can hold null where an event should be.
    public static bool IsAllowed([NotNullWhen(true)] AuditEvent? e) =>
        e is not null
        && e.Id != Guid.Empty
        && e.ActorId is { } actor && actor != Guid.Empty
        && e.Reason is null
        && e.Details is null
        && e.OccurredAt != default
        && e.Action switch
        {
            AuditActions.Read => e.PatientId is { } patient && patient != Guid.Empty
                && (e.TableName == PhiReader.PatientsTable && e.RowId == patient
                    || e.TableName is "clinical_notes" or "clinical_drafts" or "ai_note_reviews" or "ai_field_proposals" or PhiReader.ThreadsTable or PhiReader.SummariesTable
                        && e.RowId is { } row && row != Guid.Empty
                    || e.TableName == PhiReader.AuditTable && e.RowId is null),
            // Asking the chart for a patient's sensitive items, off-site as on-site; and a portal download of the record,
            // with them or without.
            AuditActions.ReadSensitive or AuditActions.Download or AuditActions.DownloadSensitive =>
                e.PatientId is { } shown && shown != Guid.Empty && e.TableName == PhiReader.PatientsTable && e.RowId == shown,
            AuditActions.SignOut => e.TableName is null && e.RowId is null && e.PatientId != Guid.Empty,
            _ => false,
        };
}
