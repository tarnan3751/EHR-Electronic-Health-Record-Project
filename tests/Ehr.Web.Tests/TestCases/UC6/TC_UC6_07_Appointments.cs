using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.AppointmentTests;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-07
// Purpose / scenario: A patient sees their own appointments in the portal: the next ones on the home page, and all of
//   them, upcoming and earlier, on the appointments page, with nobody else's. There is no way to book or change one.
// Type: Functional (successful flow)
// Requirements: FR-17
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: Two patients have portal accounts. A clinician has booked two appointments for the first (one then
//   cancelled) and one for the second.
// Inputs and system conditions: GET /portal and GET /portal/appointments on the on-site app as the first patient,
//   then on the cloud app whose link to the on-site server is down.
// Test data: Clinician "Dr. Portal Schedule". Appointments 35 days ahead: 09:00 "Synthetic: annual physical" and
//   11:00 "Synthetic: cancelled visit" (cancelled) for the first patient; 10:00 "Synthetic: someone else's" for the
//   second.
// Steps:
//   1. As the first patient, open the portal's home page.
//   2. Open "Appointments".
//   3. Open the same page off-site with the clinic's server unreachable.
// Expected result: Step 1: "Your next appointments" lists the 09:00 appointment with its date written out, "09:00
//   UTC, for 20 minutes, with Dr. Portal Schedule" and its reason, and a link to all appointments; the placeholder is
//   gone. Step 2: "Coming up" lists the 09:00 appointment; "Earlier" lists the
//   cancelled one with a "Cancelled" tag; the other patient's appointment appears nowhere; the page has no form and
//   says to contact the clinic; it is one recorded read of the patient. Step 3: the same list, read from the cloud copy.
// Creator: Tahseen Arnan
public class TC_UC6_07_Appointments(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task A_patient_sees_their_own_appointments_and_nobody_elses()
    {
        var (clinician, patient, session) = await ClinicianAsync(apps, "Dr. Portal Schedule");
        var other = await apps.InsertPatientAsync("Synthetic", "Someone else");
        await apps.AssignAsync(clinician, other);
        using var a = await BookAsync(session.Client, patient, clinician.Id, Day(5), "09:00", "Synthetic: annual physical");
        using var b = await BookAsync(session.Client, patient, clinician.Id, Day(5), "11:00", "Synthetic: cancelled visit");
        using var c = await BookAsync(session.Client, other, clinician.Id, Day(5), "10:00", "Synthetic: someone else's");
        var second = Guid.Parse((await RowsAsync(apps, patient, "id::text")).Last());
        using var cancelled = await ChangeAsync(session.Client, second, "Cancel", ("CancelReason", "Synthetic"));

        var portal = await PortalResultsTests.SignInPatientAsync(apps, apps.OnPrem, patient);
        var home = await portal.Client.GetStringAsync("/portal", Cancel);
        Assert.Contains("Your next appointments", home);
        Assert.Contains("09:00 UTC, for 20 minutes, with Dr. Portal Schedule", home);
        Assert.Contains("Synthetic: annual physical", home);
        Assert.DoesNotContain("Synthetic: cancelled visit", home);
        Assert.Contains("href=\"/portal/appointments\">All your appointments</a>", home);
        Assert.DoesNotContain("will appear here.", home);
        Assert.DoesNotContain("Appointments and messages will appear here.", home);

        var account = await apps.AuditEventsAsync(AuditActions.Read, patientId: patient);
        var page = await portal.Client.GetStringAsync("/portal/appointments", Cancel);
        Assert.True(page.IndexOf("Coming up", StringComparison.Ordinal) < page.IndexOf("Synthetic: annual physical", StringComparison.Ordinal));
        Assert.True(page.IndexOf("Earlier", StringComparison.Ordinal) < page.IndexOf("Synthetic: cancelled visit", StringComparison.Ordinal));
        Assert.Contains("<span class=\"sc-tag\">Cancelled</span>", page);
        Assert.DoesNotContain("someone else", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<form method=\"post\" action=\"/portal/appointments", page);
        Assert.Contains("To book, change or cancel an appointment, contact the clinic.", page);
        Assert.Equal(account + 1, await apps.AuditEventsAsync(AuditActions.Read, patientId: patient));

        var offSite = await Client(apps.CloudCutOff, portal.Jar).GetStringAsync("/portal/appointments", Cancel);
        Assert.Contains("Synthetic: annual physical", offSite);
        Assert.DoesNotContain("someone else", offSite, StringComparison.OrdinalIgnoreCase);
    }
}
