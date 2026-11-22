using System.Net;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.AmbientFlows;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests.TestCases.UC23;

// Test Case ID: TC-UC23-04
// Purpose / scenario: Listening to a visit when things aren't as they should be: off-site, where it isn't offered and
//   nothing about it crosses the WAN, and a review whose transcript is kept is decided only at the clinic; the dictation
//   service unreachable when listening starts; and a review left open until its transcript is deleted after 7 days.
// Type: Functional (exceptional condition)
// Requirements: proposed FR-62; UC 2
// Use case: UC 23, Draft a note from a recorded visit
// Preconditions: The clinician has the patient on their panel; the on-site and the cloud app, with the stub dictation
//   service.
// Inputs and system conditions: GET the chart, the Listen page and a review on each site; POST the start off-site; POST a
//   decision through the cloud app, which forwards it; the stub unreachable at start; ehr.purge_ambient_transcripts with
//   nothing kept, as the on-site app's timer runs it after 7 days.
// Test data: Synthetic patients and the stub's conversation.
// Steps:
//   1. Open the chart and the Listen page on each site; start listening through the cloud app.
//   2. Draft a visit at the clinic, open its review off-site, and decide a proposal through the cloud app.
//   3. With the service unreachable, start listening.
//   4. Draft a visit, purge its transcript, open its review, and accept, then edit, a proposal.
// Expected result: Step 1: "Listen to the visit" on the clinic's chart only; off-site, the page's "Unavailable:" notice
//   and no listening controls; the start refused by the cloud app, nothing forwarded, nothing sent to the service. Step 2:
//   off-site, "Read-only here:" with the review's proposals, no transcript and no decisions; the forwarded decision refused
//   with "Finish this review at the clinic…". Step 3: 503, no consent recorded. Step 4: the review says the transcript was
//   deleted after 7 days and offers no Accept; accepting is refused with that sentence; editing is saved.
// Creator: Tahseen Arnan
public class TC_UC23_04_ExceptionalCondition(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Off_site_listening_is_not_offered_and_nothing_about_it_crosses_the_WAN()
    {
        var sidecar = apps.Dictation.Reset();
        var clinician = await apps.ClinicianAsync();
        var patient = await apps.InsertPatientAsync("Synthetic", "Listening off-site");
        await apps.AssignAsync(clinician, patient);

        var onSite = await SignInAsync(apps.OnPrem, clinician);
        Assert.Contains("data-listen-link", await onSite.Client.GetStringAsync($"/patients/{patient}", Cancel));

        var offSite = await SignInAsync(apps.Cloud, clinician, via: apps.OnPrem);
        Assert.DoesNotContain("data-listen-link", await offSite.Client.GetStringAsync($"/patients/{patient}", Cancel));
        var page = await offSite.Client.GetStringAsync($"/patients/{patient}/listen", Cancel);
        Assert.Contains("data-ambient-offsite", page);
        Assert.DoesNotContain("data-ambient-api", page);

        apps.Forwarded.Clear();
        using var refused = await Post(offSite.Client, Token(page), $"{ListenApi}/start", Consent(Guid.NewGuid(), patient));
        // Answered by the cloud app as any API address it doesn't serve: refused, and nothing forwarded.
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.DoesNotContain(apps.Forwarded, forwarded => forwarded.Contains("/ambient", StringComparison.Ordinal));
        Assert.Empty(sidecar.Received);
    }

    [Fact]
    public async Task Off_site_a_review_whose_transcript_is_kept_is_read_without_it_and_decided_only_at_the_clinic()
    {
        apps.Dictation.Reset();
        var d = await DraftAsync(apps);
        var offSite = await SignInAsync(apps.Cloud, await apps.ClinicianAsync(), via: apps.OnPrem);
        var html = await offSite.Client.GetStringAsync(d.ReviewUrl, Cancel);
        Assert.Contains("Read-only here:", html);
        Assert.Contains("Your chest is clear.", html);
        Assert.DoesNotContain("What brings you in today?", html);
        Assert.DoesNotContain("data-review-controls", html);
        Assert.DoesNotContain("Reject every proposal", html);

        var proposal = (await ProposalsAsync(apps, d.Listening.Review))["objective"].Id;
        using var forwarded = await Decide(offSite.Client, Token(html), d.ReviewUrl, proposal, d.Version, "accepted");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, forwarded.StatusCode);
        Assert.Contains("Finish this review at the clinic", await forwarded.Content.ReadAsStringAsync(Cancel));
        Assert.Equal(["pending"], await RowsAsync(apps, "SELECT state FROM ehr.ai_field_proposals WHERE id = @id", proposal));
    }

    [Fact]
    public async Task With_the_dictation_service_unreachable_nothing_is_recorded()
    {
        var sidecar = apps.Dictation.Reset();
        var (session, patient, token) = await OpenAsync(apps);
        sidecar.Unreachable = true;
        using var response = await Post(session.Client, token, $"{ListenApi}/start", Consent(Guid.NewGuid(), patient));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(["0"], await RowsAsync(apps, "SELECT count(*) FROM ehr.ambient_consents WHERE patient_id = @id", patient));
    }

    [Fact]
    public async Task A_review_left_open_until_its_transcript_is_deleted_can_be_edited_but_not_accepted_as_offered()
    {
        apps.Dictation.Reset();
        var d = await DraftAsync(apps);
        var l = d.Listening;
        await using (var connection = await apps.OpenDatabaseAsync())
        await using (var purge = new NpgsqlCommand("SELECT deleted FROM ehr.purge_ambient_transcripts(interval '0')", connection))
        {
            Assert.True((long)(await purge.ExecuteScalarAsync(Cancel))! >= 1);
        }

        var html = await l.Session.Client.GetStringAsync(d.ReviewUrl, Cancel);
        Assert.Contains("transcript was deleted after 7 days, so its proposals can", html);
        Assert.DoesNotContain("value=\"accepted\"", html);

        var proposals = await ProposalsAsync(apps, l.Review);
        using (var accepted = await Decide(l.Session.Client, l.Token, d.ReviewUrl, proposals["objective"].Id, d.Version, "accepted"))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, accepted.StatusCode);
            Assert.Contains("transcript was deleted after 7 days, so its proposals can", await accepted.Content.ReadAsStringAsync(Cancel));
        }

        using var edited = await Decide(l.Session.Client, l.Token, d.ReviewUrl, proposals["objective"].Id, d.Version, "edited", "Synthetic: chest clear.");
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
    }
}
