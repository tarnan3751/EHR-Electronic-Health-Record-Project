using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.RecordViewTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-45
// Purpose / scenario: A view that showed the patient's sensitive items is listed for the patient, and for someone the
//   patient allowed sensitive items, as "with its sensitive items"; for someone without them it isn't there at all: their
//   page after the view is their page before it, with only their own look at it added.
// Type: Functional (invalid input: what a proxy without sensitive items is not shown)
// Requirements: proposed FR-57, FR-19, proposed FR-39
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: The patient has a confirmed portal account, signed in on the on-site app, and has given two other
//   patients of the clinic access, one with sensitive items and one without; each is looking at the record. Dr. Test
//   has the patient on their panel and has opened the chart.
// Inputs and system conditions: The proxy without sensitive items GETs /portal/views; Dr. Test opens the chart with
//   sensitive items shown; the patient and both proxies GET /portal/views; on the on-site app.
// Test data: None beyond the accounts.
// Steps:
//   1. The proxy without sensitive items opens Who viewed this record.
//   2. Dr. Test opens the chart with "Show sensitive items".
//   3. The patient, the proxy with sensitive items, and the one without, open the page.
// Expected result: Step 3: the patient's newest line from someone else is "Dr. Test, clinician", "Your chart, with its
//   sensitive items: the summary page, ..."; the proxy's with sensitive items, "The chart, with its sensitive items: ...". The page of the proxy
//   without them is the page of step 1 with one line before it, their own "Who viewed the record", and nothing on it
//   says "sensitive".
// Creator: Tahseen Arnan
public class TC_UC6_45_SensitiveViewsByRole(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_view_of_sensitive_items_is_listed_as_such_or_not_at_all()
    {
        var (_, session, patient) = await ViewedPatientAsync(apps, "Private");
        var (allowed, _) = await VisitSummaryTests.ProxyForAsync(apps, patient, "Allowed", sensitive: true);
        var (without, _) = await VisitSummaryTests.ProxyForAsync(apps, patient, "Without", sensitive: false);
        var chart = $"/patients/{patient.Patient}";
        await session.Client.GetStringAsync(chart, Cancel);

        // Step 1.
        var before = Lines(await PageAsync(without.Client));

        // Step 2.
        await session.Client.GetStringAsync(chart + "?sensitive=true", Cancel);

        // Step 3.
        Assert.StartsWith("staff | Dr. Test, clinician | Your chart, with its sensitive items: the summary page, which lists your visit notes | ",
            Lines(await PageAsync(patient.Client)).First(line => !line.StartsWith("you", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.StartsWith("staff | Dr. Test, clinician | The chart, with its sensitive items: the summary page, which lists the visit notes | ",
            Lines(await PageAsync(allowed.Client)).First(line => !line.StartsWith("you", StringComparison.Ordinal)), StringComparison.Ordinal);
        var html = await PageAsync(without.Client);
        var after = Lines(html);
        Assert.StartsWith("you | You | Who viewed the record | ", after[0], StringComparison.Ordinal);
        Assert.Equal(before, after.Skip(1));
        Assert.DoesNotContain("sensitive", Words(html), StringComparison.OrdinalIgnoreCase);
    }
}
