namespace Ehr.Web.Forwarding;

// Marks a page the cloud app forwards to the on-prem app even for reads. Reading PHI records an audit event, which the
// cloud app can't write: it reads a replica that takes no writes. Until the cloud app keeps its own queue of audit
// events and ships them to on-prem, these pages come from on-prem across the WAN, and are unavailable off-site while
// the on-prem app is unreachable. Quick text and the sign-in pages aren't marked and keep coming from the replica.
[AttributeUsage(AttributeTargets.Class)]
public sealed class RequiresOnPremAttribute : Attribute;
