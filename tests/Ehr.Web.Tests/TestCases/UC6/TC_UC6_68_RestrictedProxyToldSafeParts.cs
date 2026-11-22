using Xunit;
using static Ehr.Web.Tests.RecordViewTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-68
// Purpose / scenario: Someone the patient shares their record with, without sensitive items, is told which part of the
//   record a care-team member viewed only when that can't hint at a change or an item kept from them: the chart and the
//   vital signs, but not the lists, orders, who the record is shared with, or a note being written, whose views are "The
//   record" to them, as every view was before parts were recorded. Which lines they see is unchanged.
// Type: Functional (alternative flow: proxies)
// Requirements: proposed FR-57, FR-19
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: The patient has a confirmed portal account, signed in on the on-site app, and has given another patient
//   of the clinic access with messages and without sensitive items. Dr. Test signed in, then put the patient on their
//   panel.
// Inputs and system conditions: Dr. Test GETs the chart, the vital signs, the allergies' form, the order form, the
//   proxies' page and dictation; the patient and the proxy GET /portal/views; on the on-site app.
// Test data: None beyond the accounts.
// Steps:
//   1. Dr. Test opens each page.
//   2. The patient and the proxy open Who viewed the record.
// Expected result: The patient's lines from Dr. Test name each page. The proxy's name only "The chart: the summary page,
//   which lists the visit notes", "The vital signs" and "The record", one line for each of the patient's, and say
//   nothing of allergies, orders, sharing, notes or sensitive items.
// Creator: Tahseen Arnan
public class TC_UC6_68_RestrictedProxyToldSafeParts(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_proxy_without_sensitive_items_is_told_only_the_parts_that_hint_at_nothing()
    {
        var (_, session, patient) = await ViewedPatientAsync(apps, "Restricted");
        var (without, _) = await VisitSummaryTests.ProxyForAsync(apps, patient, "Toldwithout", sensitive: false, scope: "record-and-messages");

        // Step 1.
        var chart = $"/patients/{patient.Patient}";
        foreach (var page in new[] { "", "/vitals", "/allergies", "/orders/new", "/proxies", "/dictate" })
        {
            using var response = await session.Client.GetAsync(chart + page, Cancel);
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        }

        // Step 2.
        var own = PartsOf(await PageAsync(patient.Client), "staff");
        Assert.Equal(
            new[]
            {
                "Your chart: the summary page, which lists your visit notes", "Your vital signs", "Your allergies, medications and diagnoses",
                "Your orders for tests and medicines", "Who you share your record with", "A visit note, being written",
            }.Order(StringComparer.Ordinal),
            own.Select(Uncounted).Distinct().Order(StringComparer.Ordinal));
        var told = PartsOf(await PageAsync(without.Client), "staff");
        Assert.Equal(
            new[] { "The chart: the summary page, which lists the visit notes", "The vital signs", "The record" }.Order(StringComparer.Ordinal),
            told.Select(Uncounted).Distinct().Order(StringComparer.Ordinal));
        Assert.All(told, part => Assert.DoesNotMatch("(?i)allerg|order|share|being written|sensitive", part));
    }
}
