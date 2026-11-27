using System.Text.RegularExpressions;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// Small slips in what pages say, found in a click-through of the apps (2026-11-26): a count that reads wrong for one,
// and two spaces where the markup meant one, which the page's text keeps though a browser shows one.
public partial class PageTextTests(WebApps apps) : IClassFixture<WebApps>
{
    // The flowsheet's caption, which a screen reader reads: "The latest 1 sets" before.
    [Fact]
    public async Task One_set_of_vital_signs_is_one_set()
    {
        var (_, patient, session) = await OrderTests.OnPanelAsync(apps);
        using var recorded = await VitalTests.RecordAsync(apps, session.Client, patient, 10, (VitalTests.HeartRate, "72"));
        Assert.Contains("The latest set of vital signs.", await ReadAsync(recorded));

        using var again = await VitalTests.RecordAsync(apps, session.Client, patient, 5, (VitalTests.HeartRate, "74"));
        Assert.Contains("The latest 2 sets of vital signs, newest first.", await ReadAsync(again));
    }

    [Fact]
    public async Task The_order_forms_item_label_has_one_space()
    {
        var (_, patient, session) = await OrderTests.OnPanelAsync(apps);

        var html = await session.Client.GetStringAsync(OrderTests.FormPath(patient, "lab"), Cancel);

        Assert.Contains("<label for=\"order-item\">Item <span>(required)</span></label>", html);
        // Nor a space left at the end of a label that isn't required.
        Assert.DoesNotContain(" </label>", html);
        Assert.DoesNotMatch(DoubledSpace(), Words(html));
    }

    // The same slip in the heading of an emergency access's review: "Emergency access  Review".
    [Fact]
    public async Task The_emergency_access_heading_has_one_space()
    {
        var access = await EmergencyAccessFlows.GrantAsync(apps, "Dr. Heading Spaces");
        var admin = await SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("An Admin Reading", Ehr.Data.Accounts.StaffRoles.Admin));

        var html = await admin.Client.GetStringAsync($"/admin/emergency-access/{access.Id}", Cancel);

        Assert.Contains("<h1>Emergency access <span class=\"sc-tag sc-tag--danger\">Review</span></h1>", html);
    }

    // Two spaces between words, as a page's text reads once its tags are taken out.
    [GeneratedRegex("[A-Za-z.,:)] {2,}[A-Za-z(]")]
    private static partial Regex DoubledSpace();
}
