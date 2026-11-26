using System.Net;
using Ehr.Web.Api.Dictation;
using Xunit;
using static Ehr.Web.Tests.AmbientFlows;

namespace Ehr.Web.Tests.TestCases.UC23;

// Test Case ID: TC-UC23-03
// Purpose / scenario: Nothing is listened to without the clinician's confirmation, a visit's audio and its review go only
//   through their own routes, the note isn't drafted until every voice has a role and no more voices are named than the
//   consent said would speak, and a draft that doesn't say who said each passage, or that Subjective is the patient's
//   own words, isn't staged.
// Type: Functional (invalid input)
// Requirements: proposed FR-62
// Use case: UC 23, Draft a note from a recorded visit
// Preconditions: The clinician has the patient on their panel; the on-site app, with the stub dictation service.
// Inputs and system conditions: POST /api/dictation/sessions/ambient/start, /{session}/audio and /speakers; POST
//   /api/dictation/sessions/{session}/audio, the dictation's route, and /for-note, a dictation's start; the review's
//   staging, with the service's answer replaced.
// Test data: Consents without the agreement, with 4 speakers, agreed by a "neighbour", or in Spanish; a visit of three
//   voices named with one missing, two clinicians, an unknown role, two others, a voice numbered past a gap, or a voice
//   left out; a visit of two people, by its consent, and three voices, the third named as a person; drafts whose
//   passage has no speaker, as the fields couldn't be sorted, whose Subjective lacks the patient's-own-words warning,
//   that list the patient's request as an instruction not carried over, whose empty field gives a reason in a code's
//   shape that isn't the service's, or whose provenance holds words.
// Steps:
//   1. Start listening with each consent that isn't one.
//   2. Start listening properly, and send a chunk of its audio through the dictation's route.
//   3. Stop, and name the voices each way that isn't complete.
//   4. Start a dictation for a visit's review.
//   5. With a consent for the patient and the clinician, and three voices told apart, stop and name the third someone
//      else, the clinician or the patient; then "unsure".
//   6. Stage each draft that doesn't say who said what.
// Expected result: Step 1: each refused with 422; no consent row; nothing asked of the service. Step 2: 404, as for no
//   such session. Step 3: each refused with 422 speakers_incomplete, the last one by the service. Step 4: 404, as for a
//   review that isn't there, and nothing asked of the service. Step 5: 422 speakers_incomplete for each, and nothing sent
//   to the service; "unsure" is taken. Step 6: each refused with 502 invalid_result; the review not staged, and no transcript kept. (The
//   database's own refusals: tests/Ehr.Security.Tests/AmbientSecurityTests.cs.)
// Creator: Tahseen Arnan
public class TC_UC23_03_InvalidInput(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task Without_the_confirmation_nothing_starts_a_visits_audio_takes_its_own_routes_and_every_voice_needs_a_role()
    {
        var sidecar = apps.Dictation.Reset();
        var (session, patient, token) = await OpenAsync(apps);
        foreach (var consent in new[]
        {
            Consent(Guid.NewGuid(), patient, agreed: false),
            Consent(Guid.NewGuid(), patient, speakers: 4),
            Consent(Guid.NewGuid(), patient, givenBy: "neighbour"),
            Consent(Guid.NewGuid(), patient, language: "es"),
        })
        {
            using var response = await Post(session.Client, token, $"{ListenApi}/start", consent);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        }

        Assert.Empty(sidecar.Received);
        Assert.Equal(["0"], await RowsAsync(apps, "SELECT count(*) FROM ehr.ambient_consents WHERE patient_id = @id", patient));

        var l = await StartAsync(apps, speakers: 3);
        using (var wrongRoute = await Audio(l, 0, DictationApi.Path))
        {
            Assert.Equal(HttpStatusCode.NotFound, wrongRoute.StatusCode);
        }

        using (var chunk = await Audio(l, 0))
        {
            Assert.Equal(HttpStatusCode.OK, chunk.StatusCode);
        }

        using (var stop = await Post(l.Session.Client, l.Token, $"{ListenApi}/{l.SessionId}/stop"))
        {
            Assert.Equal("awaiting_speakers", (await Json(stop)).GetProperty("sorting").GetString());
        }

        foreach (var speakers in new object[]
        {
            new { },
            new Dictionary<string, string> { ["1"] = "clinician" },
            new Dictionary<string, string> { ["1"] = "clinician", ["2"] = "clinician", ["3"] = "patient" },
            new Dictionary<string, string> { ["1"] = "clinician", ["2"] = "patient", ["3"] = "doctor" },
            new Dictionary<string, string> { ["1"] = "clinician", ["2"] = "patient", ["3"] = "other", ["4"] = "other" },
            new Dictionary<string, string> { ["1"] = "clinician", ["3"] = "patient", ["4"] = "unsure" },
            new Dictionary<string, string> { ["1"] = "clinician", ["2"] = "patient" },
        })
        {
            using var refused = await Post(l.Session.Client, l.Token, $"{ListenApi}/{l.SessionId}/speakers", new { speakers });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Equal("speakers_incomplete", (await Json(refused)).GetProperty("error").GetString());
        }

        Assert.Single(sidecar.Received, request => request.PathAndQuery.EndsWith("/speakers", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_visits_review_is_never_bound_to_a_dictation()
    {
        var sidecar = apps.Dictation.Reset();
        var l = await StartAsync(apps);
        var asked = sidecar.Received.Count;
        using var dictation = await Post(l.Session.Client, l.Token, $"{DictationApi.Path}/for-note", new { reviewId = l.Review, patientId = l.Patient });
        Assert.Equal(HttpStatusCode.NotFound, dictation.StatusCode);
        Assert.DoesNotContain(l.SessionId, await dictation.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(asked, sidecar.Received.Count);
    }

    // Under a consent for the patient and the clinician, a third voice told apart (a fragment of someone's) can only be
    // "unsure": named as anyone, it is refused before the service hears of it.
    [Fact]
    public async Task No_more_people_are_named_than_the_consent_said_would_speak()
    {
        var sidecar = apps.Dictation.Reset();
        sidecar.ExtraVoice = true;
        var l = await StartAsync(apps, speakers: 2);
        using (var stop = await Post(l.Session.Client, l.Token, $"{ListenApi}/{l.SessionId}/stop"))
        {
            Assert.Equal(3, (await Json(stop)).GetProperty("speakers").GetArrayLength());
        }

        foreach (var third in new[] { "other", "clinician", "patient" })
        {
            var named = new Dictionary<string, string> { ["1"] = "clinician", ["2"] = "patient", ["3"] = third };
            using var refused = await Post(l.Session.Client, l.Token, $"{ListenApi}/{l.SessionId}/speakers", new { speakers = named });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Equal("speakers_incomplete", (await Json(refused)).GetProperty("error").GetString());
        }

        Assert.DoesNotContain(sidecar.Received, request => request.PathAndQuery.EndsWith("/speakers", StringComparison.Ordinal));
        var unsure = new Dictionary<string, string> { ["1"] = "clinician", ["2"] = "patient", ["3"] = "unsure" };
        using var accepted = await Post(l.Session.Client, l.Token, $"{ListenApi}/{l.SessionId}/speakers", new { speakers = unsure });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    // A visit's draft as the service sends one, but for what it gets wrong: a passage with no one's name, and the fields
    // not sorted; a Subjective value from the patient that doesn't say it is their own words; or an instruction listed as
    // the patient's, where only the clinician's, or one said where who spoke couldn't be told, is listed.
    const string Provenance = """
        "provenance":{"speech":{"repository":"google/medasr","commit":"1111111","sha256":"aaaa"},"language":null,"llamaServer":null,
        "prompt":"conversation-prompt-1","schema":"note-schema-1","structuring":"conversation-structuring-1","code":"sha256:cccc"}
        """;

    [Theory]
    [InlineData("""
        {"transcript":{"text":"What brings you in today?","final":true,"segments":[{"start":0,"end":25,"startMs":0,"endMs":3000,"final":true,"speakerGroup":1}]},
         "sorting":"unavailable","reason":"model_timeout","fields":null,"notCarriedOver":[],
        """)]
    [InlineData("""
        {"transcript":{"text":"I have had a cough for three days.","final":true,"segments":[{"start":0,"end":34,"startMs":0,"endMs":3000,"final":true,"speakerGroup":2,"speaker":"patient"}]},
         "sorting":"done","notCarriedOver":[],
         "fields":{"subjective":{"status":"pending","value":"I have had a cough for three days.","sources":[{"start":0,"end":34,"speaker":"patient"}],"warnings":[]},
                   "objective":{"status":"empty","reasons":["not_dictated"]},"assessment":{"status":"empty","reasons":["not_dictated"]},"plan":{"status":"empty","reasons":["not_dictated"]}},
        """)]
    [InlineData("""
        {"transcript":{"text":"I have had a cough for three days.","final":true,"segments":[{"start":0,"end":34,"startMs":0,"endMs":3000,"final":true,"speakerGroup":2,"speaker":"patient"}]},
         "sorting":"done","notCarriedOver":[{"reason":"order","start":0,"end":34,"speaker":"patient"}],
         "fields":{"subjective":{"status":"pending","value":"I have had a cough for three days.","sources":[{"start":0,"end":34,"speaker":"patient"}],"warnings":[{"code":"patients_own_words","start":0,"end":34}]},
                   "objective":{"status":"empty","reasons":["not_dictated"]},"assessment":{"status":"empty","reasons":["not_dictated"]},"plan":{"status":"empty","reasons":["not_dictated"]}},
        """)]
    // Or one whose empty field gives a reason the service doesn't (words in a code's shape), or whose provenance holds
    // words: the database would refuse either as it is stored.
    [InlineData("""
        {"transcript":{"text":"I have had a cough for three days.","final":true,"segments":[{"start":0,"end":34,"startMs":0,"endMs":3000,"final":true,"speakerGroup":2,"speaker":"patient"}]},
         "sorting":"done","notCarriedOver":[],
         "fields":{"subjective":{"status":"pending","value":"I have had a cough for three days.","sources":[{"start":0,"end":34,"speaker":"patient"}],"warnings":[{"code":"patients_own_words","start":0,"end":34}]},
                   "objective":{"status":"empty","reasons":["the_patient_said_cough_for_three_days"]},"assessment":{"status":"empty","reasons":["not_dictated"]},"plan":{"status":"empty","reasons":["not_dictated"]}},
        """)]
    [InlineData("""
        {"transcript":{"text":"I have had a cough for three days.","final":true,"segments":[{"start":0,"end":34,"startMs":0,"endMs":3000,"final":true,"speakerGroup":2,"speaker":"patient"}]},
         "sorting":"done","notCarriedOver":[],
         "fields":{"subjective":{"status":"pending","value":"I have had a cough for three days.","sources":[{"start":0,"end":34,"speaker":"patient"}],"warnings":[{"code":"patients_own_words","start":0,"end":34}]},
                   "objective":{"status":"empty","reasons":["not_dictated"]},"assessment":{"status":"empty","reasons":["not_dictated"]},"plan":{"status":"empty","reasons":["not_dictated"]}},
         "provenance":{"speech":{"repository":"google/medasr","commit":"1111111","sha256":"aaaa"},"language":null,"llamaServer":null,
         "prompt":"I have had a cough for three days","schema":"note-schema-1","structuring":"conversation-structuring-1","code":"sha256:cccc"}}
        """, false)]
    public async Task A_draft_that_doesnt_say_who_said_what_isnt_staged(string draft, bool withProvenance = true)
    {
        var sidecar = apps.Dictation.Reset();
        var l = await StartAsync(apps);
        using (var stop = await Post(l.Session.Client, l.Token, $"{ListenApi}/{l.SessionId}/stop"))
        using (var named = await Post(l.Session.Client, l.Token, $"{ListenApi}/{l.SessionId}/speakers",
            new { speakers = new Dictionary<string, string> { ["1"] = "clinician", ["2"] = "patient" } }))
        {
            Assert.Equal(HttpStatusCode.OK, named.StatusCode);
        }

        sidecar.Answer = (HttpStatusCode.OK, withProvenance ? draft + Provenance + "}" : draft);
        using var staged = await Post(l.Session.Client, l.Token, $"{DictationApi.Path}/review/{l.Review}/stage", new { version = l.Version });
        Assert.Equal(HttpStatusCode.BadGateway, staged.StatusCode);
        Assert.Equal("invalid_result", (await Json(staged)).GetProperty("error").GetString());
        Assert.Equal(["recording 0"], await RowsAsync(apps,
            "SELECT r.state || ' ' || (SELECT count(*) FROM ehr.ambient_transcripts t WHERE t.id = r.id) FROM ehr.ai_note_reviews r WHERE r.id = @id", l.Review));
    }
}
