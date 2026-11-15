namespace Ehr.Web.Forwarding;

// Whether the on-prem app can save, as the cloud app last found. CloudAuditShipper decides, every couple of seconds:
// an answer that isn't a server error (which is how the on-prem app answers with its database down) means it can.
// Forwarding only marks it unreachable, when a request gets no answer at all. While it can't, every page says the
// system is read-only (Pages/Shared/_Layout.cshtml). The on-prem app registers one too, which nothing ever marks
// unreachable.
public sealed class OnPremStatus
{
    volatile bool reachable = true;

    public bool IsReachable => reachable;

    public void Report(bool reachable) => this.reachable = reachable;
}
