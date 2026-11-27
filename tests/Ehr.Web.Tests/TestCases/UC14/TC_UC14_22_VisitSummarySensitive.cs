using System.Net;
using System.Text.RegularExpressions;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.NoteAddendumTests;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests.TestCases.UC14;

// Test Case ID: TC-UC14-22
// Purpose / scenario: A visit summary's sensitivity (Tahseen's decision of 21 November 2026): it starts sensitive,
//   inherited, when its note or an addendum is sensitive, or with a mark of its own when the clinician ticks
//   "Sensitive"; it follows its note and addenda afterwards, and is marked on its own and unmarked under the addenda's
//   rules. The note's page leaves a sensitive summary of an ordinary note out until asked for, and records the view.
// Type: Functional (alternative flow: sensitive summaries)
// Requirements: FR-39, proposed FR-51, proposed FR-55
// Use case: UC 14, Review and sign a clinical note
// Preconditions: Dr. Test has the patient on their panel and signs four notes: the first as sensitive, the second
//   with a sensitive addendum, the third and fourth ordinary. A colleague also has the patient on their panel.
// Inputs and system conditions: GET and POST .../summary for each note; POST .../sensitivity for notes, addenda and
//   summaries; GET the notes' pages, with and without sensitive items asked for.
// Test data: Reasons "Discussed a private matter.", "Names a relative.", "Reviewed with the patient.".
// Steps:
//   1. Open the summary page of the first and second notes and release a summary of each.
//   2. Release a summary of the third note with "Sensitive" ticked.
//   3. Open the third note's page, then with sensitive items asked for.
//   4. Release a summary of the fourth note; mark the note sensitive; unmark it; sign an ordinary addendum to it and
//      mark that addendum on its own; unmark the addendum; sign a sensitive addendum to the note.
//   5. The colleague tries to unmark the third note's summary; then Dr. Test unmarks it.
// Expected result: Step 1: each page says the summary is sensitive too and offers no tick; both summaries are
//   sensitive, inherited. Step 2: sensitive, Dr. Test's own mark, shown tagged to Dr. Test. Step 3: "Sensitive summaries
//   hidden: 1 visit summary is not shown." and no "Write a summary" offer; asked for, it is shown, the page says the
//   view is recorded, and it is recorded as a view of sensitive items. Step 4: the fourth summary follows each change,
//   inherited, and its mark goes when nothing of the note is sensitive; while a source is sensitive its unmarking isn't
//   offered. Step 5: the colleague is told only the person who marked it or the note's signer can unmark it, and is
//   refused; Dr. Test unmarks it, and the note's page says so with the reason.
// Creator: Tahseen Arnan
public class TC_UC14_22_VisitSummarySensitive(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_summary_is_sensitive_with_its_note_and_addenda_or_on_its_own_under_the_addenda_rules()
    {
        var (clinician, patient, session) = await OnPanelAsync(apps);
        var sensitiveNote = await SignedNoteAsync(session.Client, patient, sensitive: true);
        var withAddendum = await SignedNoteAsync(session.Client, patient);
        using (var signed = await AddAsync(session.Client, patient, withAddendum, "Synthetic sensitive addendum.", sensitive: true))
        {
            Assert.Equal(HttpStatusCode.OK, signed.StatusCode);
        }

        var ownNote = await SignedNoteAsync(session.Client, patient);
        var later = await SignedNoteAsync(session.Client, patient);
        Task<List<string>> MarkAsync(Guid note) => ProxyTests.QueryAsync(apps,
            $"SELECT sensitive || ' ' || sensitive_inherited || ' ' || coalesce(sensitive_marked_by::text, 'none') FROM ehr.visit_summaries WHERE patient_id = @patient AND note_id = '{note}'", patient);

        // 1. From a sensitive note, and from a note with a sensitive addendum: sensitive, inherited; no tick offered.
        foreach (var note in new[] { sensitiveNote, withAddendum })
        {
            var form = await session.Client.GetStringAsync(SummaryPath(patient, note), Cancel);
            Assert.Contains("Sensitive note. The note or one of its addenda is sensitive, so this summary is too.", Words(form));
            Assert.DoesNotContain("name=\"Sensitive\"", form);
            using var released = await ReleaseAsync(session.Client, patient, note);
            Assert.Contains("Released.", Words(await ReadAsync(released)));
            Assert.Equal(["true true none"], await MarkAsync(note));
        }

        // 2. Ticked as it is released: its own mark, the releaser's.
        Assert.Contains("name=\"Sensitive\"", await session.Client.GetStringAsync(SummaryPath(patient, ownNote), Cancel));
        using (var released = await ReleaseAsync(session.Client, patient, ownNote, sensitive: true))
        {
            var page = await ReadAsync(released);
            // Shown to the person who has just released it, tagged.
            Assert.Contains("data-summary-sensitive=\"own\"", page);
        }

        Assert.Equal([$"true false {clinician.Id}"], await MarkAsync(ownNote));

        // 3. Its note isn't sensitive: its page leaves the summary out until asked for, and records the asking.
        var hidden = await session.Client.GetStringAsync(NotePath(patient, ownNote), Cancel);
        Assert.Contains("Sensitive summaries hidden. 1 visit summary is not shown.", Words(hidden));
        Assert.DoesNotContain(TalkedAbout, hidden);
        Assert.DoesNotContain("data-write-summary", hidden);
        var viewsBefore = await apps.AuditEventsAsync(AuditActions.ReadSensitive, clinician.Id, patient);
        var shown = await session.Client.GetStringAsync(NotePath(patient, ownNote) + "?sensitive=true", Cancel);
        Assert.Contains(TalkedAbout, shown);
        Assert.Contains("Sensitive summaries shown. Your view of them is logged.", Words(shown));
        Assert.Equal(viewsBefore + 1, await apps.AuditEventsAsync(AuditActions.ReadSensitive, clinician.Id, patient));

        // 4. A summary of an ordinary note follows the note and its addenda afterwards.
        using (var released = await ReleaseAsync(session.Client, patient, later))
        {
            Assert.Contains("Released.", Words(await ReadAsync(released)));
        }

        Assert.Equal(["false false none"], await MarkAsync(later));
        await ChangeAsync(session.Client, patient, later, true, "Discussed a private matter.");
        Assert.Equal(["true true none"], await MarkAsync(later));
        // While the note is sensitive, the summary's unmarking isn't offered, and is refused if posted.
        var held = await session.Client.GetStringAsync(SensitivityPath(patient, later) + $"?summary={await StandingIdAsync(patient, later)}", Cancel);
        Assert.Contains("data-held-by-sources", held);
        await ChangeAsync(session.Client, patient, later, false, "Marked the wrong note.");
        Assert.Equal(["false false none"], await MarkAsync(later));
        using (var other = await AddAsync(session.Client, patient, later, "Synthetic ordinary addendum."))
        {
            Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        }

        var laterAddendum = Guid.Parse((await ProxyTests.QueryAsync(apps, $"SELECT id::text FROM ehr.note_addenda WHERE patient_id = @patient AND note_id = '{later}'", patient)).Single());
        using (var marked = await ChangeSensitivityAsync(session.Client, patient, later, mark: true, "Names a relative.", laterAddendum))
        {
            Assert.Contains("Saved.", Words(await ReadAsync(marked)));
        }

        Assert.Equal(["true true none"], await MarkAsync(later));
        using (var unmarked = await ChangeSensitivityAsync(session.Client, patient, later, mark: false, "Not about a relative.", laterAddendum))
        {
            Assert.Contains("Saved.", Words(await ReadAsync(unmarked)));
        }

        Assert.Equal(["false false none"], await MarkAsync(later));
        using (var signedSensitive = await AddAsync(session.Client, patient, later, "Synthetic sensitive addendum, later.", sensitive: true))
        {
            Assert.Equal(HttpStatusCode.OK, signedSensitive.StatusCode);
        }

        Assert.Equal(["true true none"], await MarkAsync(later));

        // 5. The summary marked on its own: only the person who marked it, or the note's signer, unmarks it.
        var colleague = await apps.CreateStaffAsync("Dr. Colleague", StaffRoles.Clinician);
        await apps.AssignAsync(colleague, patient);
        var colleagueSession = await SignInAsync(apps.OnPrem, colleague);
        var ownSummary = await StandingIdAsync(patient, ownNote);
        Assert.Contains("data-may-not-unmark", await colleagueSession.Client.GetStringAsync(SensitivityPath(patient, ownNote) + $"?summary={ownSummary}", Cancel));
        using (var refused = await colleagueSession.Client.PostAsync(SensitivityPath(patient, ownNote) + $"?summary={ownSummary}",
            Form(("__RequestVerificationToken", Token(await colleagueSession.Client.GetStringAsync(NotePath(patient, ownNote), Cancel))), ("Mark", false), ("Reason", "Let me.")), Cancel))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        Assert.Equal([$"true false {clinician.Id}"], await MarkAsync(ownNote));
        using (var unmarked = await ChangeSummarySensitivityAsync(session.Client, patient, ownNote, ownSummary, mark: false, "Reviewed with the patient."))
        {
            Assert.Contains("Saved.", Words(await ReadAsync(unmarked)));
        }

        Assert.Equal(["false false none"], await MarkAsync(ownNote));
        Assert.Matches(new Regex("data-summary-sensitivity-reason>Unmarked as sensitive \\d{4}-\\d{2}-\\d{2} \\d{2}:\\d{2} UTC: Reviewed with the patient\\."),
            await session.Client.GetStringAsync(NotePath(patient, ownNote), Cancel));
    }

    // Marks or unmarks the note itself, as its sensitivity page posts it.
    static async Task ChangeAsync(HttpClient client, Guid patient, Guid note, bool mark, string reason)
    {
        using var changed = await ChangeSensitivityAsync(client, patient, note, mark, reason);
        Assert.Contains("Saved.", Words(await ReadAsync(changed)));
    }

    // The standing summary of one of the patient's notes.
    async Task<Guid> StandingIdAsync(Guid patient, Guid note) =>
        Guid.Parse((await ProxyTests.QueryAsync(apps, $"SELECT id::text FROM ehr.visit_summaries WHERE patient_id = @patient AND note_id = '{note}' AND withdrawn_at IS NULL", patient)).Single());
}
