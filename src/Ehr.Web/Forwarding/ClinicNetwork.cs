using Ehr.Domain.Notes;

namespace Ehr.Web.Forwarding;

// Whether a request is served at the clinic, for what only the clinic's network may see or do (the expansion plan's step
// 16): listening to a visit, and a visit's transcript and the decisions on it while its review is open. The on-prem app
// answers it (NoteWriteAuthority), and the cloud app didn't forward it: a request carrying AccountRateLimits'
// ForwardedHeader at all counts as off-site, whether or not its proof verifies. The cloud app puts the header on
// everything it forwards (CloudForwardingMiddleware; it can't run without the shared key), so this fails closed: with
// Ehr:CloudAudit:Key missing on the on-prem app, or different on the two apps, what the cloud app forwards still never
// takes the conversation into a response that crosses the WAN, nor decides on it. A client on the clinic's network that
// adds the header only refuses itself. No proof is compared, so none can be compared badly.
public static class ClinicNetwork
{
    public static bool AtTheClinic(HttpRequest request, NoteWriteAuthority authority) =>
        authority.IsPrimaryHost && !request.Headers.ContainsKey(AccountRateLimits.ForwardedHeader);
}
