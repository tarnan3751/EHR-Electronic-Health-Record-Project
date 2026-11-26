using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.NoteAddendumTests;
using static Ehr.Web.Tests.OrderTests;

namespace Ehr.Web.Tests.TestCases.UC14;

// Test Case ID: TC-UC14-08
// Purpose / scenario: A note signed as sensitive follows the rules for sensitive items: it is left out of the chart
//   until asked for, each view of it is recorded, its addenda are sensitive, and it is never kept offline. A sensitive
//   addendum to an ordinary note is left out of the note's page until asked for.
// Type: Functional (alternative flow: sensitive notes and addenda)
// Requirements: FR-31, FR-39 (proposed), proposed FR-49
// Use case: UC 14, Review and sign a clinical note
// Preconditions: The clinician is signed in on the on-site app with the patient on their panel.
// Inputs and system conditions: POST .../confirm with Sensitive; GET /patients/{id} and ?sensitive=true; GET the
//   note; POST .../addendum with Sensitive; POST /api/desktop/offline-panel.
// Test data: One note signed with "Sensitive" ticked; one ordinary note with a sensitive and an ordinary addendum.
// Steps:
//   1. Sign a note with "Sensitive" ticked, and an ordinary note. Open the chart.
//   2. Choose "Show sensitive items". Open the sensitive note and sign an addendum to it without ticking "Sensitive".
//   3. On the ordinary note, sign one addendum with "Sensitive" ticked and one without. Open the note again.
//   4. Choose "Show sensitive addenda".
//   5. Download the offline copy.
// Expected result: Step 1: the chart lists one signed note and says "1 signed note is not shown."; no view of
//   sensitive items is recorded. Step 2: both notes are listed, the sensitive one tagged; the note's page says
//   "Sensitive note. Your view of it is logged." and each view is in the audit log as a view of sensitive items; its
//   addendum is stored sensitive. Step 3: the ordinary note's page shows the ordinary addendum and says "1 addendum
//   is not shown." Step 4: both are shown, the sensitive one tagged, and the view is recorded. Step 5: the copy has
//   the ordinary note with only its ordinary addendum and "sensitiveAddendaNotKept": 1, nothing of the sensitive note
//   or addendum, and "sensitiveNotKept": 1; the draft sync sends the sensitive note's draft with no text, marked
//   withheld; and an administrator's audit page for the patient has none of the sensitive note's or addenda's text,
//   including the text saved while it was a draft.
// Creator: Tahseen Arnan
public class TC_UC14_08_SensitiveNotesAndAddenda(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_sensitive_note_and_a_sensitive_addendum_are_left_out_until_asked_for_and_never_kept_offline()
    {
        var (clinician, patient, session) = await OnPanelAsync(apps);
        const string secret = "Synthetic sensitive subjective: low mood for three weeks.";
        var sensitive = await SignedNoteAsync(session.Client, patient, sensitive: true, subjective: secret);
        // With only a sensitive note, the chart doesn't say there are none.
        var only = Words(await session.Client.GetStringAsync($"/patients/{patient}", Cancel));
        Assert.Contains("No other signed notes.", only);
        Assert.DoesNotContain("No signed notes.", only);
        var ordinary = await SignedNoteAsync(session.Client, patient);
        Task<long> ViewsAsync() => apps.AuditEventsAsync(AuditActions.ReadSensitive, clinician.Id, patient);
        var views = await ViewsAsync();

        var chart = await session.Client.GetStringAsync($"/patients/{patient}", Cancel);
        Assert.Contains("Sensitive items hidden: 1 signed note is not shown.", Words(chart));
        Assert.Contains(NotePath(patient, ordinary), chart);
        Assert.DoesNotContain(NotePath(patient, sensitive), chart);
        Assert.Equal(views, await ViewsAsync());

        chart = await session.Client.GetStringAsync($"/patients/{patient}?sensitive=true", Cancel);
        Assert.Matches($"(?s)href=\"{NotePath(patient, sensitive)}\">Signed note[^<]*</a>\\s*<span class=\"sc-tag sc-tag--warning\">Sensitive</span>", chart);
        Assert.Equal(views + 1, await ViewsAsync());
        var page = await session.Client.GetStringAsync(NotePath(patient, sensitive), Cancel);
        Assert.Contains("Sensitive note. Your view of it is logged.", Words(page));
        Assert.Equal(views + 2, await ViewsAsync());
        var form = await session.Client.GetStringAsync(AddendumPath(patient, sensitive), Cancel);
        Assert.Contains("This note is sensitive, so its addenda are too.", form);
        Assert.DoesNotContain("addendum-sensitive", form);
        using var forced = await AddAsync(session.Client, patient, sensitive, "Synthetic addendum to the sensitive note.");
        Assert.Contains("Synthetic addendum to the sensitive note.", await ReadAsync(forced));

        using var marked = await AddAsync(session.Client, patient, ordinary, "Synthetic sensitive addendum.", sensitive: true);
        // Its author is shown it straight after signing, which is a recorded view.
        Assert.Contains("Synthetic sensitive addendum.", await ReadAsync(marked));
        using var plain = await AddAsync(session.Client, patient, ordinary, "Synthetic ordinary addendum.");
        views = await ViewsAsync();
        page = await session.Client.GetStringAsync(NotePath(patient, ordinary), Cancel);
        Assert.Contains("Sensitive addenda hidden. 1 addendum is not shown.", Words(page));
        Assert.Contains("Synthetic ordinary addendum.", page);
        Assert.DoesNotContain("Synthetic sensitive addendum.", page);
        Assert.Equal(views, await ViewsAsync());
        page = await session.Client.GetStringAsync(NotePath(patient, ordinary) + "?sensitive=true", Cancel);
        Assert.Contains("Sensitive addenda shown. Your view of them is logged.", Words(page));
        Assert.Matches("(?s)<span class=\"sc-tag sc-tag--warning\" data-addendum-sensitive=\"own\">Sensitive</span>\\s*</p>\\s*<p class=\"sc-note__text\">Synthetic sensitive addendum\\.", page);
        Assert.Equal(views + 1, await ViewsAsync());
        Assert.Equal(
            [$"{clinician.Id} true Synthetic addendum to the sensitive note.", $"{clinician.Id} true Synthetic sensitive addendum.", $"{clinician.Id} false Synthetic ordinary addendum."],
            await AddendaAsync(apps, patient));

        var token = await apps.DesktopSignInAsync(apps.OnPrem, clinician);
        using var offline = await DesktopSignInTests.PostAsync(apps.OnPrem, "/api/desktop/offline-panel", token);
        var copy = await offline.Content.ReadAsStringAsync(Cancel);
        Assert.Contains("Synthetic ordinary addendum.", copy);
        Assert.DoesNotContain("sensitive addendum", copy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("the sensitive note", copy);
        Assert.DoesNotContain("low mood", copy);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(copy, "\"signedAt\":\"[^\"]+\",\"signer\""));
        Assert.Contains("\"sensitiveNotKept\":1", copy);
        // The ordinary note says it has a sensitive addendum that isn't in the copy.
        Assert.Contains("\"sensitiveAddendaNotKept\":1", copy);

        // The draft sync (UC 8) sends the ordinary note's draft as before, and the sensitive note's draft with no text,
        // marked withheld, so a desktop that had its text removes it.
        using var desktop = apps.OnPrem.CreateClient();
        desktop.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var pulled = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(desktop, "/api/desktop/drafts/pull",
            new { Since = "0", Patients = new[] { patient } }, System.Text.Json.JsonSerializerOptions.Web, Cancel);
        var drafts = await pulled.Content.ReadAsStringAsync(Cancel);
        Assert.Equal(System.Net.HttpStatusCode.OK, pulled.StatusCode);
        Assert.Contains(Subjective, drafts);
        Assert.DoesNotContain("low mood", drafts);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(drafts, "\"body\":\"\",\"version\":\\d+,\"withheld\":true"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(drafts, "\"withheld\":false"));

        // An administrator's audit page: nothing of the sensitive note's text, though it was typed and saved as a draft
        // before anyone marked it; and since step 18a nothing of the ordinary note's either, as for any patient data.
        var admin = await SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("The Reviewer of Notes", Ehr.Data.Accounts.StaffRoles.Admin));
        var log = await admin.Client.GetStringAsync($"/admin/audit?Patient={patient}", Cancel);
        Assert.DoesNotContain("cough for two days", log);
        Assert.DoesNotContain("low mood", log);
        Assert.DoesNotContain("Synthetic sensitive addendum", log);
        Assert.DoesNotContain("the sensitive note", log);
    }
}
