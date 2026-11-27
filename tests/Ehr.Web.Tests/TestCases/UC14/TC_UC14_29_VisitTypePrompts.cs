using System.Net;
using System.Text.RegularExpressions;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OrderTests;

namespace Ehr.Web.Tests.TestCases.UC14;

// Test Case ID: TC-UC14-29
// Purpose / scenario: The editor says what each section holds, and a clinician picks a visit type, whose prompts for
//   what to cover are shown under the sections. The prompts are guidance: nothing of them goes into the note. The
//   draft keeps the visit type chosen.
// Type: Functional (alternative flow)
// Requirements: FR-31, proposed FR-60
// Use case: UC 14, Review and sign a clinical note
// Preconditions: The clinician is signed in on the on-site app with the patient on their panel, and has a new draft.
//   The reference data holds the five visit types of db/reference/37-note-templates.sql.
// Inputs and system conditions: GET and POST /patients/{id}/notes/drafts/{draft} (Save, Review); POST its /confirm.
// Test data: the visit type "Follow-up visit" (follow-up), and the four sections typed by the clinician.
// Steps:
//   1. Open the new draft.
//   2. Choose "Follow-up visit", type the four sections and save; open the draft again.
//   3. Sign the note.
// Expected result: Step 1: the "Visit type" list offers None, then the five visit types in their order, None chosen;
//   each section shows what it holds and no prompt. Step 2: the draft keeps follow-up, the list shows it chosen, and
//   each section shows its follow-up prompt; the saved sections hold only what was typed. Step 3: the signed note holds
//   only what was typed, and its page shows no prompt.
// Creator: Tahseen Arnan
public class TC_UC14_29_VisitTypePrompts(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    const string Prompt = "How each problem has been since the last visit";

    [Fact]
    public async Task A_visit_type_shows_prompts_beside_the_sections_and_puts_nothing_in_the_note()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        using var created = await PostFormAsync(session.Client, $"/patients/{patient}?handler=NewNote", $"/patients/{patient}");
        var editor = await ReadAsync(created);
        var (path, token) = (PathOf(created), Token(editor));
        Assert.Equal(["No visit type", "Follow-up visit", "New patient visit", "Acute visit", "Well-child visit", "Telephone visit"],
            Regex.Matches(editor, "<option[^>]*>([^<]*)</option>").Select(match => match.Groups[1].Value));
        // None is the list's first option, and no other is chosen.
        Assert.DoesNotContain("selected=\"selected\"", editor);
        Assert.Contains("What the clinician found: today&#x27;s vital signs and examination findings", editor);
        Assert.Matches("<p id=\"note-prompt-subjective\" class=\"sc-field__hint\" data-note-prompt=\"subjective\" hidden=\"hidden\"></p>", editor);

        async Task<HttpResponseMessage> PostAsync(string target, long version, params (string Name, object Value)[] extra)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, target);
            request.Headers.Add("HX-Request", "true");
            request.Content = Form([("__RequestVerificationToken", token), ("Version", version), ("NoteTemplateCode", "follow-up"),
                ("Subjective", "Synthetic subjective: no new symptoms."), ("Objective", "Synthetic objective: blood pressure 128/78."),
                ("Assessment", "Synthetic assessment: hypertension controlled."), ("Plan", "Synthetic plan: same dose."), .. extra]);
            return await session.Client.SendAsync(request, Cancel);
        }

        using (var saved = await PostAsync(path + "?handler=Save", 1))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }

        var reopened = await session.Client.GetStringAsync(path, Cancel);
        Assert.Matches("<option selected=\"selected\" value=\"follow-up\"|<option value=\"follow-up\"[^>]*selected=\"selected\"", reopened);
        foreach (var section in new[] { "subjective", "objective", "assessment", "plan" })
        {
            Assert.Matches($"<p id=\"note-prompt-{section}\" class=\"sc-field__hint\" data-note-prompt=\"{section}\">Follow-up visit: [^<]+</p>", reopened);
        }

        Assert.Contains(Prompt, reopened);
        Assert.Equal(["follow-up 0"], await ProxyTests.QueryAsync(apps,
            $"SELECT note_template_code || ' ' || (position('{Prompt}' IN body) + position('{Prompt}' IN subjective)) FROM ehr.clinical_drafts WHERE patient_id = @patient", patient));

        using (var review = await PostAsync(path + "?handler=Review", 2))
        {
            Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        }

        using var signed = await PostAsync(path + "/confirm", 3, ("Confirmed", true));
        Assert.Equal(HttpStatusCode.OK, signed.StatusCode);
        var url = System.Text.Json.JsonDocument.Parse(await signed.Content.ReadAsStringAsync(Cancel)).RootElement.GetProperty("url").GetString()!;
        Assert.DoesNotContain("Follow-up visit", await session.Client.GetStringAsync(url, Cancel));
        Assert.Equal(["0"], await ProxyTests.QueryAsync(apps,
            $"SELECT (position('Follow-up' IN subjective || objective || assessment || plan))::text FROM ehr.clinical_notes WHERE patient_id = @patient", patient));
    }
}
