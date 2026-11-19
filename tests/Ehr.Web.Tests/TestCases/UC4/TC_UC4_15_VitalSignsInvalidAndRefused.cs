using System.Net;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.VitalTests;

namespace Ehr.Web.Tests.TestCases.UC4;

// Test Case ID: TC-UC4-15
// Purpose / scenario: Vital signs that can't be right are not saved and the form says why; a clinician without the
//   patient on their panel reads the flowsheet only under emergency access and never records.
// Type: Functional (invalid input; alternative flow)
// Requirements: FR-08, FR-02, proposed FR-48
// Use case: UC 4, View patient chart
// Preconditions: A clinician is signed in on the on-site app with the patient on their panel. A second clinician is
//   signed in without the patient on their panel and with no emergency access.
// Inputs and system conditions: POST /patients/{id}/vitals; GET and POST as the second clinician.
// Test data: Systolic 148 with no diastolic; heart rate "fast"; temperature 60; nothing at all; a time tomorrow.
// Steps:
//   1. As the first clinician, record each of the five in turn.
//   2. As the second clinician, open the patient's vital signs, and post a set to it.
// Expected result: Step 1: each is answered 422 with "Not saved:" and, in turn, "Enter both numbers of the blood
//   pressure.", "Enter a number, like 36.8.", "Enter a value above 0 and no more than 45 °C.", "Enter at least one
//   vital sign." and "The time can't be in the future."; what was typed is still in the form; nothing is saved. Step
//   2: both are sent back to the chart, and nothing is saved.
// Creator: Tahseen Arnan
public class TC_UC4_15_VitalSignsInvalidAndRefused(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Unsound_vital_signs_are_not_saved_and_a_clinician_off_the_panel_records_none()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        var tomorrow = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-ddTHH:mm");
        var now = DateTime.UtcNow.AddMinutes(-5).ToString("yyyy-MM-ddTHH:mm");

        foreach (var (values, at, message) in new ((string, string)[] Values, string At, string Message)[]
        {
            ([(Systolic, "148")], now, "Enter both numbers of the blood pressure."),
            ([(HeartRate, "fast")], now, "Enter a number, like 36.8."),
            ([(Temperature, "60")], now, "Enter a value above 0 and no more than 45 °C."),
            ([], now, "Enter at least one vital sign."),
            ([(HeartRate, "72")], tomorrow, "The time can't be in the future."),
        })
        {
            using var refused = await RecordAtAsync(session.Client, patient, at, values);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            var page = await refused.Content.ReadAsStringAsync(Cancel);
            Assert.Contains("Not saved:", page);
            Assert.Contains(message, Words(page));
            foreach (var (_, typed) in values)
            {
                Assert.Contains($"value=\"{typed}\"", page);
            }
        }

        var outsider = await SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("Dr. Elsewhere", Ehr.Data.Accounts.StaffRoles.Clinician));
        var noRedirects = Client(apps.OnPrem, outsider.Jar, followRedirects: false);
        using var read = await noRedirects.GetAsync(Path(patient), Cancel);
        Assert.Equal(HttpStatusCode.Redirect, read.StatusCode);
        Assert.Equal($"/patients/{patient}", read.Headers.Location?.OriginalString);
        // Their own token, from a page they can open: the refusal is the panel's, not the forgery check's.
        var token = Token(await outsider.Client.GetStringAsync("/patients", Cancel));
        using var posted = await noRedirects.PostAsync(Path(patient), Form(("__RequestVerificationToken", token), ($"Values[{HeartRate}]", "72"), ("TakenAt", now)), Cancel);
        Assert.Equal(HttpStatusCode.Redirect, posted.StatusCode);
        Assert.Equal($"/patients/{patient}", posted.Headers.Location?.OriginalString);

        Assert.Empty(await RecordedAsync(apps, patient));
    }
}
