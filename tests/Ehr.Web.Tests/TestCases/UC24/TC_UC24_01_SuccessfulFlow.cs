using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.EmergencyAccessFlows;

namespace Ehr.Web.Tests.TestCases.UC24;

// Test Case ID: TC-UC24-01
// Purpose / scenario: An emergency access reaches every administrator's inbox; once it has ended, one of them opens it
//   from the inbox, sees who used it, the patient by ID, when, the reason given and what was done under it, with
//   nothing from the record, and marks it reviewed as appropriate, with a note. Every administrator's item is then done,
//   and the audit page says the access was reviewed (the expansion plan's step 18).
// Type: Functional (successful flow)
// Requirements: proposed FR-64, FR-06, FR-32
// Use case: UC 24, Review an emergency access
// Preconditions: Dr. Covering, a clinician, and two administrators, Reviewer One and Reviewer Two, besides the
//   bootstrap admin, on the on-site app. The patient is on nobody's panel.
// Inputs and system conditions: the reason page; "Add to my patients" and a message written to the patient; the access
//   ended; GET /inbox and /admin/emergency-access/{id} as Reviewer One; POST it with "Appropriate" and a note.
// Test data: Patient "Synthetic Reviewedchart". Reason "Synthetic: covering the ward tonight, asked by the charge
//   nurse." Message "Synthetic words of a message that the review never shows." Note "Synthetic: checked the rota, the
//   clinician was covering the ward."
// Steps:
//   1. As Dr. Covering, open the chart through emergency access; add the patient to the panel; write a message.
//   2. As Reviewer One, open the inbox.
//   3. End the access; select "Review the access".
//   4. Choose "Appropriate", write the note, and select "Mark reviewed".
//   5. Open the audit page's emergency accesses for the patient.
// Expected result: Step 2: one item, "Emergency access to review", the patient by ID and no name, "Review the access",
//   and no "Mark done". Step 3: the page names Dr. Covering, the patient's ID, the reason, and the events (break_glass,
//   reads, the inserts of the panel row and the message) with no name and no word of the message. Step 4: "Reviewed:",
//   the outcome, Reviewer One and the note; every administrator's item, the bootstrap admin's included, is done. Step 5:
//   the access is tagged "Reviewed", not "Review", untinted, with "Open the review".
// Creator: Tahseen Arnan
public class TC_UC24_01_SuccessfulFlow(WebApps apps) : IClassFixture<WebApps>
{
    const string Message = "Synthetic words of a message that the review never shows.";

    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_ended_emergency_access_is_reviewed_from_the_inbox_and_done_for_every_administrator()
    {
        var one = await apps.CreateStaffAsync("Reviewer One", StaffRoles.Admin);
        var two = await apps.CreateStaffAsync("Reviewer Two", StaffRoles.Admin);

        // 1.
        var access = await GrantAsync(apps, "Dr. Covering");
        using (var assigned = await PostFormAsync(access.Session.Client, $"/patients/{access.Patient}?handler=Assign", $"/patients/{access.Patient}"))
        {
            Assert.Contains("Remove from my patients", await ReadAsync(assigned));
        }

        using (var sent = await PostFormAsync(access.Session.Client, $"/patients/{access.Patient}/messages/new", $"/patients/{access.Patient}/messages/new",
            ("Subject", "Synthetic subject"), ("Body", Message)))
        {
            Assert.Contains(Message, await ReadAsync(sent));
        }

        // 2.
        var reviewer = await SignInAsync(apps.OnPrem, one);
        var inbox = await reviewer.Client.GetStringAsync("/inbox", Cancel);
        Assert.Contains("<span class=\"sc-tag\">Emergency access to review</span> A clinician used emergency access to a chart", inbox);
        Assert.Contains($"<td class=\"sc-table__id\">{access.Patient}</td>", inbox);
        Assert.Contains($"<a data-inbox-source href=\"/admin/emergency-access/{access.Id}\">Review the access</a>", inbox);
        Assert.DoesNotContain("Mark done", inbox);
        Assert.DoesNotContain("Reviewedchart", inbox);

        // 3.
        await EndAsync(apps, access.Id);
        var page = await reviewer.Client.GetStringAsync($"/admin/emergency-access/{access.Id}", Cancel);
        Assert.Contains("Dr. Covering", page);
        Assert.Contains($"<dd class=\"sc-table__id\">{access.Patient}</dd>", page);
        Assert.Contains("Synthetic: covering the ward tonight, asked by the charge nurse.", page);
        Assert.Contains($"<td>{AuditActions.BreakGlass}</td>", page);
        Assert.Contains($"<td>{AuditActions.Read}</td>", page);
        Assert.Contains("patient_assignments", page);
        Assert.Contains("messages", page);
        Assert.DoesNotContain("Reviewedchart", page);
        Assert.DoesNotContain("Synthetic words of a message", page);
        Assert.DoesNotContain("Synthetic subject", page);
        Assert.Contains("data-review-form", page);

        // 4.
        using (var reviewed = await ReviewAsync(reviewer.Client, access.Id, EmergencyAccessOutcomes.Appropriate, Note))
        {
            var after = await ReadAsync(reviewed);
            Assert.Equal($"/admin/emergency-access/{access.Id}", PathOf(reviewed));
            Assert.Contains("Reviewed:</span> the access is marked reviewed", after);
            Assert.Contains("<span class=\"sc-tag sc-tag--success\">Appropriate</span> Reviewed by Reviewer One", after);
            Assert.Contains(Note, after);
            Assert.DoesNotContain("data-review-form", after);
        }

        Assert.Equal([$"{one.Id} appropriate {Note}"], await ReviewsAsync(apps, access.Id));
        var items = await ItemsAsync(apps, access.Id);
        Assert.True(items[one.Id]);
        Assert.True(items[two.Id]);
        Assert.Equal(3, items.Count);
        Assert.All(items.Values, Assert.True);

        // 5.
        var audit = await reviewer.Client.GetStringAsync($"/admin/audit?Patient={access.Patient}&Flagged=true", Cancel);
        Assert.Contains($"{AuditActions.BreakGlass} <span class=\"sc-tag\">Reviewed</span>", audit);
        Assert.DoesNotContain("sc-tag sc-tag--danger\">Review</span>", audit);
        Assert.DoesNotContain("sc-table__flagged", audit);
        Assert.Contains($"<a data-review-link href=\"/admin/emergency-access/{access.Id}\">Open the review</a>", audit);
    }
}
