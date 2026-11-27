using System.Net;
using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.NoteAddendumTests;
using static Ehr.Web.Tests.OrderTests;

namespace Ehr.Web.Tests.TestCases.UC14;

// Test Case ID: TC-UC14-14
// Purpose / scenario: A signed note is marked sensitive after signing, with a reason. Its text doesn't change; it
//   then follows the rules for sensitive items, its addenda with it; only the person who marked it, or the note's
//   signer, can unmark it, with a reason. An addendum is marked and unmarked the same way.
// Type: Functional (alternative flow: sensitivity after signing)
// Requirements: FR-31, FR-39 (proposed), proposed FR-51
// Use case: UC 14, Review and sign a clinical note
// Preconditions: The clinician is signed in on the on-site app with the patient on their panel and has signed a note
//   with one addendum. Two colleagues with the patient on their panel are signed in too.
// Inputs and system conditions: GET and POST /patients/{id}/notes/{note}/sensitivity, with ?addendum=...; GET the
//   chart and the note; POST /api/desktop/drafts/pull and /api/desktop/offline-panel.
// Test data: Reasons "Patient asked for it.", "Marked the wrong note.", "Mentions a family member."
// Steps:
//   1. As the first colleague, follow "Mark this note sensitive", post with no reason, then with "Patient asked for it."
//   2. Open the chart, the note, the draft sync and the offline copy.
//   3. As the second colleague, open the page to unmark it, and post a reason to it.
//   4. As the signer, unmark it with "Marked the wrong note."
//   5. As the signer, try to unmark the addendum; then, as the second colleague, mark it with "Mentions a family member."
// Expected result: Step 1: without a reason, 422 "Say why, so the record shows it."; with one, "Saved. Marked
//   sensitive". Step 2: the chart leaves the note out and counts it; the note's page says "Sensitive note. Your view of
//   it is logged." and shows the reason and time; the text is unchanged; its addendum is sensitive; the draft sync
//   withholds the draft's text; the offline copy has no note and counts one sensitive item. Step 3: the page says only
//   the person who marked it, or the note's signer, can unmark it, with no form; the post is answered 403 and nothing
//   changes. Step 4: unmarked, with the reason shown; the chart lists the note again; its addendum, sensitive only
//   because the note was, is unmarked with it and shown. Step 5: unmarking the addendum again is answered 409; marked
//   on its own by the colleague, it is left out of the note's page until asked for, stays marked when the note is marked
//   and unmarked again, and is then unmarked by the note's signer and shown.
// Creator: Tahseen Arnan
public class TC_UC14_14_SensitiveAfterSigning(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task A_signed_note_and_an_addendum_are_marked_sensitive_and_unmarked_with_a_reason_by_those_who_may()
    {
        var (signer, patient, session) = await OnPanelAsync(apps);
        var note = await SignedNoteAsync(session.Client, patient);
        using var added = await AddAsync(session.Client, patient, note, "Synthetic addendum: culture negative.");
        var addendum = Guid.Parse((await ProxyTests.QueryAsync(apps, "SELECT id::text FROM ehr.note_addenda WHERE patient_id = @patient", patient)).Single());
        var first = await apps.CreateStaffAsync("Dr. First", Ehr.Data.Accounts.StaffRoles.Clinician);
        var second = await apps.CreateStaffAsync("Dr. Second", Ehr.Data.Accounts.StaffRoles.Clinician);
        await apps.AssignAsync(first, patient);
        await apps.AssignAsync(second, patient);
        var (marker, bystander) = (await SignInAsync(apps.OnPrem, first), await SignInAsync(apps.OnPrem, second));
        var text = await ProxyTests.QueryAsync(apps, "SELECT subjective || objective || assessment || plan || signer_id || signed_at FROM ehr.clinical_notes WHERE patient_id = @patient", patient);

        // Before anyone marks it: the second colleague opens the note, which the app records; and the desktop syncs,
        // which gives the watermark a later pull starts from.
        Assert.Contains(Subjective, await bystander.Client.GetStringAsync(NotePath(patient, note), Cancel));
        var token = await apps.DesktopSignInAsync(apps.OnPrem, signer);
        using var desktop = apps.OnPrem.CreateClient();
        desktop.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        async Task<string> PullAsync(string since)
        {
            using var response = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(desktop, "/api/desktop/drafts/pull",
                new { Since = since, Patients = new[] { patient } }, System.Text.Json.JsonSerializerOptions.Web, Cancel);
            return await response.Content.ReadAsStringAsync(Cancel);
        }

        var synced = await PullAsync("0");
        Assert.Contains("cough for two days", synced);
        var watermark = System.Text.Json.JsonDocument.Parse(synced).RootElement.GetProperty("watermark").GetString()!;
        Assert.DoesNotContain("\"withheld\":true", await PullAsync(watermark));

        var page = await marker.Client.GetStringAsync(NotePath(patient, note), Cancel);
        Assert.Matches($"<a [^>]*href=\"{SensitivityPath(patient, note)}\"[^>]*>Mark this note sensitive</a>", page);
        using var unexplained = await ChangeSensitivityAsync(marker.Client, patient, note, mark: true, null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unexplained.StatusCode);
        Assert.Contains("Not saved. Say why, so the record shows it.", Words(await unexplained.Content.ReadAsStringAsync(Cancel)));
        using var marked = await ChangeSensitivityAsync(marker.Client, patient, note, mark: true, "Patient asked for it.");
        page = await ReadAsync(marked);
        Assert.Contains("Saved. Marked sensitive, with your reason.", Words(page));
        Assert.Contains("Sensitive note. Your view of it is logged.", Words(page));
        Assert.Matches("data-sensitivity-reason>Marked sensitive by Dr. First [A-Z][a-z]{2} \\d{1,2}, \\d{4} \\d{2}:\\d{2} UTC: Patient asked for it\\.", page);

        // The same form sent again (a double click, or Back and send): it asked to mark, the note is marked, and it
        // is not undone.
        using var twice = await ChangeSensitivityAsync(marker.Client, patient, note, mark: true, "Patient asked for it.");
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.Contains("It is already marked sensitive. Nothing was changed.", Words(await twice.Content.ReadAsStringAsync(Cancel)));
        // The addendum's mark is inherited from the note's: nobody marked it itself.
        Assert.Equal([$"note true {first.Id} Patient asked for it.", "addendum true none Patient asked for it."], await SensitivityAsync(apps, patient));
        Assert.Equal(text, await ProxyTests.QueryAsync(apps, "SELECT subjective || objective || assessment || plan || signer_id || signed_at FROM ehr.clinical_notes WHERE patient_id = @patient", patient));

        var chart = Words(await session.Client.GetStringAsync($"/patients/{patient}", Cancel));
        Assert.Contains("Sensitive items hidden. 1 signed note is not shown.", chart);
        // The desktop's next sync, from where it left off: the draft comes again, with no text, to be emptied there.
        var drafts = await PullAsync(watermark);
        Assert.Matches("\"body\":\"\",\"version\":\\d+,\"withheld\":true", drafts);
        Assert.DoesNotContain("cough for two days", drafts);
        // The view the second colleague made before the note was marked is in the audit log, as the app wrote it,
        // dated before the mark.
        Assert.Equal(["true"], await ProxyTests.QueryAsync(apps,
            $"""
            SELECT bool_and(e.occurred_at < n.sensitive_changed_at)::text FROM ehr.audit_events e JOIN ehr.clinical_notes n ON n.id = e.row_id
            WHERE e.patient_id = @patient AND e.action = 'read' AND e.table_name = 'clinical_notes' AND e.actor_id = '{second.Id}' HAVING count(*) = 1
            """, patient));
        using var offline = await DesktopSignInTests.PostAsync(apps.OnPrem, "/api/desktop/offline-panel", token);
        var copy = await offline.Content.ReadAsStringAsync(Cancel);
        Assert.DoesNotContain("cough for two days", copy);
        Assert.Contains("\"sensitiveNotKept\":1", copy);

        // Someone who neither marked it nor signed the note: told why not, and refused whatever they post.
        var refusedPage = await bystander.Client.GetStringAsync(SensitivityPath(patient, note), Cancel);
        Assert.Contains("data-may-not-unmark", refusedPage);
        Assert.DoesNotContain("data-change-sensitivity", refusedPage);
        var ownToken = Token(refusedPage);
        using var refused = await bystander.Client.PostAsync(SensitivityPath(patient, note), Form(("__RequestVerificationToken", ownToken), ("Mark", false), ("Reason", "Let me see it.")), Cancel);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.StartsWith("note true ", (await SensitivityAsync(apps, patient))[0]);

        using var unmarked = await ChangeSensitivityAsync(session.Client, patient, note, mark: false, "Marked the wrong note.");
        page = await ReadAsync(unmarked);
        Assert.Contains("Saved. No longer marked sensitive.", Words(page));
        Assert.Matches("data-sensitivity-reason>Unmarked as sensitive [^<]*UTC: Marked the wrong note\\.", page);
        // After unmarking, the desktop's sync sends the draft's text again.
        Assert.Contains("cough for two days", await PullAsync(System.Text.Json.JsonDocument.Parse(drafts).RootElement.GetProperty("watermark").GetString()!));
        // The addendum's mark was the note's: it goes with it, and the addendum is shown without asking.
        Assert.Equal(["note false none Marked the wrong note.", "addendum false none Marked the wrong note."], await SensitivityAsync(apps, patient));
        Assert.DoesNotContain("Sensitive items hidden", Words(await session.Client.GetStringAsync($"/patients/{patient}", Cancel)));
        Assert.DoesNotContain("Sensitive addenda hidden", Words(page));
        Assert.Contains("Synthetic addendum: culture negative.", page);

        // The addendum, not marked now: unmarking it changes nothing; then it is marked on its own by another clinician.
        using var shown = await ChangeSensitivityAsync(session.Client, patient, note, mark: false, "Not sensitive.", addendum);
        Assert.Equal(HttpStatusCode.Conflict, shown.StatusCode);
        var views = await apps.AuditEventsAsync(AuditActions.ReadSensitive, second.Id, patient);
        using var again = await ChangeSensitivityAsync(bystander.Client, patient, note, mark: true, "Mentions a family member.", addendum);
        // The person who marked it is shown it once more, which is a recorded view; after that it is left out.
        Assert.Contains("Synthetic addendum: culture negative.", await ReadAsync(again));
        Assert.Equal(views + 1, await apps.AuditEventsAsync(AuditActions.ReadSensitive, second.Id, patient));
        Assert.Equal($"addendum true {second.Id} Mentions a family member.", (await SensitivityAsync(apps, patient))[1]);
        page = await session.Client.GetStringAsync(NotePath(patient, note), Cancel);
        Assert.DoesNotContain("Synthetic addendum: culture negative.", page);
        Assert.Contains("Sensitive addenda hidden. 1 addendum is not shown.", Words(page));

        // Marking and unmarking the note leaves the addendum's own mark, and the colleague's name on it.
        using var noteAgain = await ChangeSensitivityAsync(marker.Client, patient, note, mark: true, "Patient asked for it.");
        using var noteBack = await ChangeSensitivityAsync(marker.Client, patient, note, mark: false, "Marked the wrong note.");
        Assert.Equal($"addendum true {second.Id} Mentions a family member.", (await SensitivityAsync(apps, patient))[1]);
        // The note's signer may unmark what someone else marked: through the page, and it is shown without asking.
        using var unmarkedBySigner = await ChangeSensitivityAsync(session.Client, patient, note, mark: false, "Not sensitive.", addendum);
        Assert.Contains("Synthetic addendum: culture negative.", await ReadAsync(unmarkedBySigner));
        Assert.Equal("addendum false none Not sensitive.", (await SensitivityAsync(apps, patient))[1]);
    }
}
