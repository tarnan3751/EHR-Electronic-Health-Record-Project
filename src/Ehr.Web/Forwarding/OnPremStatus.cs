namespace Ehr.Web.Forwarding;

// Whether the cloud app last reached the on-prem app, from its forwarding and from CloudAuditShipper, which tries every
// couple of seconds. While it can't, every page says the system is read-only (Pages/Shared/_Layout.cshtml). The
// on-prem app registers one too, which nothing ever marks unreachable.
public sealed class OnPremStatus
{
    volatile bool reachable = true;

    public bool IsReachable => reachable;

    public void Report(bool reachable) => this.reachable = reachable;
}
