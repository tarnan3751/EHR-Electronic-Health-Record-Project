using System.Net;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests.TestCases.UC4;

// Test Case ID: TC-UC4-04
// Purpose / scenario: Emergency access needs a real reason and is recorded. A reason too short to be one is refused
//   and grants nothing; a real reason opens the chart, and the grant, its reason and every view of the chart are
//   recorded.
// Type: Functional (invalid input)
// Requirements: FR-02, FR-06, FR-32
// Use case: UC 4, View a patient's chart
// Preconditions: A clinician is signed in on the on-site app, with password and authenticator code. The patient is
//   not on their panel, and they have no emergency access to the patient. The clinic's server is up.
// Inputs and system conditions: On the on-site app (ehr_app role): GET /patients/{id} without following redirects;
//   POST /patients/{id}/reason with Reason "ER", then with the real reason; GET /patients/{id} again.
// Test data: Synthetic patient John {family}, born 1999-01-01, where {family} is "Emergency" and eight random hex
//   digits; clinician "Dr. Emergency". Reasons "ER" (too short) and "Patient requires immediate medical attention".
// Steps:
//   1. Open the chart: the answer redirects to the reason page, and no read is recorded.
//   2. Submit the reason "ER".
//   3. Submit the real reason.
//   4. Read the emergency access events and their reasons.
//   5. Open the chart again.
// Expected result: Step 1 is a 302 to /patients/{id}/reason, with no recorded read. Step 2 says "Reason must be 10
//   to 500 characters." and records no emergency access. Step 3 lands on /patients/{id}, which shows "{family}, John"
//   and "Break-the-glass access until". Step 4 finds exactly one emergency access event, with the real reason. Step 5 is a 200, and
//   the clinician then has two recorded reads of the patient.
// Creator: Diego Valenzuela
public class TC_UC4_04_InvalidInput(WebApps apps)
    : IClassFixture<WebApps>
{
    [Fact]
    public async Task Emergency_access_requires_reason_and_is_audited()
    {
        // Arrange: Create a synthetic patient.
        var family = "Emergency" +
            Guid.NewGuid().ToString("N")[..8];

        var patient = await apps.InsertPatientAsync(
            "John",
            family,
            new DateOnly(1999, 1, 1));

        // Create a clinician without normal chart access.
        var clinician = await apps.CreateStaffAsync(
            "Dr. Emergency",
            StaffRoles.Clinician);

        // Do not assign this patient to the clinician.
        var session = await SignInAsync(
            apps.OnPrem,
            clinician);

        const string reason =
            "Patient requires immediate medical attention";

        // Step 1: Attempt to open the restricted chart.
        using var restricted = await Client(
            apps.OnPrem,
            session.Jar,
            followRedirects: false)
            .GetAsync($"/patients/{patient}", Cancel);

        // Assert: Normal access requires justification.
        Assert.Equal(
            HttpStatusCode.Redirect,
            restricted.StatusCode);

        Assert.Equal(
            $"/patients/{patient}/reason",
            new Uri(Base, restricted.Headers.Location!)
                .AbsolutePath);

        // Verify no chart read occurred yet.
        Assert.Equal(
            0,
            await apps.AuditEventsAsync(
                AuditActions.Read,
                actorId: clinician.Id,
                patientId: patient));

        // Step 2: Submit an insufficient justification.
        using var invalid = await PostFormAsync(
            session.Client,
            $"/patients/{patient}/reason",
            $"/patients/{patient}/reason",
            ("Reason", "ER"));

        Assert.Contains(
            "Reason must be 10 to 500 characters.",
            await ReadAsync(invalid));

        // No emergency access should be granted.
        Assert.Equal(
            0,
            await apps.AuditEventsAsync(
                AuditActions.BreakGlass,
                actorId: clinician.Id,
                patientId: patient));

        // Step 3: Submit the valid emergency reason.
        using var granted = await PostFormAsync(
            session.Client,
            $"/patients/{patient}/reason",
            $"/patients/{patient}/reason",
            ("Reason", reason));

        var chartHtml = await ReadAsync(granted);

        // Assert: The patient chart opens successfully.
        Assert.Equal(
            $"/patients/{patient}",
            PathOf(granted));

        Assert.Contains(
            $"{family}, John",
            chartHtml);

        Assert.Contains(
            "Break-the-glass access until",
            chartHtml);

        // Step 4: Verify emergency access is audited.
        Assert.Equal(
            1,
            await apps.AuditEventsAsync(
                AuditActions.BreakGlass,
                actorId: clinician.Id,
                patientId: patient));

        // Verify the exact justification was recorded.
        Assert.Equal(
            [reason],
            await apps.ReasonsAsync(
                AuditActions.BreakGlass,
                clinician.Id,
                patient));

        // Step 5: Verify subsequent chart access works.
        using var again = await Client(
            apps.OnPrem,
            session.Jar,
            followRedirects: false)
            .GetAsync($"/patients/{patient}", Cancel);

        Assert.Equal(
            HttpStatusCode.OK,
            again.StatusCode);

        // Verify both chart views were audited.
        Assert.Equal(
            2,
            await apps.AuditEventsAsync(
                AuditActions.Read,
                actorId: clinician.Id,
                patientId: patient));
    }
}
