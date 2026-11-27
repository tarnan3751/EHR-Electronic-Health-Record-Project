using Xunit;
using static Ehr.Web.Tests.RecordViewTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-66
// Purpose / scenario: The patient's own views in the portal are named by the page they opened: the home, appointments,
//   test results, messages, visit summaries, who they share their record with, the printable record, the download and
//   Who viewed my record itself.
// Type: Functional (successful flow: the patient's own views)
// Requirements: proposed FR-57
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: The patient has a confirmed portal account and is signed in on the on-site app.
// Inputs and system conditions: The patient GETs /portal, /portal/appointments, /portal/results, /portal/messages,
//   /portal/summaries, /portal/access, /portal/record/print, the record's file, then /portal/views twice; on the
//   on-site app.
// Test data: None beyond the account.
// Steps:
//   1. The patient opens each page and downloads their record.
//   2. The patient opens Who viewed my record, twice.
// Expected result: The patient's own lines name, each once or more and nothing else: "Your portal home page, with your
//   appointments", "Your appointments", "Your test results", "Your messages", "Your visit summaries", "Who you share your
//   record with", "Your record, as a page to print", "Your record, downloaded", "Who viewed your record".
// Creator: Tahseen Arnan
public class TC_UC6_66_OwnPortalPagesNamed(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_patients_own_views_are_named_by_the_page_they_opened()
    {
        var patient = await ProxyTests.PersonAsync(apps, "Synthetic", "Ownpages");

        // Step 1.
        foreach (var page in new[] { "/portal", "/portal/appointments", "/portal/results", "/portal/messages", "/portal/summaries", "/portal/access",
            RecordDownload.PrintPath, RecordDownload.FilePath })
        {
            using var response = await patient.Client.GetAsync(page, Cancel);
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        }

        // Step 2.
        await PageAsync(patient.Client);
        Assert.Equal(
            new[]
            {
                "Your portal home page, with your appointments", "Your appointments", "Your test results", "Your messages", "Your visit summaries",
                "Who you share your record with", "Your record, as a page to print", "Your record, downloaded", "Who viewed your record",
            }.Order(StringComparer.Ordinal),
            PartsOf(await PageAsync(patient.Client), "you").Select(Uncounted).Distinct().Order(StringComparer.Ordinal));
    }
}
