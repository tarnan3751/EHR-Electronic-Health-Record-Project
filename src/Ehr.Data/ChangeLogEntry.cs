namespace Ehr.Data;

// A row that a transaction inserted or updated in a synced table. Only the trigger in db/policies/20-change-log.sql
// writes these; sync pulls read them by transaction ID (ChangeLog).
public class ChangeLogEntry
{
    public long Id { get; set; }

    // The writing transaction's ID (xid8), from the column default pg_current_xact_id().
    public ulong TransactionId { get; set; }

    public required string TableName { get; set; }

    public Guid RowId { get; set; }
}
