using System.Net;
using Xunit;
using static Ehr.Web.Tests.AmbientFlows;

namespace Ehr.Web.Tests.TestCases.UC23;

// Test Case ID: TC-UC23-02
// Purpose / scenario: The patient changes their mind. During the visit, the clinician stops and discards: the listening
//   takes nothing more and the service discards it. After the draft, the clinician rejects every proposal and deletes the
//   conversation: nothing goes into the note, and the transcript is gone. And a voice the clinician can't tell (a
//   fragment of someone's, told apart as one more voice) is named so, and its words are shown as "Unsure".
// Type: Functional (alternative flow)
// Requirements: proposed FR-62
// Use case: UC 23, Draft a note from a recorded visit
// Preconditions: The clinician has the patient on their panel; the on-site app, with the stub dictation service.
// Inputs and system conditions: POST /api/dictation/sessions/review/{review}/discard and DELETE
//   /api/dictation/sessions/ambient/{session} while listening; POST the review page's RejectAll after the draft.
// Test data: Two speakers, agreed by the patient; the stub's conversation.
// Steps:
//   1. Start listening and send a chunk; discard the review and the session; try to send another chunk, stop, and ask for
//      the result.
//   2. Draft another visit to its review, and reject every proposal and delete the conversation.
//   3. Listen to a visit of two people in which three voices are told apart, name the third "unsure", and draft it.
// Expected result: Step 1: the review cancelled; each later call refused with 409 not_listening and nothing more sent to
//   the service; the service told to discard the session, as a visit's. Step 2: every proposal rejected, the review
//   completed, no transcript kept, and the draft's four sections still empty. Step 3: the mapping sent to the service as
//   named; the review's transcript gives the third voice's words as "unsure". (The browser does the same from the
//   keyboard, switching the microphone off first, and names a voice "I can't tell": AmbientBrowserTests.)
// Creator: Tahseen Arnan
public class TC_UC23_02_AlternativeFlow(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Discarded_while_listening_nothing_more_is_taken_and_the_service_discards_it()
    {
        var sidecar = apps.Dictation.Reset();
        var l = await StartAsync(apps);
        using (var chunk = await Audio(l, 0))
        {
            Assert.Equal(HttpStatusCode.OK, chunk.StatusCode);
        }

        using (var discarded = await Post(l.Session.Client, l.Token, $"{Ehr.Web.Api.Dictation.DictationApi.Path}/review/{l.Review}/discard"))
        {
            Assert.Equal(HttpStatusCode.OK, discarded.StatusCode);
        }

        var sent = sidecar.Received.Count;
        using (var refused = await Audio(l, 1))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("not_listening", (await Json(refused)).GetProperty("error").GetString());
        }

        foreach (var route in new[] { "stop", "speakers", "result" })
        {
            object? body = route == "speakers" ? new { speakers = new Dictionary<string, string> { ["1"] = "clinician", ["2"] = "patient" } } : null;
            using var refused = await Post(l.Session.Client, l.Token, $"{ListenApi}/{l.SessionId}/{route}", body);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        }

        Assert.Equal(sent, sidecar.Received.Count);
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{ListenApi}/{l.SessionId}");
        request.Headers.Add("RequestVerificationToken", l.Token);
        using var deleted = await l.Session.Client.SendAsync(request, Cancel);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(("DELETE", "ambient"), (sidecar.Received.Last().Method, sidecar.Received.Last().Headers[Ehr.Web.Api.Dictation.DictationSidecar.ModeHeader]));
        Assert.Equal(["cancelled"], await RowsAsync(apps, "SELECT state FROM ehr.ai_note_reviews WHERE id = @id", l.Review));
    }

    [Fact]
    public async Task A_voice_the_clinician_cant_tell_is_named_unsure_and_its_words_shown_so()
    {
        var sidecar = apps.Dictation.Reset();
        sidecar.ExtraVoice = true;
        var l = await StartAsync(apps, speakers: 2);
        using (var chunk = await Audio(l, 0))
        using (var stop = await Post(l.Session.Client, l.Token, $"{ListenApi}/{l.SessionId}/stop"))
        {
            Assert.Equal(3, (await Json(stop)).GetProperty("speakers").GetArrayLength());
        }

        var named = new Dictionary<string, string> { ["1"] = "clinician", ["2"] = "patient", ["3"] = "unsure" };
        using (var speakers = await Post(l.Session.Client, l.Token, $"{ListenApi}/{l.SessionId}/speakers", new { speakers = named }))
        {
            Assert.Equal(HttpStatusCode.OK, speakers.StatusCode);
        }

        Assert.Equal("""{"speakers":{"1":"clinician","2":"patient","3":"unsure"}}""",
            System.Text.Encoding.UTF8.GetString(sidecar.Received.Single(r => r.PathAndQuery.EndsWith("/speakers", StringComparison.Ordinal)).Body));
        using var staged = await Post(l.Session.Client, l.Token, $"{Ehr.Web.Api.Dictation.DictationApi.Path}/review/{l.Review}/stage", new { version = l.Version });
        Assert.Equal(HttpStatusCode.OK, staged.StatusCode);
        var html = await l.Session.Client.GetStringAsync((await Json(staged)).GetProperty("url").GetString(), Cancel);
        Assert.Contains("She has been coughing at night.", html);
        Assert.Contains("\"speakerGroup\":3,\"speaker\":\"unsure\"", html);
    }

    [Fact]
    public async Task After_the_draft_every_proposal_is_rejected_and_the_conversation_deleted_with_nothing_in_the_note()
    {
        apps.Dictation.Reset();
        var d = await DraftAsync(apps);
        var l = d.Listening;
        Assert.Contains("Reject every proposal and delete the conversation", await l.Session.Client.GetStringAsync(d.ReviewUrl, Cancel));

        using var rejected = await ReviewPost(l.Session.Client, l.Token, d.ReviewUrl, "RejectAll", ("Version", d.Version));
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.Equal(["rejected"], await RowsAsync(apps, "SELECT DISTINCT state FROM ehr.ai_field_proposals WHERE review_id = @id", l.Review));
        Assert.Equal(["completed 0"], await RowsAsync(apps,
            "SELECT r.state || ' ' || (SELECT count(*) FROM ehr.ambient_transcripts t WHERE t.id = r.id) FROM ehr.ai_note_reviews r WHERE r.id = @id", l.Review));
        Assert.Equal(["0"], await RowsAsync(apps,
            "SELECT count(*) FROM ehr.clinical_drafts WHERE id = @id AND (subjective <> '' OR objective <> '' OR assessment <> '' OR plan <> '')", d.Draft));
    }
}
