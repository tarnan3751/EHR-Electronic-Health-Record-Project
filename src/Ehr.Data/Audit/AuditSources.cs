namespace Ehr.Data.Audit;

// Which server recorded an AuditEvent. On-prem events are written in the transaction that did the work; cloud
// events are queued on the cloud host and shipped to the on-prem app, so they arrive later and say so.
public static class AuditSources
{
    public const string OnPrem = "onprem";

    public const string Cloud = "cloud";
}
