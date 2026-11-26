namespace Ehr.Data.Audit;

// Which server recorded an AuditEvent. On-prem events are written in the transaction that did the work; cloud
// events are queued on the cloud host and shipped to the on-prem app, so they arrive later and say so. Lab events are
// on-prem ones that came through the lab's door (the expansion plan's step 12, db/policies/76-lab.sql): no account is
// behind them, so they have no actor.
public static class AuditSources
{
    public const string OnPrem = "onprem";

    public const string Cloud = "cloud";

    // Orders handed to the lab, and what its results wrote, recorded by the database.
    public const string Lab = "lab";
}
