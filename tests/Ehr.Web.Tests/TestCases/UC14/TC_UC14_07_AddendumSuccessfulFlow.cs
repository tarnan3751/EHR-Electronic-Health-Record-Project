using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.NoteAddendumTests;
using static Ehr.Web.Tests.OrderTests;

namespace Ehr.Web.Tests.TestCases.UC14;

// Test Case ID: TC-UC14-07
// Purpose / scenario: A clinician adds an addendum to a signed note. The note stays exactly as it was signed; the
//   addendum is shown after it with its author and time; a second clinician's addendum follows in order; the desktop
//   app's offline copy has the note and its addenda.
// Type: Functional (successful flow, continued: addenda)
// Requirements: FR-31, proposed FR-49
// Use case: UC 14, Review and sign a clinical note
// Preconditions: The clinician is signed in on the on-site app with the patient on their panel and has signed a note.
//   A colleague with the patient on their panel is signed in too.
// Inputs and system conditions: GET /patients/{id}/notes/{note}; GET and POST /patients/{id}/notes/{note}/addendum;
//   POST /api/desktop/offline-panel.
// Test data: "Synthetic addendum: culture negative." and the colleague's "Synthetic addendum: seen in follow-up."
// Steps:
//   1. Open the signed note and follow "Add addendum". Type the first text and choose "Sign addendum".
//   2. As the colleague, do the same with the second text.
//   3. Download the offline copy as the clinician.
// Expected result: Step 1: the note page says "Addendum signed." and lists the addendum under "Addenda" with the clinician's
//   name and the time; the note's Subjective, Objective, Assessment and Plan are unchanged, and the page still has nothing to type
//   into; the write is in the audit log under the clinician. Step 2: both addenda, the clinician's first, each under
//   its own author. Step 3: the patient's copy has the note with its signer and both addenda in order.
// Creator: Tahseen Arnan
public class TC_UC14_07_AddendumSuccessfulFlow(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_addendum_is_signed_after_the_note_which_stays_as_it_was()
    {
        var (clinician, patient, session) = await OnPanelAsync(apps);
        var note = await SignedNoteAsync(session.Client, patient);
        var colleague = await apps.CreateStaffAsync("Dr. Colleague", Ehr.Data.Accounts.StaffRoles.Clinician);
        await apps.AssignAsync(colleague, patient);
        var other = await SignInAsync(apps.OnPrem, colleague);
        var before = await ProxyTests.QueryAsync(apps, "SELECT to_jsonb(n)::text FROM ehr.clinical_notes n WHERE n.patient_id = @patient", patient);
        var page = await session.Client.GetStringAsync(NotePath(patient, note), Cancel);
        Assert.Contains("No addenda.", page);
        Assert.Matches($"<a [^>]*href=\"{AddendumPath(patient, note)}\"[^>]*>Add addendum</a>", page);
        var form = await session.Client.GetStringAsync(AddendumPath(patient, note), Cancel);
        Assert.Contains("can't be changed. An addendum is added after it, signed by you.", Words(form));
        Assert.Contains(Subjective, form);

        using var first = await AddAsync(session.Client, patient, note, "Synthetic addendum: culture negative.");
        page = await ReadAsync(first);
        Assert.Contains("Addendum signed. It's shown after the note.", Words(page));
        Assert.Matches("(?s)<ol class=\"sc-list\" data-addenda>.*?<strong>Dr. Test</strong>.*?signed <time[^>]*>\\d{4}-\\d{2}-\\d{2} \\d{2}:\\d{2} UTC</time>.*?Synthetic addendum: culture negative\\.", page);
        Assert.Contains(Subjective, page);
        Assert.DoesNotContain("textarea", page);

        using var second = await AddAsync(other.Client, patient, note, "Synthetic addendum: seen in follow-up.");
        page = await ReadAsync(second);
        Assert.Matches("(?s)<strong>Dr. Test</strong>.*?culture negative\\..*?<strong>Addendum by Dr. Colleague, not the note&#x27;s author</strong>.*?seen in follow-up\\.", page);
        Assert.Equal([$"{clinician.Id} false Synthetic addendum: culture negative.", $"{colleague.Id} false Synthetic addendum: seen in follow-up."], await AddendaAsync(apps, patient));
        Assert.Equal(before, await ProxyTests.QueryAsync(apps, "SELECT to_jsonb(n)::text FROM ehr.clinical_notes n WHERE n.patient_id = @patient", patient));
        Assert.Equal([clinician.Id.ToString(), colleague.Id.ToString()], await ProxyTests.QueryAsync(apps,
            "SELECT actor_id::text FROM ehr.audit_events WHERE table_name = 'note_addenda' AND action = 'insert' AND patient_id = @patient ORDER BY occurred_at", patient));
        Assert.True(await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient) >= 3);

        var token = await apps.DesktopSignInAsync(apps.OnPrem, clinician);
        using var offline = await DesktopSignInTests.PostAsync(apps.OnPrem, "/api/desktop/offline-panel", token);
        var copy = await offline.Content.ReadAsStringAsync(Cancel);
        // Signed in four sections (step 14): a desktop build that knows only History is given Subjective and Objective there.
        Assert.Matches("\"notes\":\\[\\{\"signedAt\":\"[^\"]+\",\"signer\":\"Dr. Test\",\"history\":\"Subjective\\\\nSynthetic subjective: cough for two days.\\\\n\\\\nObjective\\\\nSynthetic objective: chest clear.\"", copy);
        Assert.Matches("\"addenda\":\\[\\{[^\\]]*\"author\":\"Dr. Test\",\"text\":\"Synthetic addendum: culture negative.\"[^}]*\\},\\{[^\\]]*\"author\":\"Dr. Colleague\",\"text\":\"Synthetic addendum: seen in follow-up.\"[^}]*\\}\\]", copy);
    }
}
