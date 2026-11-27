using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.DictationProposalTests;
using static Ehr.Web.Tests.RefillRequestTests;

namespace Ehr.Web.Tests.TestCases.UC4;

// Test Case ID: TC-UC4-33
// Purpose / scenario: The allergy form opened from an allergy a dictation proposed to add (step 15): it shows the
//   clinician's words, labelled as heard and marked in the sentence they said them in, as proposed from dictation, and
//   fills in nothing from them; the substance is picked from the codes as for any allergy, and the allergy added is active. Saving it adds a
//   coded allergy, as the form adds any, "No known allergies" made inactive with it, and decides the proposal with it,
//   then goes back to the review.
// Type: Functional (successful flow)
// Requirements: FR-14 (reworded), proposed FR-38
// Use case: UC 4, View patient chart
// Preconditions: Dr. Test has the patient on their panel, signed in on the on-site app; the patient's list says "No
//   known allergies". Dr. Test dictated a note whose review proposes "Add penicillin to her allergy list".
// Inputs and system conditions: GET and POST /patients/{id}/allergies?proposal={proposal}, on the on-site app.
// Test data: Penicillin G, reaction Hives.
// Steps:
//   1. Open the allergy form from the proposal.
//   2. Save Penicillin G with the reaction Hives.
// Expected result: Step 1: "Proposed from dictation. Check every field. Nothing here was filled in from it." with the
//   words after "As heard:", marked; the substance is empty; the only status offered is Active. Step 2: back on the review; the patient has
//   Penicillin G (RxNorm 7980), active, with Hives, and "No known allergies" inactive; the proposal is decided with it.
// Creator: Tahseen Arnan
public class TC_UC4_33_AllergyFromDictation(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_allergy_form_opened_from_a_proposal_fills_in_nothing_and_adds_a_coded_allergy()
    {
        var dictated = await DictateAsync(apps);
        await QueryAsync(apps,
            "INSERT INTO ehr.allergy_intolerances (id, patient_id, code, display, clinical_status, recorded_at) VALUES (gen_random_uuid(), @id, 'no-known-allergy', 'No known allergy', 'active', now()) RETURNING id",
            dictated.Patient);

        var path = AllergyFormPath(dictated.Patient, dictated.Allergy);
        var html = await dictated.Session.Client.GetStringAsync(path, Cancel);
        var said = Said(html);
        Assert.Contains("Proposed from dictation. Check every field. Nothing here was filled in from it. It adds one allergy", said);
        Assert.Contains($"<mark class=\"sc-transcript__source\">{DictationSidecarStub.AllergySaid}</mark>", html);
        Assert.Contains($"As heard: {DictationSidecarStub.OrderSaid}. {DictationSidecarStub.AllergySaid}.", said);
        Assert.DoesNotMatch("name=\"Name\" value=\"[^\"]", html);
        Assert.Contains("<option value=\"active\"", html);
        Assert.DoesNotContain("<option value=\"inactive\"", html);
        Assert.DoesNotContain("<option value=\"resolved\"", html);
        Assert.Contains($"<a href=\"{dictated.ReviewPath}\">Cancel</a>", html);

        using (var saved = await PostFormAsync(dictated.Session.Client, path, path, ("Name", "Penicillin G"), ("Reaction", "Hives"), ("Status", "active")))
        {
            Assert.Equal(dictated.ReviewPath, PathOf(saved));
        }

        var allergy = (await QueryAsync(apps, "SELECT id FROM ehr.allergy_intolerances WHERE patient_id = @id AND code = '7980'", dictated.Patient)).Single();
        Assert.Equal(["no-known-allergy inactive -", "7980 active Hives"], await QueryAsync(apps,
            "SELECT code || ' ' || clinical_status || ' ' || coalesce(reaction, '-') FROM ehr.allergy_intolerances WHERE patient_id = @id ORDER BY recorded_at, code DESC",
            dictated.Patient));
        Assert.Equal($"recorded|-|{allergy}", await StateAsync(apps, dictated.Allergy));
    }
}
