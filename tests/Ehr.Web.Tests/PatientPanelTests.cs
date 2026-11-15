using System.Net;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Web.CloudAudit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// A clinician's panel (Areas/Clinical/Pages/Patients, PatientAssignments): the clinic's home shows their own patients
// first, a search covers the clinic and marks theirs, and the chart puts a patient on the panel or takes them off,
// except off-site while the clinic's server can't be reached.
public class PatientPanelTests(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_home_is_a_clinicians_panel_and_an_admins_staff_page()
    {
        var clinician = await SignInAsync(apps.OnPrem, await apps.ClinicianAsync());
        var admin = await SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("An Admin", StaffRoles.Admin));

        using var clinicianHome = await Client(apps.OnPrem, clinician.Jar, followRedirects: false).GetAsync("/", Cancel);
        using var adminHome = await Client(apps.OnPrem, admin.Jar, followRedirects: false).GetAsync("/", Cancel);

        Assert.Equal("/patients", new Uri(Base, clinicianHome.Headers.Location!).AbsolutePath);
        Assert.Equal("/admin/staff", new Uri(Base, adminHome.Headers.Location!).AbsolutePath);
    }

    [Fact]
    public async Task A_clinician_adds_a_patient_to_their_panel_sees_them_first_and_takes_them_off_again()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Panel", StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Gale", "Paneled" + Guid.NewGuid().ToString("N")[..6]);
        var session = await SignInAsync(apps.OnPrem, clinician);

        var empty = await session.Client.GetStringAsync("/patients", Cancel);
        Assert.Contains("No patients are on your panel yet", empty);

        // From the chart, which asks for a reason first, since the patient isn't theirs yet. The post comes back to
        // the chart, which now shows the patient as theirs.
        using var reasoned = await PostFormAsync(session.Client, $"/patients/{patient}/reason", $"/patients/{patient}/reason", ("Reason", "Taking over their care"));
        using var added = await PostFormAsync(session.Client, $"/patients/{patient}?handler=Assign", $"/patients/{patient}");
        var chart = await ReadAsync(added);
        Assert.Equal($"/patients/{patient}", PathOf(added));
        Assert.Contains("Yours", chart);
        Assert.Contains("Remove from my patients", chart);

        var panel = await session.Client.GetStringAsync("/patients", Cancel);
        Assert.Contains("My patients", panel);
        Assert.Contains($"/patients/{patient}", panel);
        var search = await session.Client.GetStringAsync("/patients?name=Paneled", Cancel);
        Assert.Contains($"/patients/{patient}", search);
        Assert.Contains("Yours", search);

        using var removed = await PostFormAsync(session.Client, $"/patients/{patient}?handler=Unassign", $"/patients/{patient}");
        Assert.Contains("Add to my patients", await ReadAsync(removed));
        Assert.DoesNotContain($"/patients/{patient}", await session.Client.GetStringAsync("/patients", Cancel));

        // The panel listing was a read of the patient, like the chart views; the emergency access, the assignment and
        // its end are writes.
        Assert.True(await apps.AuditEventsAsync(AuditActions.Read, actorId: clinician.Id, patientId: patient) >= 6);
        Assert.Equal(2, await apps.AuditEventsAsync(AuditActions.Insert, actorId: clinician.Id, patientId: patient));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.Update, actorId: clinician.Id, patientId: patient));
    }

    [Fact]
    public async Task A_panel_is_the_clinicians_own()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Mine", StaffRoles.Clinician);
        var colleague = await apps.CreateStaffAsync("Dr. Theirs", StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Harper", "Separate");
        await apps.AssignAsync(clinician, patient);
        var session = await SignInAsync(apps.OnPrem, clinician);
        using var chart = await session.Client.GetAsync($"/patients/{patient}", Cancel);
        Assert.Equal(HttpStatusCode.OK, chart.StatusCode);

        var colleagueSession = await SignInAsync(apps.OnPrem, colleague);
        var theirPanel = await colleagueSession.Client.GetStringAsync("/patients", Cancel);
        var theirSearch = await colleagueSession.Client.GetStringAsync("/patients?name=Separate", Cancel);

        Assert.DoesNotContain($"/patients/{patient}", theirPanel);
        Assert.Contains($"/patients/{patient}", theirSearch);
        Assert.DoesNotContain("Yours", theirSearch);
    }

    [Fact]
    public async Task With_the_on_prem_app_unreachable_the_chart_offers_no_changes()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Offsite", StaffRoles.Clinician);
        var own = await apps.InsertPatientAsync("Hale", "Panelonly");
        var other = await apps.InsertPatientAsync("Ira", "Emergencyonly");
        await apps.AssignAsync(clinician, own);
        // Emergency access to the other one, granted on-site before the link went down.
        var session = await SignInAsync(apps.OnPrem, clinician);
        using (var reasoned = await PostFormAsync(session.Client, $"/patients/{other}/reason", $"/patients/{other}/reason", ("Reason", "Covering the night shift")))
        {
            Assert.Equal($"/patients/{other}", PathOf(reasoned));
        }

        // The cloud app finds out within a couple of seconds; this is that check, now.
        Assert.False(await apps.CloudCutOff.Services.GetRequiredService<CloudAuditShipper>().ShipAsync(Cancel));
        var offSite = Client(apps.CloudCutOff, session.Jar);

        var onPanel = await offSite.GetStringAsync($"/patients/{own}", Cancel);
        var underEmergency = await offSite.GetStringAsync($"/patients/{other}", Cancel);

        Assert.Contains("Panelonly, Hale", onPanel);
        Assert.Contains("Emergencyonly, Ira", underEmergency);
        Assert.All([onPanel, underEmergency], chart =>
        {
            Assert.DoesNotContain("handler=Assign", chart);
            Assert.DoesNotContain("handler=Unassign", chart);
            Assert.DoesNotContain("handler=Enroll", chart);
            Assert.Contains("Your panel can't be changed while the clinic's server can't be reached.", chart);
            Assert.Contains("Enrollment codes can be issued once the clinic's server is back.", chart);
        });
    }
}
