using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.RefillRequestTests;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests.TestCases.UC15;

// Test Case ID: TC-UC15-08
// Purpose / scenario: A refill request arrives in the inbox as a "Refill request" item, saying only that a patient asked
//   to renew a medication, with the patient and a link to the request; it is not ticked off from the list, only done by
//   answering the request, which takes it off the list and puts it under Done.
// Type: Functional (alternative flow: a refill request)
// Requirements: proposed FR-41, proposed FR-58
// Use case: UC 15, Work through the inbox
// Preconditions: Dr. Test has the patient on their panel, signed in on the on-site app. The patient has asked to renew
//   lisinopril from the portal.
// Inputs and system conditions: GET /inbox; POST /inbox?handler=Done for the item, as a crafted request; POST
//   /patients/{id}/refills/{request}?handler=Decline; GET /inbox?done=true.
// Test data: lisinopril 10 MG Oral Tablet.
// Steps:
//   1. Open the inbox.
//   2. Post "Mark done" for the item.
//   3. Decline the request; open the inbox, and its Done tab.
// Expected result: Step 1: one "Refill request" item, "A patient asked to renew a medication", naming no medication,
//   with "Review the request" and no "Mark done" button; the app bar counts 1. Step 2: "Nothing to mark:", and the item
//   still waits. Step 3: nothing needs action, and Done lists the item.
// Creator: Tahseen Arnan
public class TC_UC15_08_RefillDoneByAnswering(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_refill_request_is_done_by_answering_it_not_from_the_list()
    {
        var (_, session, patient) = await PortalPatientAsync(apps, "Inboxed");
        await MedicationAsync(apps, patient.Patient);
        using (var asked = await AskAsync(patient.Client, Lisinopril))
        {
            Assert.Equal(PortalPath, PathOf(asked));
        }

        var request = await RequestIdAsync(apps, patient.Patient);
        var item = (await ProxyTests.QueryAsync(apps,
            $"SELECT id::text FROM ehr.inbox_items WHERE patient_id = @patient AND source_id = '{request}'", patient.Patient)).Single();

        var inbox = await session.Client.GetStringAsync("/inbox", Cancel);
        var words = Said(inbox);
        Assert.Contains("Refill request A patient asked to renew a medication", words);
        Assert.Contains("Review the request", words);
        Assert.DoesNotContain("Mark done", words);
        Assert.DoesNotContain("lisinopril", words, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("data-inbox-count", inbox);

        using (var ticked = await PostFormAsync(session.Client, $"/inbox?handler=Done&id={item}", "/inbox"))
        {
            Assert.Contains("Nothing to mark:", Said(await ReadAsync(ticked)));
        }

        Assert.All(await ItemsAsync(apps, patient.Patient, request), open => Assert.EndsWith(" false", open));

        using (var declined = await DeclineAsync(session.Client, patient.Patient, request))
        {
            Assert.Contains("Declined:", Said(await ReadAsync(declined)));
        }

        Assert.Contains("Nothing needs your action.", Said(await session.Client.GetStringAsync("/inbox", Cancel)));
        Assert.Contains("A patient asked to renew a medication", Said(await session.Client.GetStringAsync("/inbox?done=true", Cancel)));
    }
}
