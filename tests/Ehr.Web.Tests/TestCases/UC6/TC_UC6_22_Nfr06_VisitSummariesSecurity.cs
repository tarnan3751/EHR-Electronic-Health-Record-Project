using System.Text.RegularExpressions;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.NoteAddendumTests;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-22
// Purpose / scenario: The visit summaries page shows the patient no internal code or identifier: no ID of a summary,
//   note, patient or clinician, and no medical record number, anywhere in its markup, data attributes included; it is
//   never cached; and nothing of a note itself, or of its addenda, is on any portal page, for the patient or a proxy.
// Type: Non-functional (security)
// Requirements: NFR-06, proposed FR-55
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: The patient has a portal account, and has given access to someone with sensitive items, both signed
//   in, the proxy looking at the patient's record. Dr. Test, with the patient on their panel, has signed a note with a
//   distinctive Subjective and an addendum, released a summary of it and withdrawn it, and released another, sensitive.
// Inputs and system conditions: GET /portal/summaries (sensitive summaries asked for), /portal, /portal/results,
//   /portal/appointments, /portal/messages and /portal/access, as the patient and as the proxy, on the on-site app.
// Test data: The note's Subjective "Synthetic subjective: dry cough, quartzwing pattern, nocturnal." and Objective
//   "Synthetic objective: chest clear."; the addendum "Synthetic addendum: zebrafinch follow-up.".
// Steps:
//   1. As each, open Visit summaries with sensitive summaries asked for, and read its markup and headers.
//   2. As each, open every other portal page.
// Expected result: Step 1: Cache-Control: no-store; the markup has no UUID in any form (the antiforgery token aside,
//   which is no identifier of anything, and the account's own address in the app bar), no MRN, and none of the IDs of the patient, the note, the summaries or Dr. Test;
//   no data attribute holds anything but "standing", "withdrawn", "hidden" or "shown". Step 2: no page has the note's
//   subjective, objective, assessment or plan, or the addendum's text.
// Creator: Tahseen Arnan
public class TC_UC6_22_Nfr06_VisitSummariesSecurity(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_portal_shows_no_identifier_no_code_and_nothing_of_a_note()
    {
        var (clinician, session, patient) = await PortalPatientAsync(apps, "Guarded");
        var (proxy, _) = await ProxyForAsync(apps, patient, "GuardedProxy", sensitive: true);
        var note = await SignedNoteAsync(session.Client, patient.Patient, subjective: NoteSubjective);
        using (var added = await AddAsync(session.Client, patient.Patient, note, "Synthetic addendum: zebrafinch follow-up."))
        {
            Assert.Equal(System.Net.HttpStatusCode.OK, added.StatusCode);
        }

        using (var released = await ReleaseAsync(session.Client, patient.Patient, note, "We talked about the wrong visit."))
        {
            Assert.Contains("Released.", Words(await ReadAsync(released)));
        }

        var withdrawn = await StandingAsync(apps, patient.Patient);
        using (var gone = await WithdrawAsync(session.Client, patient.Patient, note, withdrawn))
        {
            Assert.Contains("Withdrawn.", Words(await ReadAsync(gone)));
        }

        using (var released = await ReleaseAsync(session.Client, patient.Patient, note, sensitive: true))
        {
            Assert.Contains("Released.", Words(await ReadAsync(released)));
        }

        var standing = await StandingAsync(apps, patient.Patient);
        string[] identifiers = [patient.Patient.ToString(), note.ToString(), withdrawn.ToString(), standing.ToString(), clinician.Id.ToString(),
            patient.Patient.ToString("N"), note.ToString("N"), standing.ToString("N"), WebApps.MrnOf(patient.Patient)];

        foreach (var reader in new[] { patient, proxy })
        {
            using var response = await reader.Client.GetAsync("/portal/summaries?sensitive=true", Cancel);
            var html = await ReadAsync(response);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Contains(TalkedAbout, html);
            Assert.Contains("was withdrawn by your care team.", Words(html));
            // The app bar names the signed-in account by its address, which these tests make of hex digits: the
            // account's own login, not an identifier of anything the page shows.
            var markup = WithoutTokens(html).Replace(reader.Email, "", StringComparison.OrdinalIgnoreCase);
            foreach (var identifier in identifiers)
            {
                Assert.DoesNotContain(identifier, markup, StringComparison.OrdinalIgnoreCase);
            }

            Assert.DoesNotMatch(new Regex("[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}", RegexOptions.IgnoreCase), markup);
            Assert.All(Regex.Matches(markup, "data-[a-z-]+=\"([^\"]*)\"").Select(m => m.Groups[1].Value),
                value => Assert.Contains(value, new[] { "standing", "withdrawn", "hidden", "shown", "" }.Concat(SessionBrowser.PortalSettings)));

            foreach (var path in new[] { "/portal", "/portal/summaries", "/portal/results", "/portal/appointments", "/portal/messages", "/portal/access" })
            {
                var page = Words(await reader.Client.GetStringAsync(path, Cancel));
                foreach (var noteText in new[] { NoteSubjective, Objective, NoteAssessment, NotePlan, "quartzwing", "zebrafinch" })
                {
                    Assert.DoesNotContain(noteText, page);
                }
            }
        }
    }
}
