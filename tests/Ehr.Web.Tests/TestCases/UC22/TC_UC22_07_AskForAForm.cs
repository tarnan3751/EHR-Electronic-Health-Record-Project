using System.Net;
using Ehr.Data.Accounts;
using Ehr.Web.CloudAudit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.PreVisitFormTests;

namespace Ehr.Web.Tests.TestCases.UC22;

// Test Case ID: TC-UC22-07
// Purpose / scenario: A clinician with the patient on their panel asks for a pre-visit form before a booked
//   appointment, from the patient's pre-visit forms page. The same questionnaire can't be asked twice for one
//   appointment. A clinician without the patient on their panel, or off-site while the clinic's server can't be reached,
//   is offered nothing to ask with, and told why.
// Type: Functional (alternative flow: the clinician's side)
// Requirements: proposed FR-59
// Use case: UC 22, Answer a pre-visit form
// Preconditions: The clinician is signed in on the on-site app; the patient is on their panel, with an appointment
//   booked with them two days ahead.
// Inputs and system conditions: GET /patients/{id}/forms; POST /patients/{id}/forms?handler=Ask; the same GET on the
//   cloud app cut off from the clinic's server; the same GET as a clinician without the patient on their panel.
// Test data: Clinician "Dr. Forms Seven"; patient Synthetic Booked; the PHQ-9 (44249-1) and the GAD-7 (69737-5).
// Steps:
//   1. Open the patient's pre-visit forms page.
//   2. Choose the appointment and the PHQ-9, and choose "Ask for form".
//   3. Ask for the PHQ-9 again, then the GAD-7.
//   4. Open the page off-site with the clinic's server down, and as another clinician.
// Expected result: Step 1: the appointment list starts with the cursor in it, offers the booked appointment and both
//   questionnaires, and says forms aren't watched as they arrive; no form is listed and no count shown. Step 2: the
//   page says "Form asked for."; the request names the patient, the appointment's clinician, the clinician who asked
//   and a copy of the PHQ-9's definition. Step 3: the second PHQ-9 is answered 422 "Not asked. That questionnaire is
//   already asked for at that appointment." and nothing more is saved; the GAD-7 is saved. Step 4: off-site the page
//   says "Asking for a pre-visit form needs the clinic's server." with no form to post; the other clinician is sent to
//   the chart, which asks for a reason, before anything is read.
// Creator: Tahseen Arnan
public class TC_UC22_07_AskForAForm(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task A_clinician_asks_for_a_form_before_a_booked_appointment_once_for_each_questionnaire()
    {
        var visit = await VisitAsync(apps, "Dr. Forms Seven");
        var page = await visit.Session.Client.GetStringAsync(FormsPath(visit.Patient), Cancel);
        Assert.Matches("<select id=\"form-appointment\"[^>]*autofocus", page);
        Assert.Contains($"<option value=\"{visit.Appointment}\">", page);
        Assert.Contains("with Dr. Forms Seven</option>", page);
        Assert.Contains($"<option value=\"{Phq9}\">Patient Health Questionnaire-9 (PHQ-9)</option>", page);
        Assert.Contains($"<option value=\"{Gad7}\">GAD-7</option>", page);
        Assert.Contains("Forms aren't watched as they arrive", Words(page));
        Assert.DoesNotContain("data-sensitive", page);

        using var asked = await AskAsync(visit.Session.Client, visit.Patient, visit.Appointment, Phq9);
        Assert.Equal(HttpStatusCode.OK, asked.StatusCode);
        Assert.Contains("Form asked for. The patient can fill it in from 7 days before the appointment until it ends.", Words(await ReadAsync(asked)));
        Assert.Equal([$"{visit.Patient} {visit.Clinician.Id} {visit.Clinician.Id} true"], await QueryAsync(apps,
            """
            SELECT r.patient_id || ' ' || r.clinician_id || ' ' || r.requested_by || ' ' || (r.definition = q.definition)
            FROM ehr.questionnaire_requests r JOIN ehr.questionnaires q ON q.code = r.questionnaire_code WHERE r.appointment_id = @appointment
            """, ("appointment", visit.Appointment)));

        using var again = await AskAsync(visit.Session.Client, visit.Patient, visit.Appointment, Phq9);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        Assert.Contains("Not asked. That questionnaire is already asked for at that appointment.", Words(await again.Content.ReadAsStringAsync(Cancel)));
        using var anxiety = await AskAsync(visit.Session.Client, visit.Patient, visit.Appointment, Gad7);
        Assert.Equal(HttpStatusCode.OK, anxiety.StatusCode);
        Assert.Equal([Gad7, Phq9], await QueryAsync(apps,
            "SELECT questionnaire_code FROM ehr.questionnaire_requests WHERE appointment_id = @appointment ORDER BY questionnaire_code DESC", ("appointment", visit.Appointment)));

        // Off-site, with the clinic's server down: readable, nothing to post.
        Assert.False(await apps.CloudCutOff.Services.GetRequiredService<CloudAuditShipper>().ShipAsync(Cancel));
        var cutOff = await Client(apps.CloudCutOff, visit.Session.Jar).GetStringAsync(FormsPath(visit.Patient), Cancel);
        Assert.Contains("Asking for a pre-visit form needs the clinic's server.", cutOff);
        Assert.DoesNotContain("handler=Ask", cutOff);
        Assert.Contains("2 pre-visit forms are not shown.", Words(cutOff));

        // Another clinician: sent to the chart, which asks for a reason, before anything is read.
        var other = await SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("Dr. Forms Elsewhere", StaffRoles.Clinician));
        using var elsewhere = await other.Client.GetAsync(FormsPath(visit.Patient), Cancel);
        Assert.Equal($"/patients/{visit.Patient}/reason", PathOf(elsewhere));
    }
}
