namespace Ehr.Data;

// A sync push operation that was applied, under the idempotency key its client gave it, so the same operation sent
// again (say, after a lost response) isn't applied twice. Saved in the same transaction as the change itself.
public class SyncOperation
{
    public Guid IdempotencyKey { get; set; }

    // The row the operation saved.
    public Guid RowId { get; set; }

    public DateTimeOffset AppliedAt { get; set; }
}
