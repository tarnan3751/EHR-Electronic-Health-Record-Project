using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.NoteAddendumTests;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-18
// Purpose / scenario: When the care team withdraws a visit summary that had been viewed in the portal, by the patient
//   or by someone they share their record with, the patient is emailed the same notice as for a new summary, with no
//   health details and a link from the portal's configured address (Tahseen's decision of 21 November 2026); a summary
//   nobody viewed is withdrawn without an email. The release notice earlier that day doesn't hold it back, and the
//   proxy is never emailed.
// Type: Functional (alternative flow: withdrawn summaries)
// Requirements: proposed FR-55
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: The patient has a confirmed portal account and has given access to someone, signed in and looking at
//   the record. Dr. Test has the patient on their panel and has signed a note.
// Inputs and system conditions: POST .../summary and .../summaries/{summary}/withdraw as Dr. Test, the withdrawals with
//   the request's Host header set to another site; GET /portal/summaries as the patient or the proxy in between.
// Test data: The test's summary, released three times.
// Steps:
//   1. Dr. Test releases a summary. The patient opens Visit summaries. Dr. Test withdraws it.
//   2. Dr. Test releases another. Only the proxy opens Visit summaries. Dr. Test withdraws it.
//   3. Dr. Test releases a third. Nobody opens Visit summaries. Dr. Test withdraws it.
// Expected result: The patient's emails: one for the first release; one for the first withdrawal; none for the second
//   release (the same day); one for the second withdrawal; none for the third release or withdrawal. Each says
//   "There is something new in your patient portal" with the release notice's text and a button to
//   https://localhost/portal/summaries, and nothing of the summary, the reason or the forged host. The proxy's address
//   gets none.
// Creator: Tahseen Arnan
public class TC_UC6_18_WithdrawnAfterViewEmail(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_withdrawal_emails_the_patient_only_when_the_summary_had_been_viewed()
    {
        var (_, session, patient) = await PortalPatientAsync(apps, "Notified");
        var (proxy, _) = await ProxyForAsync(apps, patient, "NotifiedProxy", sensitive: false);
        var note = await SignedNoteAsync(session.Client, patient.Patient);
        var proxyEmails = EmailsTo(apps, proxy.Email).Count;
        async Task<Guid> ReleaseOneAsync()
        {
            using var released = await ReleaseAsync(session.Client, patient.Patient, note);
            Assert.Contains("Released.", Words(await ReadAsync(released)));
            return await StandingAsync(apps, patient.Patient);
        }

        // Withdrawn as the page posts it, naming another site as its host: the link is the configured portal's.
        async Task WithdrawForgedAsync(Guid summary)
        {
            var path = WithdrawPath(patient.Patient, note, summary);
            using var page = new HttpRequestMessage(HttpMethod.Get, path);
            page.Headers.Host = "attacker.example";
            using var shown = await session.Client.SendAsync(page, Cancel);
            using var post = new HttpRequestMessage(HttpMethod.Post, path);
            post.Headers.Host = "attacker.example";
            post.Content = Form(("__RequestVerificationToken", Token(await shown.Content.ReadAsStringAsync(Cancel))), ("Reason", "Synthetic private reason."));
            using var withdrawn = await session.Client.SendAsync(post, Cancel);
            Assert.Contains("Withdrawn.", Words(await ReadAsync(withdrawn)));
        }

        var seenByPatient = await ReleaseOneAsync();
        Assert.Single(EmailsTo(apps, patient.Email));
        Assert.Contains(TalkedAbout, await PortalAsync(patient.Client));
        await WithdrawForgedAsync(seenByPatient);
        Assert.Equal(2, EmailsTo(apps, patient.Email).Count);

        var seenByProxy = await ReleaseOneAsync();
        Assert.Equal(2, EmailsTo(apps, patient.Email).Count);
        Assert.Contains(TalkedAbout, await PortalAsync(proxy.Client));
        await WithdrawForgedAsync(seenByProxy);
        Assert.Equal(3, EmailsTo(apps, patient.Email).Count);

        var unseen = await ReleaseOneAsync();
        await WithdrawForgedAsync(unseen);
        Assert.Equal(3, EmailsTo(apps, patient.Email).Count);

        foreach (var (_, subject, body) in EmailsTo(apps, patient.Email))
        {
            Assert.Equal("There is something new in your patient portal", subject);
            Assert.Equal("Your clinic added something to your patient portal. For your privacy it isn't included in this email. Sign in to the patient portal to see it.", body);
            var action = apps.Emails.Actions[body];
            Assert.Equal(("See what's new", Base + "portal/summaries"), (action.Label, action.Link));
            foreach (var word in new[] { "cough", "reason", "withdrawn", "Dr. Test", "attacker" })
            {
                Assert.DoesNotContain(word, subject + body + action.Link, StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.Equal(proxyEmails, EmailsTo(apps, proxy.Email).Count);
    }
}
