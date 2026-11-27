using System.Net;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests.TestCases.UC12;

// Test Case ID: TC-UC12-02
// Purpose / scenario: Emergency access is never granted without a real reason: one of 10 to 500 characters once the
//   spaces around it are taken off. Each refused reason gets its own message, nothing is recorded, and the chart stays
//   closed; at the boundary, a reason exactly 10 characters long is accepted, and is recorded without the spaces.
// Type: Functional (invalid input)
// Requirements: FR-06
// Use case: UC 12, Open a chart through emergency access
// Preconditions: A clinician who has finished setup, and a patient off their panel with no emergency access given yet.
// Inputs and system conditions: On-site (the on-prem app). POST each reason to /patients/{id}/reason with the page's
//   form token; GET /patients/{id} after the refused ones.
// Test data: Clinician "Dr. Short Reason", patient "Vale Invalid". Reasons: empty; ten spaces; "ER"; "ER" with four
//   spaces before and eight after (14 characters, 2 without the spaces); 501 x's; then "  Ward round  " ("Ward
//   round" is exactly 10 characters).
// Steps:
//   1. Post each refused reason in turn.
//   2. Open the chart.
//   3. Post "  Ward round  ".
// Expected result: Step 1: empty and ten spaces show "Say why you need this chart, in 10 to 500 characters."; "ER", with
//   or without the spaces, shows "Too short. Use 10 to 500 characters."; 501 x's show "Too long. Use 10 to 500
//   characters."; each stays on the reason page and no break_glass event is recorded. Step 2 is sent to the reason
//   page again. Step 3 opens the chart, and the one break_glass event holds "Ward round".
// Creator: Luke Garcia
public class TC_UC12_02_InvalidReason(WebApps apps) : IClassFixture<WebApps>
{
    public static TheoryData<string, string> Refused => new()
    {
        { "", "Say why you need this chart, in 10 to 500 characters." },
        { new string(' ', 10), "Say why you need this chart, in 10 to 500 characters." },
        { "ER", "Too short. Use 10 to 500 characters." },
        { "    ER        ", "Too short. Use 10 to 500 characters." },
        { new string('x', 501), "Too long. Use 10 to 500 characters." },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task A_reason_too_short_or_too_long_is_refused_and_nothing_is_recorded(string reason, string message)
    {
        var clinician = await apps.ClinicianAsync();
        var patient = await apps.InsertPatientAsync("Vale", "Invalid");
        var session = await SignInAsync(apps.OnPrem, clinician);

        using var refused = await PostFormAsync(session.Client, $"/patients/{patient}/reason", $"/patients/{patient}/reason", ("Reason", reason));

        Assert.Contains(message, await ReadAsync(refused));
        Assert.Equal($"/patients/{patient}/reason", PathOf(refused));
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.BreakGlass, patientId: patient));
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.Read, patientId: patient));

        using var chart = await Client(apps.OnPrem, session.Jar, followRedirects: false).GetAsync($"/patients/{patient}", Cancel);
        Assert.Equal(HttpStatusCode.Redirect, chart.StatusCode);
        Assert.Equal($"/patients/{patient}/reason", new Uri(Base, chart.Headers.Location!).AbsolutePath);
    }

    [Fact]
    public async Task A_reason_of_exactly_ten_characters_is_accepted_and_recorded_without_its_spaces()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Short Reason", StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Vale", "Boundary");
        var session = await SignInAsync(apps.OnPrem, clinician);

        using var opened = await PostFormAsync(session.Client, $"/patients/{patient}/reason", $"/patients/{patient}/reason", ("Reason", "  Ward round  "));

        Assert.Contains("Boundary, Vale", await ReadAsync(opened));
        Assert.Equal($"/patients/{patient}", PathOf(opened));
        Assert.Equal(["Ward round"], await apps.ReasonsAsync(AuditActions.BreakGlass, clinician.Id, patient));
    }
}
