using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.NoteAddendumTests;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-17
// Purpose / scenario: A visit summary the care team withdrew leaves a placeholder where it was (Tahseen's decision of
//   21 November 2026): its date and nothing else, no text, no reason, no name. A new summary of the same visit stands
//   beside it. A withdrawn sensitive summary's placeholder is shown only to someone who could have seen the summary,
//   after asking; a proxy without sensitive items gets no placeholder and no hint.
// Type: Functional (alternative flow: withdrawn summaries)
// Requirements: proposed FR-55
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: The patient has a portal account and has given access to two people, one with sensitive items and
//   one without, each signed in and looking at the record. Dr. Test has the patient on their panel and has signed two
//   notes.
// Inputs and system conditions: GET /portal/summaries, with and without ?sensitive=true, as the patient and each proxy,
//   on the on-site app.
// Test data: Reasons "Synthetic private reason." and "Synthetic second reason."; a replacement summary "We talked about
//   the right visit."; a sensitive summary "We talked about something private.".
// Steps:
//   1. Dr. Test releases a summary of the first note and withdraws it. Each opens Visit summaries.
//   2. Dr. Test releases a replacement. The patient opens Visit summaries.
//   3. Dr. Test releases a sensitive summary of the second note and withdraws it. Each opens Visit summaries, and asks
//      for sensitive summaries.
// Expected result: Step 1: each sees "Your care team withdrew the visit summary from <date> UTC." and nothing of the
//   summary, the reason or Dr. Test, anywhere in the markup. Step 2: the replacement and the placeholder, of the same
//   date, the replacement first. Step 3: the patient and the proxy with sensitive items see "Sensitive summaries
//   hidden. 1 visit summary is not shown." and, once they ask, a second placeholder; the proxy without them gets the
//   page they had before, asking or not.
// Creator: Tahseen Arnan
public class TC_UC6_17_WithdrawnSummaryPlaceholder(WebApps apps) : IClassFixture<WebApps>
{
    const string Placeholder = "Your care team withdrew the visit summary from";

    [Fact]
    public async Task A_withdrawn_summary_leaves_a_dated_placeholder_and_nothing_of_itself()
    {
        var (_, session, patient) = await PortalPatientAsync(apps, "Withdrawn");
        var (allowed, _) = await ProxyForAsync(apps, patient, "WithdrawnAllowed", sensitive: true);
        var (without, _) = await ProxyForAsync(apps, patient, "WithdrawnWithout", sensitive: false);
        var first = await SignedNoteAsync(session.Client, patient.Patient);
        var second = await SignedNoteAsync(session.Client, patient.Patient);
        using (var released = await ReleaseAsync(session.Client, patient.Patient, first))
        {
            Assert.Contains("Released.", Words(await ReadAsync(released)));
        }

        using (var withdrawn = await WithdrawAsync(session.Client, patient.Patient, first, await StandingAsync(apps, patient.Patient), "Synthetic private reason."))
        {
            Assert.Contains("Withdrawn.", Words(await ReadAsync(withdrawn)));
        }

        var date = await PortalDateAsync(apps, patient.Patient, first);
        foreach (var reader in new[] { patient, allowed, without })
        {
            var page = await PortalAsync(reader.Client);
            Assert.Contains($"{Placeholder} {date}.", Words(page));
            foreach (var hidden in new[] { "cough", "chest", "fluids", "Synthetic private reason", "Dr. Test" })
            {
                Assert.DoesNotContain(hidden, page, StringComparison.OrdinalIgnoreCase);
            }
        }

        using (var replaced = await ReleaseAsync(session.Client, patient.Patient, first, "We talked about the right visit."))
        {
            Assert.Contains("Released.", Words(await ReadAsync(replaced)));
        }

        var both = Words(await PortalAsync(patient.Client));
        Assert.Contains("We talked about the right visit.", both);
        Assert.Contains($"{Placeholder} {date}.", both);
        Assert.True(both.IndexOf("We talked about the right visit.", StringComparison.Ordinal) < both.IndexOf(Placeholder, StringComparison.Ordinal));

        var withoutBefore = WithoutTokens(await PortalAsync(without.Client));
        using (var released = await ReleaseAsync(session.Client, patient.Patient, second, "We talked about something private.", sensitive: true))
        {
            Assert.Contains("Released.", Words(await ReadAsync(released)));
        }

        using (var withdrawn = await WithdrawAsync(session.Client, patient.Patient, second, await StandingIdForAsync(patient.Patient, second), "Synthetic second reason."))
        {
            Assert.Contains("Withdrawn.", Words(await ReadAsync(withdrawn)));
        }

        foreach (var reader in new[] { patient, allowed })
        {
            var asked = Words(await PortalAsync(reader.Client));
            Assert.Contains("Sensitive summaries hidden. 1 visit summary is not shown.", asked);
            Assert.Equal(1, CountOf(asked, Placeholder));
            var shown = await PortalAsync(reader.Client, sensitive: true);
            Assert.Equal(2, CountOf(Words(shown), Placeholder));
            Assert.DoesNotContain("something private", shown);
            Assert.DoesNotContain("Synthetic second reason", shown);
        }

        Assert.Equal(withoutBefore, WithoutTokens(await PortalAsync(without.Client)));
        Assert.Equal(withoutBefore, WithoutTokens(await PortalAsync(without.Client, sensitive: true)));
    }

    async Task<Guid> StandingIdForAsync(Guid patient, Guid note) =>
        Guid.Parse((await ProxyTests.QueryAsync(apps, $"SELECT id::text FROM ehr.visit_summaries WHERE patient_id = @patient AND note_id = '{note}' AND withdrawn_at IS NULL", patient)).Single());

    static int CountOf(string text, string part) => (text.Length - text.Replace(part, "", StringComparison.Ordinal).Length) / part.Length;
}
