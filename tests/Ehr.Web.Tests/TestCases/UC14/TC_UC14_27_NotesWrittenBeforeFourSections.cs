using System.Text.Json;
using Ehr.Testing;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.NoteAddendumTests;
using static Ehr.Web.Tests.OrderTests;

namespace Ehr.Web.Tests.TestCases.UC14;

// Test Case ID: TC-UC14-27
// Purpose / scenario: Notes and drafts written before notes had four sections (the expansion plan's step 14) are kept
//   as they were written. A signed one is shown as signed, with its History. An unsigned one opens in the editor with
//   its History in Subjective and a notice to move what belongs in Objective; nothing about it changes until its author
//   saves it, and that save writes the four sections and clears the History together. Shown off-site while the clinic's
//   server can't be reached, it is shown as saved.
// Type: Functional (alternative flow)
// Requirements: FR-31, proposed FR-60
// Use case: UC 14, Review and sign a clinical note
// Preconditions: The clinician is signed in with the patient on their panel. The patient has a note signed before step
//   14, and the clinician an unsigned draft written then, each with a History, as the SoapNotes migration left them.
// Inputs and system conditions: GET /patients/{id}/notes/{note}, its /addendum and /summary; POST
//   /api/desktop/offline-panel; GET /patients/{id}/notes/drafts/{draft} on-site and on the cloud app cut off from the
//   clinic; POST it with handler Save as the form posts without JavaScript, and with handler Review (save, then the
//   signing checks).
// Test data: the signed note's History "Synthetic history: signed as History, temperature 37.9."; the draft's History
//   "Synthetic history: drafted as History, chest clear."
// Steps:
//   1. Open the signed note, its addendum page and its summary page; download the offline copy.
//   2. Open the draft in the editor, and look at the saved draft.
//   3. Open the draft off-site with the clinic's server unreachable, and look at the saved draft.
//   4. Without JavaScript, save it with a visit type that isn't on the list ("general-surgery").
//   5. Choose "Sign note…" with Objective left empty.
//   6. Type Objective, and sign.
// Expected result: Step 1: each page shows History, Assessment and Plan, with "Written as History, Assessment and Plan,
//   before notes had four sections.", and no Subjective; the offline copy gives the note's History as signed. Step 2:
//   Subjective holds the History, Objective is empty, and a notice starting "Check:" says to move today's vital signs,
//   examination findings and reviewed results to Objective; the saved draft is exactly as it was. Step 3: the draft is
//   shown read-only under History, Assessment and Plan, and the saved draft is exactly as it was. Step 4: nothing is
//   saved ("Choose a visit type from the list."), and the page shows the History in Subjective with the "Check:" notice
//   again; the saved draft is exactly as it was. Step 5: the draft is saved, in one update that writes the four sections
//   and clears the History, and signing is refused with "Enter Objective before signing." Step 6: the note is signed in
//   four sections.
// Creator: Tahseen Arnan
public class TC_UC14_27_NotesWrittenBeforeFourSections(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    const string SignedHistory = "Synthetic history: signed as History, temperature 37.9.";

    const string DraftHistory = "Synthetic history: drafted as History, chest clear.";

    [Fact]
    public async Task A_note_signed_before_four_sections_is_shown_as_signed()
    {
        var (clinician, patient, session) = await OnPanelAsync(apps);
        var (_, note) = await EarlierNotes.SignedAsync(apps.Database, clinician.Id, patient, SignedHistory);
        foreach (var path in new[] { NotePath(patient, note), AddendumPath(patient, note), NotePath(patient, note) + "/summary" })
        {
            var page = await session.Client.GetStringAsync(path, Cancel);
            Assert.Contains("Written as History, Assessment and Plan, before notes had four sections.", page);
            Assert.Matches($"(?s)<h2>History</h2><p class=\"sc-note__text\">{System.Text.RegularExpressions.Regex.Escape(SignedHistory)}</p>\\s*<h2>Assessment</h2>.*<h2>Plan</h2>", page);
            Assert.DoesNotContain("<h2>Subjective</h2>", page);
        }

        var token = await apps.DesktopSignInAsync(apps.OnPrem, clinician);
        using var offline = await DesktopSignInTests.PostAsync(apps.OnPrem, "/api/desktop/offline-panel", token);
        Assert.Contains($"\"history\":\"{SignedHistory}\"", await offline.Content.ReadAsStringAsync(Cancel));
    }

    [Fact]
    public async Task A_draft_written_before_four_sections_changes_only_when_its_author_saves_it()
    {
        var (clinician, patient, session) = await OnPanelAsync(apps);
        var draft = await EarlierNotes.DraftAsync(apps.Database, clinician.Id, patient, DraftHistory);
        var path = $"/patients/{patient}/notes/drafts/{draft}";
        Task<List<string>> RowAsync() => ProxyTests.QueryAsync(apps, $"SELECT to_jsonb(d)::text FROM ehr.clinical_drafts d WHERE d.patient_id = @patient AND d.id = '{draft}'", patient);
        var saved = await RowAsync();

        var editor = await session.Client.GetStringAsync(path, Cancel);
        Assert.Matches($"<textarea[^>]*id=\"Subjective\"[^>]*>\\s*{System.Text.RegularExpressions.Regex.Escape(DraftHistory)}</textarea>", editor);
        Assert.Matches("<textarea[^>]*id=\"Objective\"[^>]*>\\s*</textarea>", editor);
        Assert.Contains("Check: this draft was written before notes had four sections. Its History is now in Subjective: move today's vital signs, examination findings and the results you reviewed to Objective before signing.", Words(editor));
        Assert.Equal(saved, await RowAsync());

        var offSite = await Client(apps.CloudCutOff, session.Jar).GetStringAsync(path, Cancel);
        Assert.Contains("It is shown as last saved.", offSite);
        Assert.Matches($"(?s)<h2>History</h2><p class=\"sc-note__text\">{System.Text.RegularExpressions.Regex.Escape(DraftHistory)}</p>", offSite);
        Assert.DoesNotContain("<textarea", offSite);
        Assert.Equal(saved, await RowAsync());

        using (var refused = await PostFormAsync(session.Client, path + "?handler=Save", path, ("Version", 1), ("Subjective", DraftHistory), ("Objective", ""),
            ("Assessment", "Synthetic assessment: earlier."), ("Plan", "Synthetic plan: earlier."), ("NoteTemplateCode", "general-surgery")))
        {
            Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            var page = await refused.Content.ReadAsStringAsync(Cancel);
            Assert.Contains("Choose a visit type from the list.", Words(page));
            Assert.Contains("Check: this draft was written before notes had four sections.", Words(page));
            Assert.Matches($"<textarea[^>]*id=\"Subjective\"[^>]*>\\s*{System.Text.RegularExpressions.Regex.Escape(DraftHistory)}</textarea>", page);
        }

        Assert.Equal(saved, await RowAsync());

        var token = Token(editor);
        async Task<HttpResponseMessage> PostAsync(string target, long version, string objective, params (string Name, object Value)[] extra)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, target);
            request.Headers.Add("HX-Request", "true");
            request.Content = Form([("__RequestVerificationToken", token), ("Version", version), ("Subjective", DraftHistory), ("Objective", objective),
                ("Assessment", "Synthetic assessment: earlier."), ("Plan", "Synthetic plan: earlier."), .. extra]);
            return await session.Client.SendAsync(request, Cancel);
        }

        using (var review = await PostAsync(path + "?handler=Review", 1, ""))
        {
            Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, review.StatusCode);
            var answer = JsonDocument.Parse(await review.Content.ReadAsStringAsync(Cancel)).RootElement;
            Assert.Equal(2, answer.GetProperty("version").GetInt64());
            Assert.Contains("Enter Objective before signing.", answer.GetProperty("errors").EnumerateArray().Select(e => e.GetString()));
        }

        // The one update, as the write-audit trigger recorded it: the four sections written and the History cleared.
        Assert.Equal([$"{DraftHistory}|null|\"\"|\"{DraftHistory}\""], await ProxyTests.QueryAsync(apps,
            $"""
            SELECT (details -> 'history' ->> 'old') || '|' || (details -> 'history' -> 'new')::text || '|' || (details -> 'objective' -> 'new')::text
                || '|' || (details -> 'subjective' -> 'new')::text
            FROM ehr.audit_events WHERE patient_id = @patient AND table_name = 'clinical_drafts' AND row_id = '{draft}' AND action = 'update'
            """, patient));

        using (var review = await PostAsync(path + "?handler=Review", 2, "Synthetic objective: chest clear."))
        {
            Assert.Equal(System.Net.HttpStatusCode.OK, review.StatusCode);
        }

        using (var signed = await PostAsync(path + "/confirm", 3, "Synthetic objective: chest clear.", ("Confirmed", true)))
        {
            Assert.Equal(System.Net.HttpStatusCode.OK, signed.StatusCode);
        }

        Assert.Equal([$"-|{DraftHistory}|Synthetic objective: chest clear."], await ProxyTests.QueryAsync(apps,
            $"SELECT coalesce(history, '-') || '|' || subjective || '|' || objective FROM ehr.clinical_notes WHERE patient_id = @patient AND draft_id = '{draft}'", patient));
    }
}
