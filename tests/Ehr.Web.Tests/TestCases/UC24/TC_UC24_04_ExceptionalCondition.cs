using System.Net;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.EmergencyAccessFlows;

namespace Ehr.Web.Tests.TestCases.UC24;

// Test Case ID: TC-UC24-04
// Purpose / scenario: Reviewing off-site (the expansion plan's step 18). The cloud app shows the review page from its
//   copy and forwards the review to the clinic's server, which saves it. With the clinic's server cut off, the page is
//   still readable, the form is replaced by a sentence that says why, and a review posted as the link drops gets the "Not
//   saved" page with the note typed shown again to copy.
// Type: Functional (exceptional condition)
// Requirements: proposed FR-64, FR-33, FR-34
// Use case: UC 24, Review an emergency access
// Preconditions: Dr. Remote Access used emergency access, now ended; the administrator Remote Reviewer is signed in on
//   the cloud app, connected, and then on the cloud app cut off from the clinic's server.
// Inputs and system conditions: GET and POST /admin/emergency-access/{id} on the cloud app; the same on the cut-off app.
// Test data: Note "Synthetic: checked the rota, the clinician was covering the ward."; "Synthetic: typed as the link
//   dropped."
// Steps:
//   1. On the cloud app, open the access and review it.
//   2. On the cut-off app, open another ended access; post a review to it anyway.
// Expected result: Step 1: the page reads as on-site; the post crosses the WAN link and the review is saved. Step 2:
//   "Marking an access reviewed needs the clinic's server. The access remains readable.", no form; the post answers 503
//   with the "Not saved" page showing the note; nothing saved.
// Creator: Tahseen Arnan
public class TC_UC24_04_ExceptionalCondition(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Off_site_the_review_is_forwarded_and_cut_off_it_waits_and_keeps_what_was_typed()
    {
        var account = await apps.CreateStaffAsync("Remote Reviewer", StaffRoles.Admin);
        var reviewer = await SignInAsync(apps.Cloud, account);
        var access = await GrantAsync(apps, "Dr. Remote Access");
        await EndAsync(apps, access.Id);

        // 1.
        var page = await reviewer.Client.GetStringAsync($"/admin/emergency-access/{access.Id}", Cancel);
        Assert.Contains("data-review-form", page);
        using (var reviewed = await ReviewAsync(reviewer.Client, access.Id, EmergencyAccessOutcomes.Appropriate, Note))
        {
            Assert.Equal(HttpStatusCode.OK, reviewed.StatusCode);
        }

        Assert.Contains($"POST /admin/emergency-access/{access.Id}", apps.Forwarded);
        Assert.Equal([$"{account.Id} appropriate {Note}"], await ReviewsAsync(apps, access.Id));

        // 2.
        var other = await GrantAsync(apps, "Dr. Remote Again");
        await EndAsync(apps, other.Id);
        var cutOff = Client(apps.CloudCutOff, reviewer.Jar);
        var readOnly = await cutOff.GetStringAsync($"/admin/emergency-access/{other.Id}", Cancel);
        Assert.Contains("Marking an access reviewed needs the clinic's server. The access remains readable.", readOnly);
        Assert.DoesNotContain("data-review-form", readOnly);
        using (var request = new HttpRequestMessage(HttpMethod.Post, $"/admin/emergency-access/{other.Id}")
        {
            Content = Form(("Outcome", EmergencyAccessOutcomes.Appropriate), ("Note", "Synthetic: typed as the link dropped."), ("__RequestVerificationToken", Token(readOnly))),
        })
        {
            request.Headers.Add("Sec-Fetch-Site", "same-origin");
            using var notSaved = await cutOff.SendAsync(request, Cancel);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, notSaved.StatusCode);
            Assert.Contains("Synthetic: typed as the link dropped.", await notSaved.Content.ReadAsStringAsync(Cancel));
        }

        Assert.Empty(await ReviewsAsync(apps, other.Id));
    }
}
