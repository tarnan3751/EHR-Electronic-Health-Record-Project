using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
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
//   decision, and finishing the review, through the cloud app, which forwards them; the on-prem app sent the cloud app's
//   forwarding header with the shared key's proof, another key's, and with no key of its own; the stub unreachable at
//   start; ehr.purge_ambient_transcripts as the on-site app's timer runs it, on a transcript written 8 days ago.
// Test data: Synthetic patients and the stub's conversation.
// Steps:
//   1. Open the chart and the Listen page on each site; start listening through the cloud app.
//   2. Draft a visit at the clinic, open its review off-site, and decide a proposal through the cloud app; decide them
//      all at the clinic, and finish the review through the cloud app.
//   2a. Send the chart, a draft, the Listen page and the review, a decision, finishing and rejecting everything to the
//      on-prem app with the forwarding header, under each of the three proofs.
//   3. With the service unreachable, start listening.
//   4. Draft a visit, purge its transcript, open its review, and accept a proposal, edit it back to its own words, and
//      edit it into others.
// Expected result: Step 1: "Listen to the visit" on the clinic's chart only; off-site, the page's "Unavailable:" notice
//   and no listening controls; the start refused by the cloud app, nothing forwarded, nothing sent to the service. Step 2:
//   off-site, "Read-only here:" with the review's proposals, no transcript, no decisions and no finishing; the forwarded
//   decision and finishing refused with "Finish this review at the clinic…", the review still open with its transcript.
//   Step 2a: each answered as off-site, under every proof: no listening offered, no transcript, everything refused.
//   Step 3: 503, no consent recorded. Step 4: the review says the transcript was deleted after 7 days, offers no Accept,
//   and leaves the box to edit it empty, saying why; accepting, or editing back to the words offered, even spaced
//   differently, is refused with that sentence; editing into other words is saved.
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
        Assert.DoesNotContain("data-review-complete", html);

        var proposals = await ProposalsAsync(apps, d.Listening.Review);
        using (var forwarded = await Decide(offSite.Client, Token(html), d.ReviewUrl, proposals["objective"].Id, d.Version, "accepted"))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, forwarded.StatusCode);
            Assert.Contains("Finish this review at the clinic", await forwarded.Content.ReadAsStringAsync(Cancel));
        }

        Assert.Equal(["pending"], await RowsAsync(apps, "SELECT state FROM ehr.ai_field_proposals WHERE id = @id", proposals["objective"].Id));

        // Every proposal decided at the clinic: finishing it, which deletes the transcript, waits for the clinic too.
        var l = d.Listening;
        var version = d.Version;
        foreach (var field in new[] { "subjective", "objective", "assessment", "plan" })
        {
            using var rejected = await Decide(l.Session.Client, l.Token, d.ReviewUrl, proposals[field].Id, version++, "rejected");
            Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        }

        using (var finished = await ReviewPost(offSite.Client, Token(html), d.ReviewUrl, "Complete", ("Version", version)))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, finished.StatusCode);
            Assert.Contains("Finish this review at the clinic", await finished.Content.ReadAsStringAsync(Cancel));
        }

        Assert.Equal(["pending 1"], await RowsAsync(apps,
            "SELECT r.state || ' ' || (SELECT count(*) FROM ehr.ambient_transcripts t WHERE t.id = r.id) FROM ehr.ai_note_reviews r WHERE r.id = @id", l.Review));
        Assert.DoesNotContain("data-review-complete", await offSite.Client.GetStringAsync(d.ReviewUrl, Cancel));
        using var atTheClinic = await ReviewPost(l.Session.Client, l.Token, d.ReviewUrl, "Complete", ("Version", version));
        Assert.Equal(HttpStatusCode.OK, atTheClinic.StatusCode);
    }

    // What the cloud app forwards is off-site whatever proof it carries, so a key missing on the on-prem app or different
    // on the two apps never lets the conversation, or a decision on it, across the WAN: sent here with the shared key's
    // proof, with another key's, and to an on-prem app with no key at all, a request with the header is answered as
    // off-site. The chart and the draft offer no listening, the Listen page says it is unavailable, and the review shows
    // no transcript and takes no decision, no finishing and no rejecting of everything.
    [Fact]
    public async Task What_the_cloud_app_forwards_is_off_site_whatever_proof_it_carries()
    {
        apps.Dictation.Reset();
        var d = await DraftAsync(apps);
        var l = d.Listening;
        using var created = await PostFormAsync(l.Session.Client, $"/patients/{l.Patient}?handler=NewNote", $"/patients/{l.Patient}");
        var draft = PathOf(created);
        Assert.Contains("Listen to the visit into this draft", await created.Content.ReadAsStringAsync(Cancel));
        Assert.Contains("data-listen-link", await l.Session.Client.GetStringAsync($"/patients/{l.Patient}", Cancel));
        Assert.Contains("What brings you in today?", await l.Session.Client.GetStringAsync(d.ReviewUrl, Cancel));
        var objective = (await ProposalsAsync(apps, l.Review))["objective"].Id;

        await using var unkeyed = apps.OnPrem.WithWebHostBuilder(builder => builder.UseSetting("Ehr:CloudAudit:Key", ""));
        foreach (var (app, proof) in new (WebApplicationFactory<Program>, string)[]
        {
            (apps.OnPrem, AccountRateLimits.ForwardedProof(WebApps.CloudAuditKey)),
            (apps.OnPrem, AccountRateLimits.ForwardedProof("another key than the one both apps share")),
            (unkeyed, AccountRateLimits.ForwardedProof(WebApps.CloudAuditKey)),
        })
        {
            var forwarded = await SignInAsync(app, await apps.ClinicianAsync());
            forwarded.Client.DefaultRequestHeaders.Add(AccountRateLimits.ForwardedHeader, proof);
            Assert.DoesNotContain("data-listen-link", await forwarded.Client.GetStringAsync($"/patients/{l.Patient}", Cancel));
            Assert.Contains("data-ambient-offsite", await forwarded.Client.GetStringAsync($"/patients/{l.Patient}/listen", Cancel));
            Assert.DoesNotContain("Listen to the visit into this draft", await forwarded.Client.GetStringAsync(draft, Cancel));

            var html = await forwarded.Client.GetStringAsync(d.ReviewUrl, Cancel);
            Assert.Contains("Read-only here:", html);
            Assert.DoesNotContain("What brings you in today?", html);
            Assert.DoesNotContain("data-review-complete", html);
            using var decided = await Decide(forwarded.Client, Token(html), d.ReviewUrl, objective, d.Version, "accepted");
            using var finished = await ReviewPost(forwarded.Client, Token(html), d.ReviewUrl, "Complete", ("Version", d.Version));
            using var rejected = await ReviewPost(forwarded.Client, Token(html), d.ReviewUrl, "RejectAll", ("Version", d.Version));
            foreach (var refused in new[] { decided, finished, rejected })
            {
                Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
                Assert.Contains("Finish this review at the clinic", await refused.Content.ReadAsStringAsync(Cancel));
            }
        }

        Assert.Equal(["pending 1"], await RowsAsync(apps,
            "SELECT r.state || ' ' || (SELECT count(*) FROM ehr.ambient_transcripts t WHERE t.id = r.id) FROM ehr.ai_note_reviews r WHERE r.id = @id", l.Review));
        Assert.Equal(["pending"], await RowsAsync(apps, "SELECT DISTINCT state FROM ehr.ai_field_proposals WHERE review_id = @id", l.Review));
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
        Assert.True(await PurgeAsync(apps, l.Review) >= 1);

        var html = await l.Session.Client.GetStringAsync(d.ReviewUrl, Cancel);
        Assert.Contains("transcript was deleted after 7 days, so its proposals can", html);
        Assert.DoesNotContain("value=\"accepted\"", html);
        // Nor offered, ready to approve, in the box to edit it: the page says why.
        Assert.Matches("<textarea id=\"edited-objective\"[^>]*aria-describedby=\"edited-objective-hint\"[^>]*></textarea>", html);
        Assert.Contains("Write it as your own note: with the visit's transcript deleted, the words offered can't go in as they are.", html);

        var proposals = await ProposalsAsync(apps, l.Review);
        foreach (var (decision, words) in new[] { ("accepted", ""), ("edited", "Your chest is clear."), ("edited", "  Your chest\n is   clear. ") })
        {
            using var refused = await Decide(l.Session.Client, l.Token, d.ReviewUrl, proposals["objective"].Id, d.Version, decision, words);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Contains("transcript was deleted after 7 days, so its proposals can", await refused.Content.ReadAsStringAsync(Cancel));
        }

        using var edited = await Decide(l.Session.Client, l.Token, d.ReviewUrl, proposals["objective"].Id, d.Version, "edited", "Synthetic: chest clear.");
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
    }
}
