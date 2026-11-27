using System.Net;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Web.CloudAudit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// Emergency access (Areas/Clinical/Pages/Patients/Reason, BreakGlass): a chart off the clinician's panel asks for a
// reason, records it, and stays open to them afterwards; a chart on their panel never asks. Off-site, while the clinic's
// server can't be reached, nothing can be recorded, so emergency access isn't offered.
public class BreakGlassTests(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task A_chart_off_the_panel_asks_for_a_reason_records_it_and_stays_open()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Covering", StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Indy", "Emergency");
        var session = await SignInAsync(apps.OnPrem, clinician);

        using var chart = await Client(apps.OnPrem, session.Jar, followRedirects: false).GetAsync($"/patients/{patient}", Cancel);
        Assert.Equal(HttpStatusCode.Redirect, chart.StatusCode);
        Assert.Equal($"/patients/{patient}/reason", new Uri(Base, chart.Headers.Location!).AbsolutePath);
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.Read, actorId: clinician.Id, patientId: patient));

        using var tooShort = await PostFormAsync(session.Client, $"/patients/{patient}/reason", $"/patients/{patient}/reason", ("Reason", "ER"));
        Assert.Contains("Too short. Use 10 to 500 characters.", await ReadAsync(tooShort));
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.BreakGlass, actorId: clinician.Id, patientId: patient));

        using var opened = await PostFormAsync(session.Client, $"/patients/{patient}/reason", $"/patients/{patient}/reason", ("Reason", "Covering the night shift"));
        var html = await ReadAsync(opened);
        Assert.Equal($"/patients/{patient}", PathOf(opened));
        Assert.Contains("Emergency, Indy", html);
        Assert.Contains("Emergency access", html);

        // Open to them now without asking again, and every step is in the log: the grant with its reason, the trigger's
        // record of the grant row, and the two chart views. (Since step 18 the grant also writes each administrator's
        // inbox item to review it, which TC-UC12-04 counts.)
        using var again = await Client(apps.OnPrem, session.Jar, followRedirects: false).GetAsync($"/patients/{patient}", Cancel);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.BreakGlass, actorId: clinician.Id, patientId: patient));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.Insert, actorId: clinician.Id, patientId: patient, table: "break_glass_accesses"));
        Assert.Equal(2, await apps.AuditEventsAsync(AuditActions.Read, actorId: clinician.Id, patientId: patient));
        Assert.Equal(["Covering the night shift"], await apps.ReasonsAsync(AuditActions.BreakGlass, clinician.Id, patient));
    }

    [Fact]
    public async Task A_chart_on_the_panel_opens_without_a_reason()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Own", StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Jules", "Paneled");
        await apps.AssignAsync(clinician, patient);
        var session = await SignInAsync(apps.OnPrem, clinician);

        using var chart = await Client(apps.OnPrem, session.Jar, followRedirects: false).GetAsync($"/patients/{patient}", Cancel);

        Assert.Equal(HttpStatusCode.OK, chart.StatusCode);
        Assert.DoesNotContain("Emergency access", await chart.Content.ReadAsStringAsync(Cancel));
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.BreakGlass, actorId: clinician.Id, patientId: patient));
    }

    [Fact]
    public async Task A_reason_for_a_patient_that_does_not_exist_is_not_found()
    {
        var session = await SignInAsync(apps.OnPrem, await apps.ClinicianAsync());
        var nobody = Guid.CreateVersion7();

        using var response = await PostFormAsync(session.Client, $"/patients/{nobody}/reason", $"/patients/{nobody}/reason", ("Reason", "Looking for a record"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task With_the_on_prem_app_unreachable_emergency_access_is_not_offered()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Away", StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Kit", "Elsewhere");
        var session = await SignInAsync(apps.OnPrem, clinician);
        // The cloud app finds out within a couple of seconds; this is that check, now.
        Assert.False(await apps.CloudCutOff.Services.GetRequiredService<CloudAuditShipper>().ShipAsync(Cancel));

        // The chart sends them here, as on-site.
        using var response = await Client(apps.CloudCutOff, session.Jar).GetAsync($"/patients/{patient}", Cancel);

        var html = await ReadAsync(response);
        Assert.Equal($"/patients/{patient}/reason", PathOf(response));
        Assert.Contains("Emergency access can't be granted until the clinic's server is back", html);
        Assert.DoesNotContain("<textarea", html);
        Assert.DoesNotContain("Open the chart", html);
    }
}
