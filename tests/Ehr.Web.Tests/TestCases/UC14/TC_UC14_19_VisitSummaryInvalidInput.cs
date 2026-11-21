using System.Net;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.NoteAddendumTests;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests.TestCases.UC14;

// Test Case ID: TC-UC14-19
// Purpose / scenario: A visit summary with an internal code or identifier in it, the patient's own record number, a
//   whole section of the note copied in, an empty part or a part over 4,000 characters is not released: the page says
//   what was found, where, and what to do instead, and keeps what was typed. Ordinary words, doses, dates and numbers
//   are released as typed.
// Type: Functional (invalid input)
// Requirements: proposed FR-55
// Use case: UC 14, Review and sign a clinical note
// Preconditions: The clinician is signed in on the on-site app with the patient on their panel and has signed a note
//   whose history is "Synthetic history: dry cough, quartzwing pattern, nocturnal.". The patient has a portal account.
// Inputs and system conditions: POST /patients/{id}/notes/{note}/summary, once for each input below.
// Test data: In What we found, each of: "E11.9", "E119" (a diagnosis code with or without its dot), "860975" (an
//   RxNorm code), "4548-4" (a LOINC code), "QE1QX6B99R" (a UNII), "SYN-LAB-001" (the clinic's own order code), a UUID,
//   the patient's MRN, and the note's history pasted whole; then an empty What happens next, and a What we talked about
//   of 4,001 characters. Last, all three parts as "Vitamin B12 and A1c are fine. Take 500 mg twice a day, vitamin D
//   50,000 units weekly, 1-2 tablets as needed; back on 2026-11-21. Wear an N95 mask.".
// Steps:
//   1. Post each refused input in turn.
//   2. Post the ordinary words.
// Expected result: Step 1: each is answered 422 with the form again, "Not released:" and the sentence for what was
//   found (for example "“E11.9” in What we found is a diagnosis code. Say what it means in plain words."), and what was
//   typed is still in the field; nothing is saved and no email is sent. Step 2: the summary is released as typed.
// Creator: Tahseen Arnan
public class TC_UC14_19_VisitSummaryInvalidInput(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_summary_with_a_code_an_identifier_or_the_notes_own_words_is_not_released()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        var (email, _) = await apps.CreatePatientAccountAsync(patient);
        var note = await SignedNoteAsync(session.Client, patient, history: NoteHistory);
        var mrn = WebApps.MrnOf(patient);
        const string identifier = "0193a5c2-7e4b-7c1d-9f2e-3b4a5c6d7e8f";

        foreach (var (found, sentence) in new[]
        {
            ("We checked E11.9.", "“E11.9” in What we found is a diagnosis code. Say what it means in plain words."),
            ("We checked E119.", "“E119” in What we found is a diagnosis code. Say what it means in plain words."),
            ("Keep taking 860975.", "“860975” in What we found is a medication code. Name the medicine in plain words."),
            ("We will repeat 4548-4.", "“4548-4” in What we found is a lab test code. Name the test in plain words."),
            ("Avoid QE1QX6B99R.", "“QE1QX6B99R” in What we found is a substance code. Name it in plain words."),
            ("We ordered SYN-LAB-001.", "“SYN-LAB-001” in What we found is one of the clinic's order codes. Say what was ordered in plain words."),
            ($"See {identifier}.", $"“{identifier}” in What we found looks like an internal identifier. Leave it out."),
            ($"Your number is {mrn.ToUpperInvariant()}.", $"“{mrn.ToUpperInvariant()}” in What we found is the patient's medical record number. Leave it out."),
            (NoteHistory, "What we found repeats the note's History word for word. Write it for the patient in your own words."),
        })
        {
            using var refused = await ReleaseAsync(session.Client, patient, note, found: found);
            var page = await refused.Content.ReadAsStringAsync(Cancel);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Contains("Not released: " + sentence, Words(page));
            // What was typed is still there, to change.
            Assert.Contains(found, Words(page));
            Assert.Contains("data-release-summary", page);
        }

        using (var empty = await ReleaseAsync(session.Client, patient, note, nextSteps: "   "))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
            Assert.Contains("Not released: Write “What happens next”.", Words(await empty.Content.ReadAsStringAsync(Cancel)));
        }

        using (var tooLong = await ReleaseAsync(session.Client, patient, note, talkedAbout: new string('a', 4001)))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLong.StatusCode);
            Assert.Contains("Not released: “What we talked about” can be up to 4,000 characters. It has 4,001.", Words(await tooLong.Content.ReadAsStringAsync(Cancel)));
        }

        Assert.Empty(await SummariesAsync(apps, patient));
        Assert.Empty(EmailsTo(apps, email));

        // Ordinary words, doses, dates and numbers: released as typed.
        const string ordinary = "Vitamin B12 and A1c are fine. Take 500 mg twice a day, vitamin D 50,000 units weekly, 1-2 tablets as needed; back on 2026-11-21. Wear an N95 mask.";
        using var released = await ReleaseAsync(session.Client, patient, note, ordinary, ordinary, ordinary);
        Assert.Contains("Released:", Words(await ReadAsync(released)));
        Assert.Single(await SummariesAsync(apps, patient));
    }
}
