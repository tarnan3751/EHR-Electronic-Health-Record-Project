using Ehr.Data.Audit;
using Ehr.Data.Patients;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests.TestCases.UC4;

// Test Case ID: TC-UC4-01
// Purpose / scenario: Open an assigned patient's chart. A clinician finds a patient on their panel and opens the
//   chart, which shows the patient's identity, allergies, active medications, diagnoses and latest visit, and the
//   opening is one recorded read.
// Type: Functional (successful flow)
// Requirements: FR-02, FR-30, FR-32
// Use case: UC 4, View a patient's chart
// Preconditions: The clinician is signed in on the on-site app, with password and authenticator code, and the patient
//   is on their panel. The clinic's server is up.
// Inputs and system conditions: GET /patients?name={family}&birthdate=1999-01-01&mrn={mrn}, then GET /patients/{id},
//   on the on-site app (ehr_app role).
// Test data: Synthetic patient John {family}, born 1999-01-01, with an allergy to Penicillin (high risk, reaction
//   "Hives"), Lisinopril 10 mg tablet (1 tablet by mouth daily), Essential (primary) hypertension (I10, onset
//   2019-04-01), and a visit three days ago for "Annual physical" (blood pressure 128/82 mmHg, heart rate 72/min).
//   {family} is "Doe" and eight random hex digits.
// Steps:
//   1. Insert the patient and their chart, and assign them to the clinician.
//   2. Sign in as the clinician and search for the patient: the results link to their chart.
//   3. Count the clinician's recorded reads of the patient.
//   4. Open the chart.
//   5. Count the reads again.
// Expected result: The chart shows "{family}, John", "Penicillin: Hives" with "High risk", the medication and its
//   dosage, the diagnosis with "(I10)", and the visit with its vitals. Step 5's count is step 3's plus one.
// Creator: Diego Valenzuela
public class TC_UC4_01_SuccessfulFlow(WebApps apps)
    : IClassFixture<WebApps>
{
    [Fact]
    public async Task Authorized_clinician_can_view_patient_chart()
    {
        // Arrange: Create a synthetic patient.
        var family = "Doe" + Guid.NewGuid().ToString("N")[..8];

        var patient = await apps.InsertPatientAsync(
            "John",
            family,
            new DateOnly(1999, 1, 1));

        var mrn = WebApps.MrnOf(patient);

        // Create and authenticate an authorized clinician.
        var clinician = await apps.ClinicianAsync();

        // Assign the patient to the clinician.
        await apps.AssignAsync(clinician, patient);

        // Populate the patient's chart with clinical data.
        await apps.InsertAllergyAsync(
            patient,
            "Penicillin",
            criticality: AllergyCriticality.High,
            reaction: "Hives");

        await apps.InsertMedicationAsync(
            patient,
            "Lisinopril 10 mg tablet",
            dosage: "1 tablet by mouth daily");

        await apps.InsertConditionAsync(
            patient,
            "Essential (primary) hypertension",
            code: "I10",
            onset: new DateOnly(2019, 4, 1));

        var visitDate = DateTimeOffset.UtcNow.AddDays(-3);

        await apps.InsertVisitAsync(
            patient,
            visitDate,
            reason: "Annual physical",
            systolic: 128,
            diastolic: 82,
            heartRate: 72);

        var session = await SignInAsync(
            apps.OnPrem,
            clinician);

        // Act: Search for the patient using identifying info.
        var searchHtml = await session.Client.GetStringAsync(
            $"/patients?name={Uri.EscapeDataString(family)}" +
            $"&birthdate=1999-01-01&mrn={mrn}",
            Cancel);

        // Verify the correct patient appears in search results.
        Assert.Contains($"/patients/{patient}", searchHtml);

        // Record the existing patient-specific audit count.
        var readsBefore = await apps.AuditEventsAsync(
            AuditActions.Read,
            clinician.Id,
            patient);

        // Act: Open the patient's chart.
        var chartHtml = await session.Client.GetStringAsync(
            $"/patients/{patient}",
            Cancel);

        // Assert: Correct patient identity is displayed.
        Assert.Contains($"{family}, John", chartHtml);

        // Verify allergy information.
        Assert.Contains(
            "<strong>Penicillin</strong>: Hives",
            chartHtml);

        Assert.Contains("High risk", chartHtml);

        // Verify active medication information.
        Assert.Contains(
            "<strong>Lisinopril 10 mg tablet</strong>: " +
            "1 tablet by mouth daily",
            chartHtml);

        // Verify diagnosis information.
        Assert.Contains(
            "Essential (primary) hypertension",
            chartHtml);

        Assert.Contains("(I10)", chartHtml);

        // Verify recent visit information.
        Assert.Contains("Annual physical", chartHtml);

        Assert.Matches(
            @"<td>Annual physical</td>\s*<td>128/82 mmHg</td>\s*<td>72/min</td>",
            chartHtml);

        // Verify chart access generates exactly one audit event.
        var readsAfter = await apps.AuditEventsAsync(
            AuditActions.Read,
            clinician.Id,
            patient);

        Assert.Equal(readsBefore + 1, readsAfter);
    }
}
