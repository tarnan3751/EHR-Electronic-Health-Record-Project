using Xunit;
using static Ehr.Web.Tests.RecordViewTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-64
// Purpose / scenario: A visit note's views are named as a note being written and a signed note, which shows the
//   patient's sensitive items when the note is sensitive; a proxy without sensitive items is never told of either, and
//   sees those pages' views of the record as "Record".
// Type: Functional (alternative flow: visit notes)
// Requirements: proposed FR-57, FR-19
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: The patient has a confirmed portal account, signed in on the on-site app, and has given two other
//   patients of the clinic access, one with sensitive items and one without, each looking at the record. Dr. Test
//   signed in, then put the patient on their panel.
// Inputs and system conditions: Dr. Test writes and signs a note marked sensitive, then GETs its page; the patient and
//   both proxies GET /portal/views; on the on-site app.
// Test data: The note's synthetic history, assessment and plan.
// Steps:
//   1. Dr. Test writes and signs the note, then opens it.
//   2. The patient and each proxy open Who viewed the record.
// Expected result: The patient's lines from Dr. Test include "Draft visit note" and "Visit note, with sensitive items";
//   the proxy with sensitive items has the same; the proxy without has neither, nor any line saying "visit note" or
//   "sensitive", and has "Record" for those pages instead.
// Creator: Tahseen Arnan
public class TC_UC6_64_VisitNotesNamed(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_note_is_named_while_written_and_once_signed_to_those_who_may_see_it()
    {
        var (_, session, patient) = await ViewedPatientAsync(apps, "Noted");
        var (allowed, _) = await VisitSummaryTests.ProxyForAsync(apps, patient, "Notesallowed", sensitive: true);
        var (without, _) = await VisitSummaryTests.ProxyForAsync(apps, patient, "Noteswithout", sensitive: false);

        // Step 1.
        var note = await NoteAddendumTests.SignedNoteAsync(session.Client, patient.Patient, sensitive: true);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await session.Client.GetAsync(NoteAddendumTests.NotePath(patient.Patient, note), Cancel)).StatusCode);

        // Step 2.
        var own = PartsOf(await PageAsync(patient.Client), "staff");
        Assert.Contains("Draft visit note", own);
        Assert.Contains("Visit note, with sensitive items", own);
        var full = PartsOf(await PageAsync(allowed.Client), "staff");
        Assert.Contains("Draft visit note", full);
        Assert.Contains("Visit note, with sensitive items", full);
        var restricted = PartsOf(await PageAsync(without.Client), "staff");
        Assert.Contains("Record", restricted.Select(Uncounted));
        Assert.DoesNotContain(restricted, part => part.Contains("visit note", StringComparison.OrdinalIgnoreCase) || part.Contains("sensitive", StringComparison.Ordinal));
    }
}
