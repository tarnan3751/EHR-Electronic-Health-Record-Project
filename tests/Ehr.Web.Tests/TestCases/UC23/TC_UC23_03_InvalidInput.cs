using System.Net;
using Ehr.Web.Api.Dictation;
using Xunit;
using static Ehr.Web.Tests.AmbientFlows;

namespace Ehr.Web.Tests.TestCases.UC23;

// Test Case ID: TC-UC23-03
// Purpose / scenario: Nothing is listened to without the clinician's confirmation, a visit's audio goes only through its
//   own routes, and the note isn't drafted until every voice has a role.
// Type: Functional (invalid input)
// Requirements: proposed FR-62
// Use case: UC 23, Draft a note from a recorded visit
// Preconditions: The clinician has the patient on their panel; the on-site app, with the stub dictation service.
// Inputs and system conditions: POST /api/dictation/sessions/ambient/start, /{session}/audio and /speakers; POST
//   /api/dictation/sessions/{session}/audio, the dictation's route.
// Test data: Consents without the agreement, with 4 speakers, agreed by a "neighbour", or in Spanish; a visit of three
//   voices named with one missing, two clinicians, an unknown role, or a voice left out.
// Steps:
//   1. Start listening with each consent that isn't one.
//   2. Start listening properly, and send a chunk of its audio through the dictation's route.
//   3. Stop, and name the voices each way that isn't complete.
// Expected result: Step 1: each refused with 422; no consent row; nothing asked of the service. Step 2: 404, as for no
//   such session. Step 3: each refused with 422 speakers_incomplete, the last one by the service. (The database's own
//   refusals: tests/Ehr.Security.Tests/AmbientSecurityTests.cs.)
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
            new Dictionary<string, string> { ["1"] = "clinician", ["2"] = "patient" },
        })
        {
            using var refused = await Post(l.Session.Client, l.Token, $"{ListenApi}/{l.SessionId}/speakers", new { speakers });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Equal("speakers_incomplete", (await Json(refused)).GetProperty("error").GetString());
        }

        Assert.Single(sidecar.Received, request => request.PathAndQuery.EndsWith("/speakers", StringComparison.Ordinal));
    }
}
