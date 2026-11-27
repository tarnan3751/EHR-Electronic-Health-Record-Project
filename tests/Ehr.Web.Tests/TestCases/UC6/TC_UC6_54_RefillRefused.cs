using System.Net;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.RefillRequestTests;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-54
// Purpose / scenario: A request the portal can't make is refused with a sentence and nothing saved: a medication
//   reference that wasn't given by this record's page (made up, or another patient's), a patient ID slipped into the
//   form, a second request while the first waits, a medication stopped after the page was opened, a note over 500
//   characters, and a record whose care team left after the page was opened. A record with no care team gets no form.
// Type: Functional (invalid input)
// Requirements: proposed FR-58; AGENTS.md: portal handlers never accept a patient ID
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: Two patients with portal accounts, signed in on the on-site app, both on Dr. Test's panel; a third, on
//   nobody's panel.
// Inputs and system conditions: POST /portal/refills with each input below, on the on-site app.
// Test data: lisinopril 10 MG Oral Tablet and amlodipine 5 MG Oral Tablet on each chart.
// Steps:
//   1. Post "x" as the medication; post the other patient's reference for lisinopril.
//   2. Post the patient's own reference with the other patient's ID as PatientId.
//   3. Post lisinopril again.
//   4. Open the page, then have amlodipine stopped, then post it.
//   5. Post amlodipine (active again) with a note of 501 characters.
//   6. Open the page, then end Dr. Test's assignment, then post.
//   7. As the third patient, open the page.
// Expected result: Steps 1, 3, 4, 5 and 6 answer 422 with "Not sent." and the sentence that applies; step 3 says the
//   request is waiting already. Step 2 saves the request for the signed-in patient alone. Step 7 says "No care team
//   yet." and offers no form. The other patient has no request, and nothing else was saved.
// Creator: Tahseen Arnan
public class TC_UC6_54_RefillRefused(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task A_request_the_portal_can_not_make_is_refused_and_nothing_is_saved()
    {
        var (clinician, _, patient) = await PortalPatientAsync(apps, "Refused");
        var other = await ProxyTests.PersonAsync(apps, "Synthetic", "RefusedOther");
        await apps.AssignAsync(clinician, other.Patient);
        await MedicationAsync(apps, patient.Patient);
        var amlodipine = await MedicationAsync(apps, patient.Patient, "amlodipine 5 MG Oral Tablet", "197361");
        await MedicationAsync(apps, other.Patient);
        var theirs = ReferenceOf(await other.Client.GetStringAsync(PortalPath, Cancel), Lisinopril);

        async Task<string> PostAsync(string? reference, string note = "", params (string Name, object Value)[] more)
        {
            var html = await patient.Client.GetStringAsync(PortalPath, Cancel);
            using var posted = await patient.Client.PostAsync(PortalPath,
                Form([("Medication", reference ?? ReferenceOf(html, Lisinopril)), ("Note", note), ("__RequestVerificationToken", Token(html)), .. more]), Cancel);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, posted.StatusCode);
            var words = Words(await posted.Content.ReadAsStringAsync(Cancel));
            Assert.Contains("Not sent.", words);
            return words;
        }

        Assert.Contains("Choose a medication from the list.", await PostAsync("x"));
        Assert.Contains("Choose a medication from the list.", await PostAsync(theirs));
        Assert.Empty(await RequestsAsync(apps, patient.Patient));

        var html = await patient.Client.GetStringAsync(PortalPath, Cancel);
        using (var sent = await patient.Client.PostAsync(PortalPath, Form(("Medication", ReferenceOf(html, Lisinopril)), ("Note", ""), ("PatientId", other.Patient),
            ("__RequestVerificationToken", Token(html))), Cancel))
        {
            Assert.Equal(PortalPath, PathOf(sent));
        }

        Assert.Equal([$"requested|{Lisinopril}|-|{patient.Account}|-|-|-"], await RequestsAsync(apps, patient.Patient));
        Assert.Empty(await RequestsAsync(apps, other.Patient));

        // A reference from a page opened before the first request: the medication is waiting already.
        var reference = ReferenceOf(html, Lisinopril);
        Assert.Contains("You've already asked for a refill of this medication. Your care team hasn't answered yet.", await PostAsync(reference));

        var stale = ReferenceOf(await patient.Client.GetStringAsync(PortalPath, Cancel), "amlodipine 5 MG Oral Tablet");
        await SetStatusAsync(amlodipine, "stopped");
        Assert.Contains("That medication can't be refilled here any more. Choose one from the list.", await PostAsync(stale));
        await SetStatusAsync(amlodipine, "active");

        Assert.Contains("Shorten the note to 500 characters or fewer.",
            await PostAsync(ReferenceOf(await patient.Client.GetStringAsync(PortalPath, Cancel), "amlodipine 5 MG Oral Tablet"), new string('x', 501)));

        var beforeLeaving = ReferenceOf(await patient.Client.GetStringAsync(PortalPath, Cancel), "amlodipine 5 MG Oral Tablet");
        await ProxyTests.QueryAsync(apps,
            "WITH ended AS (UPDATE ehr.patient_assignments SET ended_at = now() WHERE patient_id = @patient AND ended_at IS NULL RETURNING id::text) SELECT id FROM ended", patient.Patient);
        Assert.Contains("This record has no care team yet. To ask for a refill, call the clinic.", await PostAsync(beforeLeaving));
        Assert.Contains("No care team yet.", Words(await patient.Client.GetStringAsync(PortalPath, Cancel)));
        Assert.Single(await RequestsAsync(apps, patient.Patient));

        var alone = await ProxyTests.PersonAsync(apps, "Synthetic", "RefusedAlone");
        await MedicationAsync(apps, alone.Patient);
        var page = await alone.Client.GetStringAsync(PortalPath, Cancel);
        Assert.Contains("No care team yet. To ask for a refill, call the clinic.", Words(page));
        Assert.DoesNotContain("name=\"Medication\"", page);
    }

    async Task SetStatusAsync(Guid medication, string status) =>
        await ProxyTests.QueryAsync(apps,
            $"WITH changed AS (UPDATE ehr.medication_statements SET status = '{status}' WHERE id = @patient RETURNING id::text) SELECT id FROM changed", medication);
}
