using System.Net;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// The audit page (Areas/Clinical/Pages/Admin/Audit, AuditLog): admins see what happened to a patient, with emergency
// access flagged and its reason shown; nobody else gets in; and the cloud app serves it from its own database.
public class AuditLogTests(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task An_admin_reviews_a_patients_events_with_emergency_access_flagged()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Reviewed", StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Kit", "Audited");
        var clinicianSession = await SignInAsync(apps.OnPrem, clinician);
        using var opened = await PostFormAsync(clinicianSession.Client, $"/patients/{patient}/reason", $"/patients/{patient}/reason",
            ("Reason", "Consult requested by the attending"));
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);

        var admin = await SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("The Reviewer", StaffRoles.Admin));
        var html = await admin.Client.GetStringAsync($"/admin/audit?Patient={patient}", Cancel);

        Assert.Contains("Dr. Reviewed", html);
        Assert.Contains("Consult requested by the attending", html);
        Assert.Contains("<tr class=\"sc-table__flagged\">", html);
        Assert.Contains(">Break the glass <span class=\"sc-tag sc-tag--danger\">Review</span>", html);
        Assert.Contains("<td>Read</td>", html);
        // The patient by ID, not by name: an admin has no access to patient data, and the only patient values on this
        // page are the ones the write audit recorded as changed.
        Assert.Contains($"<td class=\"sc-table__id\">{patient}</td>", html);

        var flaggedOnly = await admin.Client.GetStringAsync($"/admin/audit?Patient={patient}&Flagged=true", Cancel);
        Assert.Contains("<td>Break the glass <span", flaggedOnly);
        Assert.DoesNotContain("<td>Read</td>", flaggedOnly);

        var someoneElse = await admin.Client.GetStringAsync($"/admin/audit?Patient={Guid.CreateVersion7()}", Cancel);
        Assert.Contains("No events match.", someoneElse);
    }

    // Each read says which part of the record it was for (step 13c2), in a column of its own after the row, and the page
    // lists one part's reads; an event that isn't a read has none.
    [Fact]
    public async Task An_admin_sees_the_part_each_read_was_for_and_lists_one_parts_reads()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Parted", StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Kit", "Partaudited");
        var clinicianSession = await SignInAsync(apps.OnPrem, clinician);
        using var opened = await PostFormAsync(clinicianSession.Client, $"/patients/{patient}/reason", $"/patients/{patient}/reason",
            ("Reason", "Consult requested by the attending"));
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await clinicianSession.Client.GetAsync($"/patients/{patient}/vitals", Cancel)).StatusCode);

        var admin = await SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("The Part Reviewer", StaffRoles.Admin));
        var html = await admin.Client.GetStringAsync($"/admin/audit?Patient={patient}", Cancel);

        Assert.Contains("<th>Record</th><th>Part</th>", html);
        foreach (var part in new[] { AuditParts.Chart, AuditParts.Vitals })
        {
            Assert.Matches($"(?s)<td>Read</td>\\s*<td class=\"sc-table__id\">{patient}</td>\\s*<td class=\"sc-table__id\">patients {patient}</td>\\s*<td class=\"sc-table__mono\">{part}</td>", html);
        }

        Assert.Matches($"(?s)<td>Break the glass <span[^>]*>Review</span> <a[^>]*>Open the review</a></td>\\s*<td class=\"sc-table__id\">{patient}</td>\\s*<td[^>]*>[^<]*</td>\\s*<td class=\"sc-table__muted\">—</td>", html);

        var vitals = await admin.Client.GetStringAsync($"/admin/audit?Patient={patient}&Part={AuditParts.Vitals}", Cancel);
        Assert.Contains($"<td class=\"sc-table__mono\">{AuditParts.Vitals}</td>", vitals);
        Assert.DoesNotContain($">{AuditParts.Chart}</td>", vitals);
        Assert.DoesNotContain("<td>Break the glass", vitals);
    }

    [Fact]
    public async Task Only_admins_reach_the_audit_page()
    {
        var session = await SignInAsync(apps.OnPrem, await apps.ClinicianAsync());

        using var response = await Client(apps.OnPrem, session.Jar, followRedirects: false).GetAsync("/admin/audit", Cancel);

        Assert.Equal("/denied", new Uri(Base, response.Headers.Location!).AbsolutePath);
    }

    [Fact]
    public async Task The_cloud_app_serves_the_audit_page()
    {
        var admin = await SignInAsync(apps.Cloud, await apps.CreateStaffAsync("Remote Reviewer", StaffRoles.Admin));

        using var response = await Client(apps.Cloud, admin.Jar, followRedirects: false).GetAsync("/admin/audit?Action=sign_in", Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Remote Reviewer", await response.Content.ReadAsStringAsync(Cancel));
    }
}
