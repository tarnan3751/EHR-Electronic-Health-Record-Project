using System.Net;
using Ehr.Web.Api.Dictation;
using Xunit;
using static Ehr.Web.Tests.AmbientFlows;

namespace Ehr.Web.Tests.TestCases.UC23;

// Test Case ID: TC-UC23-01
// Purpose / scenario: A clinician confirms that the patient and they have agreed aloud, listens to the visit, names each
//   voice, and reviews the note drafted from it: the consent is recorded before anything else, the review shows who said
//   each passage, and finishing it puts the approved values in the note and deletes the conversation's transcript.
// Type: Functional (successful flow)
// Requirements: proposed FR-62
// Use case: UC 23, Draft a note from a recorded visit
// Preconditions: The clinician has the patient on their panel; the on-site app, with the stub dictation service.
// Inputs and system conditions: POST /api/dictation/sessions/ambient/start, /{session}/audio, /stop, /speakers and
//   /result; POST /api/dictation/sessions/review/{review}/stage; GET and POST the review page; GET the note editor.
// Test data: Two speakers, agreed by the patient; the stub's conversation, voice 1 the clinician and voice 2 the patient.
// Steps:
//   1. Start listening with the consent, send a chunk, stop, name both voices, get the draft and stage it.
//   2. Open the review.
//   3. Edit Subjective into the clinician's words, accept the other three, and finish the review.
// Expected result: Step 1: one consent row (2 speakers, the patient) with its write audit under the clinician; the review
//   bound to it; the service asked for a visit's session, told so on every call, and the session discarded at the service
//   once staged; the transcript kept apart. Step 2: the page titled "Review the note drafted from the visit", and the
//   transcript a turn at a time, "Clinician" and "Patient"; the notice and the warning that Subjective is the patient's
//   own words; "Said by: Patient." on Subjective; one recorded read of the transcript. Step 3: the note editor has the edited Subjective and the three accepted values; the review is
//   completed and no transcript is kept.
// Creator: Tahseen Arnan
public class TC_UC23_01_SuccessfulFlow(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_visit_listened_to_with_consent_is_drafted_reviewed_and_its_transcript_deleted_when_finished()
    {
        var sidecar = apps.Dictation.Reset();
        var d = await DraftAsync(apps);
        var l = d.Listening;
        var clinician = await apps.ClinicianAsync();

        Assert.Equal(["2 patient"], await RowsAsync(apps, "SELECT speakers || ' ' || given_by FROM ehr.ambient_consents WHERE patient_id = @id", l.Patient));
        Assert.Equal(["1"], await RowsAsync(apps,
            $"SELECT count(*) FROM ehr.audit_events WHERE table_name = 'ambient_consents' AND action = 'insert' AND patient_id = @id AND actor_id = '{clinician.Id}'", l.Patient));
        Assert.Equal(["ambient true"], await RowsAsync(apps,
            "SELECT r.mode || ' ' || (r.consent_id = c.id)::text FROM ehr.ai_note_reviews r JOIN ehr.ambient_consents c ON c.patient_id = r.patient_id WHERE r.id = @id", l.Review));
        Assert.Equal("/v1/sessions?mode=ambient&speakers=2", sidecar.Received.First().PathAndQuery);
        Assert.All(sidecar.Received, request => Assert.Equal("ambient", request.Headers[DictationSidecar.ModeHeader]));
        Assert.Equal("DELETE", sidecar.Received.Last().Method);
        Assert.Equal(["1"], await RowsAsync(apps, "SELECT count(*) FROM ehr.ambient_transcripts WHERE id = @id", l.Review));
        Assert.Equal([""], await RowsAsync(apps, "SELECT result_json #>> '{transcript,text}' FROM ehr.ai_note_reviews WHERE id = @id", l.Review));

        var html = await l.Session.Client.GetStringAsync(d.ReviewUrl, Cancel);
        Assert.Contains("<title>Review the note drafted from the visit</title>", html);
        Assert.Contains("What brings you in today?", html);
        Assert.Contains("data-ambient-own-words", html);
        Assert.Contains("patients_own_words", html);
        Assert.Contains("\"speaker\":\"patient\"", html);
        Assert.Equal(["note-draft"], await RowsAsync(apps,
            "SELECT DISTINCT part FROM ehr.audit_events WHERE table_name = 'ambient_transcripts' AND action = 'read' AND row_id = @id", l.Review));

        var proposals = await ProposalsAsync(apps, l.Review);
        var version = d.Version;
        foreach (var (field, decision) in new[] { ("subjective", "edited"), ("objective", "accepted"), ("assessment", "accepted"), ("plan", "accepted") })
        {
            using var decided = await Decide(l.Session.Client, l.Token, d.ReviewUrl, proposals[field].Id, version, decision,
                field == "subjective" ? "Synthetic: cough for three days." : "");
            Assert.Equal(HttpStatusCode.OK, decided.StatusCode);
            version++;
        }

        using var finished = await ReviewPost(l.Session.Client, l.Token, d.ReviewUrl, "Complete", ("Version", version));
        Assert.Equal(HttpStatusCode.OK, finished.StatusCode);
        var editor = await l.Session.Client.GetStringAsync($"/patients/{l.Patient}/notes/drafts/{d.Draft}", Cancel);
        Assert.Contains("Synthetic: cough for three days.", editor);
        Assert.Contains("Your chest is clear.", editor);
        Assert.Equal(["completed 0"], await RowsAsync(apps,
            "SELECT r.state || ' ' || (SELECT count(*) FROM ehr.ambient_transcripts t WHERE t.id = r.id) FROM ehr.ai_note_reviews r WHERE r.id = @id", l.Review));
        var after = await l.Session.Client.GetStringAsync(d.ReviewUrl, Cancel);
        Assert.Contains("transcript was deleted when its review was finished.", after);
        Assert.DoesNotContain("What brings you in today?", after);
    }
}
