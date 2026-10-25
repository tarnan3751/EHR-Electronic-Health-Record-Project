namespace Ehr.Data.Audit;

// One view or change of patient information, or a sign-in event: who, what, which row, which patient, from which
// server, and why when a reason is required (break-glass). Append-only: no runtime role may update or delete a row
// (db/policies/10-grants.sql). Writes to PHI tables are recorded by a trigger that runs as the table owner; reads are
// recorded by the PHI reader in Ehr.Domain in the same transaction as the read, or, on the cloud site, queued and
// shipped to the on-prem app, which inserts them with source "cloud".
public class AuditEvent
{
    public long Id { get; set; }

    // When it happened: the database's clock on-prem (the column default), the cloud app's clock for shipped events.
    public DateTimeOffset OccurredAt { get; set; }

    // The transaction that wrote the event (xid8), from the column default pg_current_xact_id(), so a request's read
    // and its writes can be tied together.
    public ulong TransactionId { get; set; }

    // The staff or patient account acting. Null for the system (the bootstrap admin) and for anonymous requests.
    public Guid? ActorId { get; set; }

    // One of AuditActions.
    public required string Action { get; set; }

    public string? TableName { get; set; }

    public Guid? RowId { get; set; }

    // The patient the row belongs to, repeated here so "who looked at this patient" is one index scan.
    public Guid? PatientId { get; set; }

    // One of AuditSources.
    public required string Source { get; set; }

    // The stated reason, for emergency access.
    public string? Reason { get; set; }

    // What changed, as JSON: the old and new values of the columns a write changed.
    public string? Details { get; set; }
}
