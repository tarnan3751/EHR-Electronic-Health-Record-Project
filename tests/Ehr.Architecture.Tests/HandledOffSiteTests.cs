using Ehr.Web.Forwarding;
using Xunit;

namespace Ehr.Architecture.Tests;

// The cloud app handles a HandledOffSite page's POST itself, and whatever that records goes to its audit queue, which
// takes only what the on-prem app takes (CloudAuditEvents). Today that is signing out, staff and patient, and nothing
// else; a new page here needs its event's shape added there too.
public class HandledOffSiteTests
{
    [Fact]
    public void Only_the_sign_out_pages_are_handled_off_site()
    {
        var marked = typeof(Ehr.Web.Site).Assembly.GetTypes()
            .Where(type => type.IsDefined(typeof(HandledOffSiteAttribute), inherit: false))
            .Select(type => type.FullName)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            marked.SequenceEqual(["Ehr.Web.Areas.Clinical.Pages.Account.SignOutModel", "Ehr.Web.Areas.Portal.Pages.Account.SignOutModel"]),
            "Only the staff and patient sign-out pages are handled on the cloud app. A new page handled there records its "
                + "events into the cloud app's queue, so add their shape to CloudAuditEvents (both the queue and the on-prem "
                + "app use it) and then add the page here; otherwise the request fails. Marked now: " + string.Join(", ", marked));
    }
}
