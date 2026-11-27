using System.Net;
using System.Text.Json;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OrderTests;

namespace Ehr.Web.Tests.TestCases.UC14;

// Test Case ID: TC-UC14-28
// Purpose / scenario: A note with an empty section, or one that shows nothing, isn't signed, and a section too long or
//   holding a null character, or a visit type that isn't one of the list's, isn't saved. Nothing is signed, and the
//   saved draft keeps what was saved before.
// Type: Functional (invalid input)
// Requirements: FR-31, proposed FR-60
// Use case: UC 14, Review and sign a clinical note
// Preconditions: The clinician is signed in on the on-site app with the patient on their panel, and has a new draft.
// Inputs and system conditions: POST /patients/{id}/notes/drafts/{draft} (Save, Review) and its /confirm.
// Test data: Subjective, Assessment and Plan typed and Objective empty; then an Objective of 16,001 characters; then
//   an Objective with a null character; then the visit type "general-surgery", which isn't one. For another draft, four
//   sections that show nothing: a zero-width space; a no-break space; a word joiner, a byte order mark and an
//   ideographic space; a tab, a soft hyphen and a narrow no-break space.
// Steps:
//   1. Save with Objective empty and choose "Sign note…"; then try to sign that version.
//   2. Save with the overlong Objective.
//   3. Save with the null character in Objective.
//   4. Save with the unknown visit type.
//   5. In the other draft, save the four sections that show nothing and choose "Sign note…"; then try to sign that
//      version.
// Expected result: Step 1: the draft is saved, "Sign note…" answers "Objective is required." and signing that
//   version is refused with the same; nothing is signed. Steps 2 and 3: nothing is saved ("Objective must be 16,000
//   characters or less."; the null character with 400). Step 4: nothing is saved ("Visit type must be selected from
//   the list."). The draft keeps the text of step 1, with no visit type. Step 5: the draft is saved as written, and "Sign
//   note…" and signing that version answer "Subjective is required.", and so for each section; nothing is
//   signed.
// Creator: Tahseen Arnan
public class TC_UC14_28_FourSectionsInvalidInput(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task An_empty_section_is_not_signed_and_an_overlong_or_null_one_is_not_saved()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        using var created = await PostFormAsync(session.Client, $"/patients/{patient}?handler=NewNote", $"/patients/{patient}");
        var (path, token) = (PathOf(created), Token(await ReadAsync(created)));
        async Task<(HttpStatusCode Status, string[] Errors)> PostAsync(string target, long version, string objective, params (string Name, object Value)[] extra)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, target);
            request.Headers.Add("HX-Request", "true");
            request.Content = Form([("__RequestVerificationToken", token), ("Version", version), ("Subjective", "Synthetic subjective: headache."), ("Objective", objective),
                ("Assessment", "Synthetic assessment: tension headache."), ("Plan", "Synthetic plan: rest."), .. extra]);
            using var response = await session.Client.SendAsync(request, Cancel);
            var body = await response.Content.ReadAsStringAsync(Cancel);
            if (body.Length == 0)
            {
                return (response.StatusCode, []);
            }

            var answer = JsonDocument.Parse(body).RootElement;
            var errors = answer.TryGetProperty("errors", out var list) ? list : answer.GetProperty("Errors");
            return (response.StatusCode, [.. errors.EnumerateArray().Select(error => error.GetString()!)]);
        }

        async Task RefusedAsync(string target, long version, string objective, string error, params (string Name, object Value)[] extra)
        {
            var (status, errors) = await PostAsync(target, version, objective, extra);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
            Assert.Contains(error, errors);
        }

        await RefusedAsync(path + "?handler=Review", 1, "", "Objective is required.");
        await RefusedAsync(path + "/confirm", 2, "", "Objective is required.", ("Confirmed", true));
        await RefusedAsync(path + "?handler=Save", 2, new string('o', 16_001), "Objective must be 16,000 characters or less.");
        // A null character is refused with 400 and no body, not with the page's own message ("Remove the null character
        // from Objective.", which NoteContentTests checks); either way nothing is saved.
        var (nullStatus, _) = await PostAsync(path + "?handler=Save", 2, "Synthetic objective\0");
        Assert.Equal(HttpStatusCode.BadRequest, nullStatus);
        await RefusedAsync(path + "?handler=Save", 2, "Synthetic objective", "Visit type must be selected from the list.", ("NoteTemplateCode", "general-surgery"));

        Assert.Equal(["2|Synthetic subjective: headache.||0"], await ProxyTests.QueryAsync(apps,
            "SELECT d.version || '|' || d.subjective || '|' || d.objective || '|' || (SELECT count(*) FROM ehr.clinical_notes n WHERE n.draft_id = d.id) || coalesce(d.note_template_code, '') FROM ehr.clinical_drafts d WHERE d.patient_id = @patient", patient));
    }

    [Fact]
    public async Task Sections_that_show_nothing_are_not_signed()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        using var created = await PostFormAsync(session.Client, $"/patients/{patient}?handler=NewNote", $"/patients/{patient}");
        var (path, token) = (PathOf(created), Token(await ReadAsync(created)));
        string[] blank = ["\u200B", "\u00A0", "\u2060\uFEFF\u3000", "\t\u00AD\u202F"];
        async Task<(HttpStatusCode Status, string[] Errors)> PostAsync(string target, long version, params (string Name, object Value)[] extra)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, target);
            request.Headers.Add("HX-Request", "true");
            request.Content = Form([("__RequestVerificationToken", token), ("Version", version), ("Subjective", blank[0]), ("Objective", blank[1]),
                ("Assessment", blank[2]), ("Plan", blank[3]), .. extra]);
            using var response = await session.Client.SendAsync(request, Cancel);
            var answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel)).RootElement;
            var errors = answer.TryGetProperty("errors", out var list) ? list : answer.GetProperty("Errors");
            return (response.StatusCode, [.. errors.EnumerateArray().Select(error => error.GetString()!)]);
        }

        string[] every = ["Subjective is required.", "Objective is required.", "Assessment is required.", "Plan is required."];
        foreach (var (status, errors) in new[] { await PostAsync(path + "?handler=Review", 1), await PostAsync(path + "/confirm", 2, ("Confirmed", true)) })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
            Assert.Equal(every, errors);
        }

        Assert.Equal([string.Join("|", blank) + "|0"], await ProxyTests.QueryAsync(apps,
            "SELECT d.subjective || '|' || d.objective || '|' || d.assessment || '|' || d.plan || '|' || (SELECT count(*) FROM ehr.clinical_notes n WHERE n.draft_id = d.id) FROM ehr.clinical_drafts d WHERE d.patient_id = @patient", patient));
    }
}
