using System.Net;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// Emergency access (Areas/Clinical/Pages/Patients/Reason, BreakGlass): a chart off the clinician's panel asks for a
// reason, records it, and stays open to them afterwards; a chart on their panel never asks.
public class BreakGlassTests(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

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
        Assert.Contains("Use at least 10 characters.", await ReadAsync(tooShort));
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.BreakGlass, actorId: clinician.Id, patientId: patient));

        using var opened = await PostFormAsync(session.Client, $"/patients/{patient}/reason", $"/patients/{patient}/reason", ("Reason", "Covering the night shift"));
        var html = await ReadAsync(opened);
        Assert.Equal($"/patients/{patient}", PathOf(opened));
        Assert.Contains("Emergency, Indy", html);
        Assert.Contains("Emergency access", html);

        // Open to them now without asking again, and every step is in the log: the grant with its reason, the trigger's
        // record of the grant row, and the two chart views.
        using var again = await Client(apps.OnPrem, session.Jar, followRedirects: false).GetAsync($"/patients/{patient}", Cancel);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.BreakGlass, actorId: clinician.Id, patientId: patient));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.Insert, actorId: clinician.Id, patientId: patient));
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
}
