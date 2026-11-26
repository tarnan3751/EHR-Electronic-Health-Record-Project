using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.NoteAddendumTests;
using static Ehr.Web.Tests.OrderTests;

namespace Ehr.Web.Tests.TestCases.UC14;

// Test Case ID: TC-UC14-17
// Purpose / scenario: Marking a signed note sensitive marks each of its addenda, as inherited from the note.
//   Unmarking the note clears only the inherited marks: an addendum marked on its own stays marked until it is
//   unmarked itself. While the note is sensitive, none of its addenda is shown without asking or kept offline.
// Type: Functional (alternative flow: sensitivity after signing, addenda)
// Requirements: FR-31, FR-39 (proposed), proposed FR-51
// Use case: UC 14, Review and sign a clinical note
// Preconditions: The clinician is signed in on the on-site app with the patient on their panel and has signed a note
//   with two addenda, the second marked sensitive on its own.
// Inputs and system conditions: POST /patients/{id}/notes/{note}/sensitivity; GET the note; POST
//   /api/desktop/offline-panel.
// Test data: Addenda "Synthetic addendum one: culture negative.", "Synthetic addendum two: names a relative.",
//   "Synthetic addendum three: signed later."; reasons "Patient asked for it.", "Marked the wrong note."
// Steps:
//   1. Mark the note sensitive, then sign a third addendum to it.
//   2. Open the chart without asking for sensitive items, then the note; open the offline copy.
//   3. Unmark the note, and open it and the offline copy again.
// Expected result: Step 1: all three addenda are sensitive; the first and third are recorded as inherited from the
//   note, the second keeps its own mark. Step 2: the chart leaves the note out and no view is recorded; the note's
//   page shows them, the inherited ones labeled "Sensitive with the note", and the view is recorded; the offline copy has none of the texts. Step 3: the first and third addenda are unmarked
//   with the note's reason and shown; the second is still sensitive, left out until asked for, and not in the
//   offline copy, which counts it.
// Creator: Tahseen Arnan
public class TC_UC14_17_SensitiveNotesAddendaInherit(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_sensitive_notes_addenda_are_sensitive_with_it_and_only_the_inherited_marks_go_with_it()
    {
        var (clinician, patient, session) = await OnPanelAsync(apps);
        var note = await SignedNoteAsync(session.Client, patient);
        const string one = "Synthetic addendum one: culture negative.";
        const string two = "Synthetic addendum two: names a relative.";
        const string three = "Synthetic addendum three: signed later.";
        using var first = await AddAsync(session.Client, patient, note, one);
        using var second = await AddAsync(session.Client, patient, note, two, sensitive: true);
        Task<List<string>> MarksAsync() => ProxyTests.QueryAsync(apps,
            "SELECT left(text, 22) || ' ' || sensitive || ' ' || sensitive_inherited FROM ehr.note_addenda WHERE patient_id = @patient ORDER BY signed_at, id", patient);
        Assert.Equal(["Synthetic addendum one false false", "Synthetic addendum two true false"], await MarksAsync());
        var token = await apps.DesktopSignInAsync(apps.OnPrem, clinician);
        async Task<string> OfflineAsync()
        {
            using var offline = await DesktopSignInTests.PostAsync(apps.OnPrem, "/api/desktop/offline-panel", token);
            return await offline.Content.ReadAsStringAsync(Cancel);
        }

        // Before the mark, the offline copy has the note and its plain addendum.
        var copy = await OfflineAsync();
        Assert.Contains(one, copy);
        Assert.DoesNotContain(two, copy);

        // 1. Marked: every addendum is sensitive, the unmarked one by inheritance; one signed afterwards too.
        using var marked = await ChangeSensitivityAsync(session.Client, patient, note, mark: true, "Patient asked for it.");
        Assert.Contains("Saved. Marked sensitive, with your reason.", Words(await ReadAsync(marked)));
        using var third = await AddAsync(session.Client, patient, note, three);
        Assert.Equal(["Synthetic addendum one true true", "Synthetic addendum two true false", "Synthetic addendum thr true true"], await MarksAsync());

        // 2. Not shown without asking, and no view recorded; shown when asked for, recorded; never kept offline.
        Task<long> ViewsAsync() => apps.AuditEventsAsync(AuditActions.ReadSensitive, clinician.Id, patient);
        var views = await ViewsAsync();
        var chart = await session.Client.GetStringAsync($"/patients/{patient}", Cancel);
        Assert.DoesNotContain(NotePath(patient, note), chart);
        Assert.Equal(views, await ViewsAsync());
        var page = await session.Client.GetStringAsync(NotePath(patient, note), Cancel);
        Assert.Contains("Sensitive note. Your view of it is logged.", Words(page));
        Assert.All(new[] { one, two, three }, text => Assert.Contains(text, page));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(page, "data-addendum-sensitive=\"inherited\">Sensitive with the note</span>").Count);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(page, "data-addendum-sensitive=\"own\">Sensitive</span>"));
        Assert.True(await ViewsAsync() > views);
        copy = await OfflineAsync();
        Assert.All(new[] { one, two, three, Subjective }, text => Assert.DoesNotContain(text, copy));
        Assert.Contains("\"sensitiveNotKept\":1", copy);

        // 3. Unmarked: the inherited marks go with the note's; the addendum marked on its own stays.
        using var unmarked = await ChangeSensitivityAsync(session.Client, patient, note, mark: false, "Marked the wrong note.");
        page = await ReadAsync(unmarked);
        Assert.Equal(["Synthetic addendum one false false", "Synthetic addendum two true false", "Synthetic addendum thr false false"], await MarksAsync());
        Assert.Contains(one, page);
        Assert.Contains(three, page);
        Assert.DoesNotContain(two, page);
        Assert.Contains("Sensitive addenda hidden. 1 addendum is not shown.", Words(page));
        copy = await OfflineAsync();
        Assert.Contains(one, copy);
        Assert.Contains(three, copy);
        Assert.DoesNotContain(two, copy);
        Assert.Contains("\"sensitiveAddendaNotKept\":1", copy);
    }
}
