using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.RefillRequestTests;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-52
// Purpose / scenario: The patient sees what became of their requests: one approved, with what the care team ordered,
//   the same product as asked; one declined, with the care team's message, line breaks kept. The page
//   never names the clinician who answered. The patient's own address gets one email that there is something new in the
//   portal, naming nothing, with a link to the portal's home; the second answer that day sends none, and someone the
//   patient shares their record with, who asked, gets none.
// Type: Functional (successful flow: answers)
// Requirements: proposed FR-58
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: The patient has a portal account, signed in, and has given access with messages to someone, signed in
//   and looking at the patient's record. Dr. Test has the patient on their panel.
// Inputs and system conditions: the proxy asks for lisinopril, the patient for amlodipine; Dr. Test approves the
//   lisinopril request and declines amlodipine's; then GET /portal/refills as the patient.
// Test data: the message "Synthetic: we need to check your blood pressure first.\nPlease book a visit.".
// Steps:
//   1. The proxy and the patient each ask.
//   2. Dr. Test approves the first, and declines the second with the message.
//   3. The patient opens the page.
// Expected result: The page lists amlodipine "Declined" with the message as written, and lisinopril "Approved", "your
//   care team ordered lisinopril 10 MG Oral Tablet"; "Dr. Test" is nowhere on it. The patient's address has one new
//   email, "There is something new in your patient portal", with no medication in it and a button to /portal; the
//   proxy's address has none.
// Creator: Tahseen Arnan
public class TC_UC6_52_RefillAnswered(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_patient_sees_each_answer_and_is_told_by_one_email_that_names_nothing()
    {
        var (_, session, patient) = await PortalPatientAsync(apps, "Answered");
        var (proxy, _) = await ProxyForAsync(apps, patient, "AnsweredProxy", sensitive: false, scope: "record-and-messages");
        await MedicationAsync(apps, patient.Patient);
        await MedicationAsync(apps, patient.Patient, "amlodipine 5 MG Oral Tablet", "197361");
        using (var asked = await AskAsync(proxy.Client, Lisinopril))
        {
            Assert.Equal(PortalPath, PathOf(asked));
        }

        var lisinopril = await RequestIdAsync(apps, patient.Patient);
        using (var asked = await AskAsync(patient.Client, "amlodipine 5 MG Oral Tablet"))
        {
            Assert.Equal(PortalPath, PathOf(asked));
        }

        var amlodipine = await RequestIdAsync(apps, patient.Patient);
        var (patientEmails, proxyEmails) = (EmailsTo(apps, patient.Email).Count, EmailsTo(apps, proxy.Email).Count);

        using (var approved = await ApproveAsync(session.Client, patient.Patient, lisinopril))
        {
            Assert.Contains("Approved:", Words(await ReadAsync(approved)));
        }

        using (var declined = await DeclineAsync(session.Client, patient.Patient, amlodipine, "Synthetic: we need to check your blood pressure first.\nPlease book a visit."))
        {
            Assert.Contains("Declined:", Words(await ReadAsync(declined)));
        }

        var words = Words(await patient.Client.GetStringAsync(PortalPath, Cancel));
        Assert.Contains("Approved", words);
        Assert.Contains("your care team ordered lisinopril 10 MG Oral Tablet.", words);
        Assert.Contains("Declined", words);
        Assert.Contains("Your care team says:", words);
        Assert.Contains("Synthetic: we need to check your blood pressure first.\nPlease book a visit.", words);
        Assert.DoesNotContain("Dr. Test", words);

        var sent = EmailsTo(apps, patient.Email).Skip(patientEmails).ToList();
        var email = Assert.Single(sent);
        Assert.Equal("There is something new in your patient portal", email.Subject);
        foreach (var detail in new[] { "lisinopril", "amlodipine", "refill", "medication", "blood pressure", "Dr. Test" })
        {
            Assert.DoesNotContain(detail, email.Subject + email.Body, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal("/portal", new Uri(apps.Emails.Actions[email.Body].Link).AbsolutePath);
        Assert.Equal(proxyEmails, EmailsTo(apps, proxy.Email).Count);
    }
}
