using System.Net;
using Ehr.Web.CloudAudit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.ProxyTests;

namespace Ehr.Web.Tests.TestCases.UC18;

// Test Case ID: TC-UC18-04
// Purpose / scenario: Off-site, giving access is forwarded to the clinic's server. With the clinic's server
//   unreachable, who has access stays readable, the forms are replaced by a sentence saying why, and a form already on
//   screen saves nothing. Access that has passed its end date stops working without anyone removing it.
// Type: Functional (exceptional condition)
// Requirements: FR-19, FR-33, FR-34, proposed FR-44
// Use case: UC 18, Give someone proxy access to a record
// Preconditions: The patient and the proxy have portal accounts and are signed in. For the second part, the cloud
//   app's link to the on-site server is down. For the third, the grant's end date has passed.
// Inputs and system conditions: POST /portal/access on the cloud app, then GET and POST on the cloud app whose link is
//   down; then GET /portal/access and POST /portal/acting on the on-site app.
// Test data: For 90 days. The end date is moved into the past by the test, in the database.
// Steps:
//   1. Off-site, give the proxy access.
//   2. With the link down, open "Sharing"; post the remove form that was on screen.
//   3. On-site, after the end date, as the proxy open "Sharing" and post the grant's ID to view the record; as the
//      patient open "Sharing".
// Expected result: Step 1: the post is forwarded and the access saved. Step 2: the page lists the proxy and says
//   "Read-only. Sharing can't be changed until the clinic's server is back.", with no form and no buttons;
//   the post is answered 503 and the access is still in force. Step 3: the proxy's list is empty and the post is
//   answered 404; the patient sees the entry under "Sharing that has ended", as ended.
// Creator: Tahseen Arnan
public class TC_UC18_04_ExceptionalCondition(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task Off_site_access_is_forwarded_is_read_only_during_an_outage_and_expires_by_itself()
    {
        var patient = await PersonAsync(apps, "Synthetic", "Offsite");
        var proxy = await PersonAsync(apps, "Synthetic", "Awaited");
        var cloud = Client(apps.Cloud, patient.Jar);

        using var given = await PostFormAsync(cloud, "/portal/access", "/portal/access", ("Email", proxy.Email), ("Scope", "record"), ("Days", 90));
        Assert.Contains("POST /portal/access", apps.Forwarded);
        Assert.Equal([$"{proxy.Email} record false patient in-force"], await GrantsAsync(apps, patient.Patient));
        var grant = await GrantIdAsync(apps, patient.Patient);
        var token = Token(await patient.Client.GetStringAsync("/portal/access", Cancel));

        Assert.False(await apps.CloudCutOff.Services.GetRequiredService<CloudAuditShipper>().ShipAsync(Cancel));
        var cutOff = Client(apps.CloudCutOff, patient.Jar);
        var readOnly = await cutOff.GetStringAsync("/portal/access", Cancel);
        Assert.Contains(proxy.Email, readOnly);
        Assert.Contains("Read-only. Sharing can't be changed until the clinic's server is back.", Words(readOnly));
        Assert.DoesNotContain("name=\"Email\"", readOnly);
        Assert.DoesNotContain("Stop sharing", readOnly);
        using var notSaved = await cutOff.PostAsync("/portal/access?handler=Revoke", Form(("__RequestVerificationToken", token), ("grantId", grant.ToString())), Cancel);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, notSaved.StatusCode);
        Assert.EndsWith("in-force", (await GrantsAsync(apps, patient.Patient)).Single());

        Assert.Contains("<h3>Synthetic Offsite</h3>", await proxy.Client.GetStringAsync("/portal/access", Cancel));
        await using (var db = await apps.OpenDatabaseAsync())
        {
            await using var expire = new Npgsql.NpgsqlCommand("UPDATE ehr.proxy_grants SET expires_at = now() - interval '1 minute' WHERE id = @id", db);
            expire.Parameters.AddWithValue("id", grant);
            await expire.ExecuteNonQueryAsync(Cancel);
        }

        Assert.Contains("No records shared with you.", await proxy.Client.GetStringAsync("/portal/access", Cancel));
        using var tooLate = await PostFormAsync(proxy.Client, "/portal/acting", "/portal/access", ("grantId", grant));
        Assert.Equal(HttpStatusCode.NotFound, tooLate.StatusCode);
        var own = await patient.Client.GetStringAsync("/portal/access", Cancel);
        Assert.Contains("Sharing that has ended", own);
        Assert.Contains("· ended ", own);
        Assert.Contains("Not shared with anyone.", own);
    }
}
