using System.Net;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.DictationProposalTests;
using static Ehr.Web.Tests.RefillRequestTests;

namespace Ehr.Web.Tests.TestCases.UC4;

// Test Case ID: TC-UC4-34
// Purpose / scenario: An allergy a dictation proposed (step 15) is decided once: opened in two tabs, the second "Save
//   allergy" adds nothing and says so. A proposal decided already sends the form back to the review; an order's proposal
//   doesn't open the allergy form; nor does a proposal with an entry to change, or on another list, or one named on the
//   allergy form of another patient on the same clinician's panel. (One dismissed on the review while its form was
//   open is TC-UC13-11's.)
// Type: Functional (exceptional condition)
// Requirements: FR-14 (reworded), proposed FR-38
// Use case: UC 4, View patient chart
// Preconditions: Dr. Test has the patient on their panel, signed in on the on-site app, and dictated a note whose review
//   proposes an order and an allergy to add.
// Inputs and system conditions: GET and POST /patients/{id}/allergies?proposal={proposal} in two tabs; GET
//   /patients/{id}/allergies/{entry}?proposal= and /patients/{id}/medications?proposal=; on the on-site app.
// Test data: Penicillin G, reaction Hives, in the first tab; Amoxicillin in the second.
// Steps:
//   1. Open the allergy form from the proposal in two tabs; save Penicillin G in the first, Amoxicillin in the second.
//   2. Open the form from the proposal again; from the order's proposal, and save from it; with an entry; on the
//      medications list.
//   3. Open another patient's allergy form, of the clinician's panel too, with a second dictation's allergy proposal;
//      save from it, the proposal in the address and then in the form.
// Expected result: Step 1: the first is saved and goes back to the review; the second is 409, "Not saved: this proposal
//   was decided first, in another tab or window. Nothing was added.", which takes the focus, not a field; the patient
//   has Penicillin G alone, which decides the proposal. Step 2: back to the review; to the chart, and 404 with nothing
//   added; 400; 400. Step 3: to that patient's chart, then 404 twice; no allergy is added to either, the proposal still
//   waits, and no read of it or its review is recorded against the other patient.
// Creator: Tahseen Arnan
public class TC_UC4_34_AllergyFromDictationRefused(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_proposed_allergy_is_added_once_and_the_form_opens_only_for_a_waiting_allergy_of_its_own()
    {
        var dictated = await DictateAsync(apps);
        var path = AllergyFormPath(dictated.Patient, dictated.Allergy);
        var first = Token(await dictated.Session.Client.GetStringAsync(path, Cancel));
        var second = Token(await dictated.Session.Client.GetStringAsync(path, Cancel));
        using (var saved = await dictated.Session.Client.PostAsync(path, Form([("__RequestVerificationToken", first), ("Name", "Penicillin G"),
            ("Reaction", "Hives"), ("Status", "active")]), Cancel))
        {
            Assert.Equal(dictated.ReviewPath, PathOf(saved));
        }

        using (var refused = await dictated.Session.Client.PostAsync(path, Form([("__RequestVerificationToken", second), ("Name", "Amoxicillin"),
            ("Status", "active")]), Cancel))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            var page = await refused.Content.ReadAsStringAsync(Cancel);
            Assert.Contains("Not saved: this proposal was decided first, in another tab or window. Nothing was added.", Said(page));
            // The notice answers the save, so it takes the focus (NFR-12, as the form's other "Not saved:" notices do).
            Assert.Contains("role=\"alert\" tabindex=\"-1\" data-focus-on-load><span class=\"sc-notice__state\">Not saved:</span> this proposal was decided first", page);
            Assert.DoesNotContain("autofocus", page);
        }

        Assert.Equal(["7980"], await QueryAsync(apps, "SELECT code FROM ehr.allergy_intolerances WHERE patient_id = @id", dictated.Patient));
        Assert.StartsWith("recorded|-|", await StateAsync(apps, dictated.Allergy), StringComparison.Ordinal);

        using (var decided = await dictated.Session.Client.GetAsync(path, Cancel))
        {
            Assert.Equal(dictated.ReviewPath, PathOf(decided));
        }

        using (var order = await dictated.Session.Client.GetAsync(AllergyFormPath(dictated.Patient, dictated.Order), Cancel))
        {
            Assert.Equal($"/patients/{dictated.Patient}", PathOf(order));
        }

        using (var order = await dictated.Session.Client.PostAsync(AllergyFormPath(dictated.Patient, dictated.Order),
            Form([("__RequestVerificationToken", second), ("Name", "Amoxicillin"), ("Status", "active")]), Cancel))
        {
            Assert.Equal(HttpStatusCode.NotFound, order.StatusCode);
        }

        Assert.Equal(["7980"], await QueryAsync(apps, "SELECT code FROM ehr.allergy_intolerances WHERE patient_id = @id", dictated.Patient));

        foreach (var other in new[] { $"/patients/{dictated.Patient}/allergies/{Guid.NewGuid()}?proposal={dictated.Allergy}", $"/patients/{dictated.Patient}/medications?proposal={dictated.Allergy}" })
        {
            using var refused = await dictated.Session.Client.GetAsync(other, Cancel);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }

        // Named on the allergy form of another patient on the clinician's panel: not that patient's proposal.
        var again = await DictateAsync(apps, dictated.Session, dictated.Patient);
        var elsewhere = await apps.InsertPatientAsync("Synthetic", "Elsewhere");
        await apps.AssignAsync(await apps.ClinicianAsync(), elsewhere);
        using (var form = await dictated.Session.Client.GetAsync(AllergyFormPath(elsewhere, again.Allergy), Cancel))
        {
            Assert.Equal($"/patients/{elsewhere}", PathOf(form));
        }

        foreach (var (named, fields) in new[]
        {
            (AllergyFormPath(elsewhere, again.Allergy), Array.Empty<(string, object)>()),
            ($"/patients/{elsewhere}/allergies", [("Proposal", again.Allergy)]),
        })
        {
            using var refused = await dictated.Session.Client.PostAsync(named, Form([("__RequestVerificationToken", second), ("Name", "Amoxicillin"),
                ("Status", "active"), .. fields]), Cancel);
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        }

        Assert.Equal("0 orders, 0 allergies", await ChartAsync(apps, elsewhere));
        Assert.Equal(["7980"], await QueryAsync(apps, "SELECT code FROM ehr.allergy_intolerances WHERE patient_id = @id", dictated.Patient));
        Assert.Equal("pending|-|-", await StateAsync(apps, again.Allergy));
        Assert.Equal(["0"], await QueryAsync(apps,
            "SELECT count(*) FROM ehr.audit_events WHERE patient_id = @id AND table_name IN ('ai_chart_proposals', 'ai_note_reviews')", elsewhere));
    }
}
