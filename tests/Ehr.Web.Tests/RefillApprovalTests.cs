using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.RefillRequestTests;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests;

// Approving a refill request (step 13d), on the second review's findings that the test cases don't cover: a request
// whose medication's entry was changed on the chart since it was asked (its drug code with it) is only declined. The
// review approved a request for amlodipine 5 mg with 10 mg, and the portal said so.
public class RefillApprovalTests(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_request_whose_medication_was_changed_since_it_was_asked_is_only_declined()
    {
        var (_, session, patient) = await PortalPatientAsync(apps, "Changed");
        var amlodipine = await MedicationAsync(apps, patient.Patient, "amlodipine 5 MG Oral Tablet", "197361");
        using (var asked = await AskAsync(patient.Client, "amlodipine 5 MG Oral Tablet"))
        {
            Assert.Equal(PortalPath, PathOf(asked));
        }

        var request = await RequestIdAsync(apps, patient.Patient);
        var formBefore = await session.Client.GetStringAsync(ApprovePath(patient.Patient, request), Cancel);

        // The clinician changes the chart's entry to the 10 mg tablet, from the medication list's form.
        using (var changed = await ChartListTests.SaveAsync(session.Client, patient.Patient, "medications", amlodipine,
            ("Name", "amlodipine 10 MG Oral Tablet"), ("Status", "active")))
        {
            Assert.True(changed.IsSuccessStatusCode);
        }

        Assert.Equal(["308135"], await ProxyTests.QueryAsync(apps, $"SELECT code FROM ehr.medication_statements WHERE id = '{amlodipine}' AND patient_id = @patient",
            patient.Patient));

        var page = await session.Client.GetStringAsync(ReviewPath(patient.Patient, request), Cancel);
        Assert.Contains("Can't be approved: the medication on the chart has changed since this was asked.", Said(page));
        Assert.DoesNotContain("data-refill-approve", page);
        using (var form = await session.Client.GetAsync(ApprovePath(patient.Patient, request), Cancel))
        {
            Assert.Equal(ReviewPath(patient.Patient, request), PathOf(form));
        }

        // The form opened before the change, posted after it with the new product: nothing is ordered.
        using (var refused = await session.Client.PostAsync(ApprovePath(patient.Patient, request), Form(("__RequestVerificationToken", Token(formBefore)),
            ("Item", "amlodipine 10 MG Oral Tablet"), ("SafetySeen", "true"), ("Details[dose]", "10 mg"), ("Details[route]", "By mouth"),
            ("Details[frequency]", "Once a day")), Cancel))
        {
            Assert.Equal(System.Net.HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains("this refill request can't be approved", Said(await refused.Content.ReadAsStringAsync(Cancel)));
        }

        Assert.Equal(["0"], await ProxyTests.QueryAsync(apps, "SELECT count(*)::text FROM ehr.orders WHERE patient_id = @patient", patient.Patient));
        using (var declined = await DeclineAsync(session.Client, patient.Patient, request, "Synthetic: your dose changed; please book a visit."))
        {
            Assert.Contains("Declined:", Said(await ReadAsync(declined)));
        }

        Assert.StartsWith("rejected|amlodipine 5 MG Oral Tablet|", (await RequestsAsync(apps, patient.Patient))[0], StringComparison.Ordinal);
    }
}
