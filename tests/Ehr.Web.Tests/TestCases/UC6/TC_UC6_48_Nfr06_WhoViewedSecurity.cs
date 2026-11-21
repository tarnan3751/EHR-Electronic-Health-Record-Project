using System.Net;
using System.Text.RegularExpressions;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.RecordViewTests;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-48
// Purpose / scenario: Who viewed my record shows no identifier of anything, no staff member's address, no table or
//   action of the audit log, and no medical record number; it is never cached; it takes no patient from the request,
//   so each patient sees only their own record's views, whatever the address says; and only a portal account reaches it.
// Type: Non-functional (security)
// Requirements: NFR-06, proposed FR-57
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: Two patients with confirmed portal accounts, signed in on the on-site app; the first has given a third
//   access with sensitive items, who is looking at the record. Dr. Test has both on their panel and has opened each
//   chart, the first's with sensitive items and a note they signed for the first.
// Inputs and system conditions: GET /portal/views as each patient and as the proxy, also with ?patientId= the other
//   patient's ID and with ?older= and ?newer= values that aren't moments; GET it as Dr. Test and with no session; on the
//   on-site app.
// Test data: None beyond the accounts.
// Steps:
//   1. As each patient and as the proxy, open Who viewed my record and read its markup and headers.
//   2. As the first patient, open it with ?patientId= the second's ID, and with cursors that aren't moments.
//   3. As Dr. Test, and with no session, open it.
// Expected result: Step 1: Cache-Control: no-store; no ID of a patient, an account or Dr. Test, and no UUID in any form
//   (the antiforgery token and the addresses of the account and of the proxy the patient named aside), no address of
//   Dr. Test's, no table or action name of the log, no MRN; no data attribute holds anything but "you", "staff",
//   "proxy", "lab" or "explained"; each patient's page has its own views only. Step 2: the same lines as without them.
//   Step 3: sent to the portal's sign-in.
// Creator: Tahseen Arnan
public partial class TC_UC6_48_Nfr06_WhoViewedSecurity(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    static readonly string[] LogWords =
    [
        "audit_events", "patients", "clinical_notes", "clinical_drafts", "ai_note_reviews", "ai_field_proposals", "message_threads",
        "visit_summaries", "read_sensitive", "sent_to_lab", "break_glass", "onprem", "record-sensitive", "lab-order",
    ];

    [Fact]
    public async Task The_page_shows_no_identifier_and_takes_no_patient_from_the_request()
    {
        var (clinician, session, first) = await ViewedPatientAsync(apps, "Guarded");
        var (proxy, _) = await ProxyForAsync(apps, first, "Guardproxy", sensitive: true);
        var second = await ProxyTests.PersonAsync(apps, "Synthetic", "Elsewhere");
        await apps.AssignAsync(clinician, second.Patient);
        await session.Client.GetStringAsync($"/patients/{first.Patient}?sensitive=true", Cancel);
        await session.Client.GetStringAsync(NoteAddendumTests.NotePath(first.Patient, await NoteAddendumTests.SignedNoteAsync(session.Client, first.Patient)), Cancel);
        await session.Client.GetStringAsync($"/patients/{second.Patient}", Cancel);

        // Step 1.
        string[] identifiers = [first.Patient.ToString(), second.Patient.ToString(), proxy.Patient.ToString(), clinician.Id.ToString(), first.Account.ToString(),
            proxy.Account.ToString(), first.Patient.ToString("N"), clinician.Id.ToString("N")];
        // The app bar names the signed-in account by its address, and the patient's page names a proxy by the address
        // access was given to: these tests make addresses of hex digits, which are no identifier of anything.
        foreach (var (reader, addresses) in new[] { (first, new[] { first.Email, proxy.Email }), (proxy, new[] { proxy.Email }), (second, new[] { second.Email }) })
        {
            using var response = await reader.Client.GetAsync(ViewsPath, Cancel);
            var html = await ReadAsync(response);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var markup = addresses.Aggregate(WithoutTokens(html), (rest, address) => rest.Replace(address, "", StringComparison.OrdinalIgnoreCase));
            Assert.All(identifiers, identifier => Assert.DoesNotContain(identifier, markup, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotMatch(AnyUuid(), markup);
            Assert.DoesNotContain(clinician.Email, markup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(WebApps.MrnOf(first.Patient), markup);
            Assert.DoesNotContain(WebApps.MrnOf(second.Patient), markup);
            Assert.All(LogWords, word => Assert.DoesNotContain(word, Words(markup), StringComparison.Ordinal));
            Assert.All(DataAttribute().Matches(markup), value => Assert.Contains(value.Groups[1].Value, (string[])["you", "staff", "proxy", "lab", "explained"]));
            Assert.Contains(Lines(html), line => line.StartsWith("staff | Dr. Test", StringComparison.Ordinal));
        }

        // Each patient's own: the first's has the note read and the sensitive read; the second's neither.
        var firstLines = Lines(await PageAsync(first.Client));
        var secondLines = Lines(await PageAsync(second.Client));
        Assert.Contains(firstLines, line => line.Contains("with its sensitive items", StringComparison.Ordinal));
        Assert.Contains(firstLines, line => line.Contains("visit note", StringComparison.Ordinal));
        Assert.DoesNotContain(secondLines, line => line.Contains("with its sensitive items", StringComparison.Ordinal) || line.Contains("visit note", StringComparison.Ordinal));

        // Step 2.
        static List<string> Others(string html) => [.. Lines(html).Where(line => !line.StartsWith("you | You | Who viewed", StringComparison.Ordinal))];
        var plain = Others(await PageAsync(first.Client));
        Assert.Equal(plain, Others(await PageAsync(first.Client, $"?patientId={second.Patient}")));
        foreach (var query in new[] { "?older=yesterday", "?newer=1", "?older=" + Guid.NewGuid(), "?older=2026-13-40T99:99:99.000000Z&newer=x" })
        {
            Assert.Equal(plain, Others(await PageAsync(first.Client, query)));
        }

        // Step 3.
        foreach (var client in new[] { session.Client, Client(apps.OnPrem, new CookieContainer()) })
        {
            using var refused = await client.GetAsync(ViewsPath, Cancel);
            Assert.Equal("/portal/sign-in", PathOf(refused));
        }
    }

    [GeneratedRegex("[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}")]
    private static partial Regex AnyUuid();

    [GeneratedRegex("data-[a-z-]+=\"([^\"]*)\"")]
    private static partial Regex DataAttribute();
}
