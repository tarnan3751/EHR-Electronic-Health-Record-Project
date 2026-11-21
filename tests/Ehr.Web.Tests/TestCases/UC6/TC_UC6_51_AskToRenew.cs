using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.RefillRequestTests;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-51
// Purpose / scenario: The patient asks the care team to renew a medication from the portal's home: the page offers the
//   active medications with a drug code, each with how it is taken, and not one that was stopped or has no code; the
//   request, with the patient's note, then waits for the care team, who each find it in their inbox, and the medication
//   is no longer offered while it waits. No email is sent for asking.
// Type: Functional (successful flow: refill requests)
// Requirements: proposed FR-58
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: The patient has a confirmed portal account and is signed in on the on-site app; Dr. Test and a colleague
//   have the patient on their panel.
// Inputs and system conditions: GET /portal, GET /portal/refills, POST /portal/refills, on the on-site app.
// Test data: Active, coded: lisinopril 10 MG Oral Tablet, "1 tablet by mouth daily". Stopped: atorvastatin 20 MG Oral
//   Tablet. With no code: "Albuterol inhaler". The note "Synthetic: I have three days left.".
// Steps:
//   1. Open the portal's home and follow "Renew a medication".
//   2. Choose lisinopril, type the note, and choose "Send request".
// Expected result: Step 1: the page sends Cache-Control: no-store, offers lisinopril with its dosage and nothing else,
//   and opening it is one recorded read of the patient by their own account. Step 2: the page says "Sent:", lists the
//   request as Waiting, asked by the patient, with the note, and offers no medication; the database holds one request,
//   waiting, asked by the patient's account; Dr. Test and the colleague each have an item "A patient asked to renew a
//   medication"; no email was sent to the patient.
// Creator: Tahseen Arnan
public class TC_UC6_51_AskToRenew(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_patient_asks_to_renew_an_active_coded_medication_and_the_care_team_is_told()
    {
        var (clinician, _, patient) = await PortalPatientAsync(apps, "Renewing");
        var colleague = await apps.CreateStaffAsync("Dr. Colleague", Ehr.Data.Accounts.StaffRoles.Clinician);
        await apps.AssignAsync(colleague, patient.Patient);
        await MedicationAsync(apps, patient.Patient);
        await MedicationAsync(apps, patient.Patient, "atorvastatin 20 MG Oral Tablet", "617310", status: "stopped");
        await MedicationAsync(apps, patient.Patient, "Albuterol inhaler", code: null);
        var emails = EmailsTo(apps, patient.Email).Count;

        var home = await patient.Client.GetStringAsync("/portal", Cancel);
        Assert.Contains("href=\"/portal/refills\"", home);
        Assert.Contains("Renew a medication", Words(home));
        var readsBefore = await apps.AuditEventsAsync(AuditActions.Read, patient.Account, patient.Patient);
        using (var page = await patient.Client.GetAsync(PortalPath, Cancel))
        {
            var html = await ReadAsync(page);
            Assert.True(page.Headers.CacheControl?.NoStore);
            Assert.Contains(Lisinopril, Words(html));
            Assert.Contains("1 tablet by mouth daily", Words(html));
            Assert.DoesNotContain("atorvastatin", html);
            Assert.DoesNotContain("Albuterol", html);
            Assert.Contains("No requests yet.", Words(html));
        }

        Assert.Equal(readsBefore + 1, await apps.AuditEventsAsync(AuditActions.Read, patient.Account, patient.Patient));

        using var sent = await AskAsync(patient.Client, Lisinopril, "Synthetic: I have three days left.");
        var words = Words(await ReadAsync(sent));

        Assert.Equal(PortalPath, PathOf(sent));
        Assert.Contains("Sent: the care team will answer your request here.", words);
        Assert.Contains("Waiting", words);
        Assert.Contains("by you", words);
        Assert.Contains("Synthetic: I have three days left.", words);
        Assert.Contains("None of your medications can be renewed here right now.", words);
        Assert.Equal([$"requested|{Lisinopril}|Synthetic: I have three days left.|{patient.Account}|-|-|-"], await RequestsAsync(apps, patient.Patient));
        var request = await RequestIdAsync(apps, patient.Patient);
        Assert.Equal(
            [.. new[] { clinician.Id, colleague.Id }.Select(id => id.ToString()).Order(StringComparer.Ordinal).Select(id => $"{id} refill-request A patient asked to renew a medication")],
            await ProxyTests.QueryAsync(apps,
                $"SELECT recipient_id || ' ' || kind || ' ' || summary FROM ehr.inbox_items WHERE source_table = 'refill_requests' AND source_id = '{request}' AND done_at IS NULL ORDER BY recipient_id::text COLLATE \"C\"",
                patient.Patient));
        Assert.Equal(emails, EmailsTo(apps, patient.Email).Count);
    }
}
