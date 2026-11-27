using System.Net;
using Ehr.Web.CloudAudit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.ProxyTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-11
// Purpose / scenario: What a proxy can't do. Access that doesn't include messages shows none and sends none; a record
//   that was never shared with them can't be chosen; a proxy can't pass access on; and when the patient removes the
//   access, the proxy's next page is their own record, saying the access ended. Off-site with the clinic's server
//   down, a proxy can still choose a record and read it.
// Type: Functional (invalid input and exceptional condition)
// Requirements: FR-19, FR-18, FR-34
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: Three patients with portal accounts. The first has given the second access to their record without
//   messages, and has one conversation. The third has given nobody access.
// Inputs and system conditions: POST /portal/acting; GET and POST /portal/messages, /portal/messages/new and
//   /portal/access on the on-site app; then GET /portal and POST /portal/acting on the cloud app whose link is down.
// Test data: Subject "Synthetic: private question". A grant ID made up by the test.
// Steps:
//   1. As the proxy viewing the patient's record, open "Messages", request the new-message form, and post a message.
//   2. Post a made-up grant ID, and the ID of a grant the third patient gave to someone else, to /portal/acting.
//   3. Still viewing the patient's record, post the "Share your record" form.
//   4. Off-site with the clinic's server unreachable, go back to the own record, choose the patient's again, and open
//      the home page.
//   5. On-site, the patient removes the access. The proxy opens "Results".
// Expected result: Step 1: the app bar has no "Messages" link; the page says "Your access to this record doesn't
//   include messages." and shows no conversation; the form and the post lead back to that page and nothing is sent.
//   Step 2: both are answered 404 and the proxy still sees the first record. Step 3: nothing is saved, and the page
//   says to go back to their own record. Step 4: both switches work, and the patient's record is shown with the band.
//   Step 5: the proxy lands on their own record with "Access ended. You can no longer see that record. This is your
//   own."; "Records shared with you" is then empty.
// Creator: Tahseen Arnan
public class TC_UC6_11_ProxyLimits(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task A_proxy_is_held_to_the_grant_and_loses_the_record_when_it_is_removed()
    {
        var patient = await PersonAsync(apps, "Synthetic", "Guarded");
        var proxy = await PersonAsync(apps, "Synthetic", "Helper");
        var third = await PersonAsync(apps, "Synthetic", "Elsewhere");
        await MessageTests.StartAsync(patient.Client, "Synthetic: private question", "Synthetic: for my care team only");
        await GiveAsync(patient, proxy.Email);
        await GiveAsync(third, patient.Email);
        var grant = await GrantIdAsync(apps, patient.Patient);
        var notTheirs = await GrantIdAsync(apps, third.Patient);
        await ViewAsync(proxy, grant);

        var messages = await proxy.Client.GetStringAsync("/portal/messages", Cancel);
        Assert.Contains("Your access to this record doesn't include messages.", messages);
        Assert.DoesNotContain("private question", messages);
        Assert.DoesNotContain("href=\"/portal/messages\"", messages);
        using var form = await proxy.Client.GetAsync("/portal/messages/new", Cancel);
        Assert.Equal("/portal/messages", PathOf(form));
        using var sent = await PostFormAsync(proxy.Client, "/portal/messages/new", "/portal/access", ("Subject", "Synthetic: from a proxy"), ("Body", "Synthetic: not allowed"));
        Assert.Equal("/portal/messages", PathOf(sent));
        Assert.Single(await MessageTests.RowsAsync(apps, patient.Patient, "body"));

        foreach (var id in new[] { Guid.NewGuid(), notTheirs })
        {
            using var refused = await PostFormAsync(proxy.Client, "/portal/acting", "/portal/access", ("grantId", id));
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        }

        Assert.Contains("Synthetic Guarded&#x27;s record", await proxy.Client.GetStringAsync("/portal", Cancel));

        var passedOn = await GiveAsync(proxy, third.Email);
        Assert.Contains("Go back to your own to see or change who can see yours.", passedOn);
        Assert.DoesNotContain("name=\"Email\"", passedOn);
        Assert.Single(await GrantsAsync(apps, patient.Patient));
        Assert.Empty(await GrantsAsync(apps, proxy.Patient));

        Assert.False(await apps.CloudCutOff.Services.GetRequiredService<CloudAuditShipper>().ShipAsync(Cancel));
        var cutOff = Client(apps.CloudCutOff, proxy.Jar);
        using var mine = await PostFormAsync(cutOff, "/portal/acting?handler=Mine", "/portal");
        Assert.Contains("<h1>My record</h1>", await ReadAsync(mine));
        using var again = await PostFormAsync(cutOff, "/portal/acting", "/portal/access", ("grantId", grant));
        var offSite = await ReadAsync(again);
        Assert.Contains("Synthetic Guarded&#x27;s record", offSite);
        Assert.Contains("data-acting", offSite);
        Assert.Contains("Sharing can't be changed until the clinic's server is back.", Words(await Client(apps.CloudCutOff, patient.Jar).GetStringAsync("/portal/access", Cancel)));

        using var removed = await PostFormAsync(patient.Client, "/portal/access?handler=Revoke", "/portal/access", ("grantId", grant));
        Assert.Contains("Sharing removed.", await ReadAsync(removed));
        using var after = await proxy.Client.GetAsync("/portal/results", Cancel);
        Assert.Equal("/portal", PathOf(after));
        var ended = await ReadAsync(after);
        Assert.Contains("Access ended. You can no longer see that record. This is your own.", Words(ended));
        Assert.Contains("<dd>Synthetic Helper</dd>", ended);
        Assert.DoesNotContain("data-acting", ended);
        Assert.Contains("Nobody has shared a record with you.", await proxy.Client.GetStringAsync("/portal/access", Cancel));
    }
}
