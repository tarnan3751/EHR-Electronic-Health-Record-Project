using System.Net;
using Ehr.Data.Accounts;
using Ehr.Web.CloudAudit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OrderTests;

namespace Ehr.Web.Tests.TestCases.UC1;

// Test Case ID: TC-UC1-10
// Purpose / scenario: Two things that stop a change to an order. Someone else discontinues the order first: the second
//   change is refused, nothing is saved, and the page says the order is no longer active. And off-site, with the
//   clinic's server unreachable: the chart offers neither action and says why, and a post that was already on screen
//   isn't saved and keeps what was typed.
// Type: Functional (exceptional condition)
// Requirements: FR-05, FR-33, FR-34, proposed FR-37
// Use case: UC 1, Place an order (changing a signed order)
// Preconditions: Two clinicians are signed in on the on-site app, each with the patient on their panel. The patient
//   has two active orders. For the second part, the cloud app's link to the on-site server is down.
// Inputs and system conditions: POST /patients/{id}/orders/{order}/discontinue and POST
//   /patients/{id}/orders/new?type=lab&replaces={order} on the on-site app, with form tokens read before the order
//   changed; then GET /patients/{id} and the same posts on the cloud app whose link is down, as js/orders.js sends
//   them and as a plain form.
// Test data: Patient Synthetic Orderable. Orders "Complete blood count (CBC)" and "Urinalysis". Clinicians "Dr. Test"
//   and "Dr. Second Orders". Reasons "Synthetic: first", "Synthetic: second", "Synthetic: typed off-site".
// Steps:
//   1. Both clinicians open the CBC order's discontinue page.
//   2. The second clinician discontinues it; then the first chooses "Discontinue order", and then signs a change of it.
//   3. Off-site with the link down, open the chart.
//   4. Post a discontinuation of the Urinalysis order, as js/orders.js does and as a plain form.
// Expected result: In step 2 the first clinician's discontinuation and change are each answered 409 with "Not saved.
//   This order is no longer active."; the order keeps the second clinician's reason and name, there is one audit row
//   for its update, and no new order exists. In step 3 the chart lists the Urinalysis order with no "Change" or
//   "Discontinue…" and says "Changing or discontinuing an order needs the clinic's server." In step 4 both posts are
//   answered 503, the plain one with the "Not saved" page showing the typed reason again, and the order stays active.
// Creator: Tahseen Arnan
public class TC_UC1_10_ChangeExceptionalCondition(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task An_order_someone_else_discontinued_first_is_not_changed_again()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        var order = await SignedAsync(session.Client, patient, "lab", "Complete blood count (CBC)", Cbc);
        var second = await apps.CreateStaffAsync("Dr. Second Orders", StaffRoles.Clinician);
        await apps.AssignAsync(second, patient);
        var colleague = await SignInAsync(apps.OnPrem, second);
        var token = Token(await session.Client.GetStringAsync(DiscontinuePath(patient, order), Cancel));
        var changeToken = Token(await session.Client.GetStringAsync(ChangePath(patient, "lab", order), Cancel));

        using var first = await DiscontinueAsync(colleague.Client, patient, order, "Synthetic: first");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var late = await DiscontinueAsync(session.Client, patient, order, "Synthetic: second", token: token);
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Contains("Not saved. This order is no longer active.", Words(await late.Content.ReadAsStringAsync(Cancel)));

        using var lateChange = await ChangeAsync(session.Client, patient, "lab", order, "Complete blood count (CBC)",
            [("indication", "Synthetic: changed late")], token: changeToken);
        Assert.Equal(HttpStatusCode.Conflict, lateChange.StatusCode);
        var page = await lateChange.Content.ReadAsStringAsync(Cancel);
        Assert.Contains("Not saved. This order is no longer active.", Words(page));
        // What was typed is still there to copy.
        Assert.Contains("value=\"Synthetic: changed late\"", page);

        Assert.Equal($"revoked|Synthetic: first|{second.Id}|-|-", await RowAsync(apps, order));
        Assert.Equal(1, await OrdersAsync(apps, patient));
        Assert.Equal(1, await apps.AuditEventsAsync("update", patientId: patient));
    }

    [Fact]
    public async Task Off_site_with_the_clinics_server_down_an_order_is_not_changed_or_discontinued()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        var order = await SignedAsync(session.Client, patient, "lab", "Urinalysis", Cbc);
        var token = Token(await session.Client.GetStringAsync(DiscontinuePath(patient, order), Cancel));
        Assert.False(await apps.CloudCutOff.Services.GetRequiredService<CloudAuditShipper>().ShipAsync(Cancel));
        var cutOff = Client(apps.CloudCutOff, session.Jar);

        var chart = await cutOff.GetStringAsync($"/patients/{patient}", Cancel);
        Assert.Contains("<strong>Urinalysis</strong> (Lab)", chart);
        Assert.DoesNotContain("data-change-order", chart);
        Assert.DoesNotContain("data-discontinue-order", chart);
        Assert.Contains("Changing or discontinuing an order needs the clinic's server.", chart);
        // The discontinued orders are read from the cloud copy.
        Assert.Contains("No discontinued orders.", await cutOff.GetStringAsync($"/patients/{patient}/orders/discontinued", Cancel));

        using var fetched = await DiscontinueAsync(cutOff, patient, order, "Synthetic: typed off-site", token: token);
        using var posted = await DiscontinueAsync(cutOff, patient, order, "Synthetic: typed off-site", fetch: false, token: token);
        using var changed = await ChangeAsync(cutOff, patient, "lab", order, "Urinalysis", Cbc, token: token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, fetched.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, posted.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, changed.StatusCode);
        var notSaved = await posted.Content.ReadAsStringAsync(Cancel);
        Assert.Contains("Not saved.</span> The clinic's server can't be reached.", notSaved);
        Assert.Contains("Synthetic: typed off-site", notSaved);
        Assert.Equal("active|-|-|-|-", await RowAsync(apps, order));
        Assert.Equal(1, await OrdersAsync(apps, patient));
    }
}
