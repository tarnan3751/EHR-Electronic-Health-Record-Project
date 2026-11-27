using System.Text.Json;
using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OrderTests;

namespace Ehr.Web.Tests.TestCases.UC14;

// Test Case ID: TC-UC14-26
// Purpose / scenario: A clinician writes a note in its four sections, Subjective, Objective, Assessment and Plan, and
//   signs it. The note is kept exactly as saved, and is shown under the four headings in that order.
// Type: Functional (successful flow)
// Requirements: FR-31, proposed FR-60
// Use case: UC 14, Review and sign a clinical note
// Preconditions: The clinician is signed in on the on-site app with the patient on their panel.
// Inputs and system conditions: POST /patients/{id}?handler=NewNote; GET and POST
//   /patients/{id}/notes/drafts/{draft} (Save, then Review); POST its /confirm.
// Test data: "Synthetic subjective: cough for two days.", "Synthetic objective: temperature 37.9, chest clear.",
//   "Synthetic assessment: viral illness." and "Synthetic plan: rest and fluids."
// Steps:
//   1. From the chart, choose "New note".
//   2. Type the four sections and save; open the draft again.
//   3. Choose "Sign note…", tick the confirmation and choose "Sign note".
// Expected result: Step 1: the editor has the four sections, labeled, in order, with the cursor in Subjective, and says
//   to complete all four before signing. Step 2: each section comes back as typed; the draft's plain text (what the
//   desktop app keeps) has the four headings, each before its text. Step 3: the note is signed as the clinician, holds
//   the four sections as saved and no History, and its page shows them under the four headings in order; signing is in
//   the audit log.
// Creator: Tahseen Arnan
public class TC_UC14_26_FourSectionsSuccessfulFlow(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    static readonly (string Name, object Value)[] Sections =
    [
        ("Subjective", "Synthetic subjective: cough for two days."), ("Objective", "Synthetic objective: temperature 37.9, chest clear."),
        ("Assessment", "Synthetic assessment: viral illness."), ("Plan", "Synthetic plan: rest and fluids."),
    ];

    [Fact]
    public async Task A_note_is_written_and_signed_in_four_sections()
    {
        var (clinician, patient, session) = await OnPanelAsync(apps);
        using var created = await PostFormAsync(session.Client, $"/patients/{patient}?handler=NewNote", $"/patients/{patient}");
        var editor = await ReadAsync(created);
        var (path, token) = (PathOf(created), Token(editor));
        Assert.Matches("(?s)<label[^>]*for=\"Subjective\"[^>]*>Subjective</label>.*<label[^>]*for=\"Objective\"[^>]*>Objective</label>.*<label[^>]*for=\"Assessment\"[^>]*>Assessment</label>.*<label[^>]*for=\"Plan\"[^>]*>Plan</label>", editor);
        Assert.Matches("<textarea[^>]*autofocus[^>]*id=\"Subjective\"|<textarea[^>]*id=\"Subjective\"[^>]*autofocus", editor);
        Assert.Contains("Fill in all four sections to sign it.", Words(editor));
        Assert.DoesNotContain("data-note-origin", editor);

        async Task<JsonElement> PostAsync(string target, long version, params (string Name, object Value)[] extra)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, target);
            request.Headers.Add("HX-Request", "true");
            request.Content = Form([("__RequestVerificationToken", token), ("Version", version), .. Sections, .. extra]);
            using var response = await session.Client.SendAsync(request, Cancel);
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel)).RootElement.Clone();
        }

        Assert.Equal(2, (await PostAsync(path + "?handler=Save", 1)).GetProperty("version").GetInt64());
        var reopened = await session.Client.GetStringAsync(path, Cancel);
        foreach (var (name, value) in Sections)
        {
            Assert.Matches($"<textarea[^>]*id=\"{name}\"[^>]*>\\s*{System.Text.RegularExpressions.Regex.Escape((string)value)}</textarea>", reopened);
        }

        var draft = path.Split('/')[^1];
        Assert.Equal(["Subjective\nSynthetic subjective: cough for two days.\n\nObjective\nSynthetic objective: temperature 37.9, chest clear.\n\nAssessment\nSynthetic assessment: viral illness.\n\nPlan\nSynthetic plan: rest and fluids."],
            await ProxyTests.QueryAsync(apps, $"SELECT body FROM ehr.clinical_drafts WHERE patient_id = @patient AND id = '{draft}'", patient));

        await PostAsync(path + "?handler=Review", 2);
        var url = (await PostAsync(path + "/confirm", 3, ("Confirmed", true))).GetProperty("url").GetString()!;
        Assert.Equal([$"{clinician.Id}|-|Synthetic subjective: cough for two days.|Synthetic objective: temperature 37.9, chest clear.|Synthetic assessment: viral illness.|Synthetic plan: rest and fluids."],
            await ProxyTests.QueryAsync(apps,
                "SELECT signer_id::text || '|' || coalesce(history, '-') || '|' || subjective || '|' || objective || '|' || assessment || '|' || plan FROM ehr.clinical_notes WHERE patient_id = @patient", patient));
        var page = await session.Client.GetStringAsync(url, Cancel);
        Assert.Matches("(?s)<h2>Subjective</h2><p class=\"sc-note__text\">Synthetic subjective: cough for two days\\.</p>\\s*<h2>Objective</h2><p class=\"sc-note__text\">Synthetic objective: temperature 37\\.9, chest clear\\.</p>\\s*<h2>Assessment</h2>.*<h2>Plan</h2>", page);
        Assert.DoesNotContain("<h2>History</h2>", page);
        Assert.DoesNotContain("data-note-format", page);
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.SignNote, clinician.Id, patient));
    }
}
