using Ehr.Data.Accounts;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.RecordViewTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-65
// Purpose / scenario: A clinician's list that named the patient is named as such: a search, their list of patients,
//   their day's appointments, their list of tasks. A proxy without sensitive items is told of each but the list of
//   tasks, which may hold an item about something kept from them, and reads it as "The record".
// Type: Functional (alternative flow: lists that name the patient)
// Requirements: proposed FR-57
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: The patient has a confirmed portal account, signed in on the on-site app, and has given another
//   patient of the clinic access without sensitive items. Dr. Lists, a clinician, signed in, then put the patient on
//   their panel; the patient has an appointment with Dr. Lists a minute from now, and an item in Dr. Lists's inbox.
// Inputs and system conditions: Dr. Lists GETs /patients, a search by the patient's family name, /schedule and /inbox;
//   the patient and the proxy GET /portal/views; on the on-site app.
// Test data: The family name "Listed".
// Steps:
//   1. Dr. Lists opens their patients, searches for the patient, opens their day and their inbox.
//   2. The patient and the proxy open Who viewed the record.
// Expected result: The patient's lines from Dr. Lists are, each once or more: "Your name, in their list of patients",
//   "Your name, in a search for patients", "Your name, on their day's appointments", "Your name, on their list of
//   tasks". The proxy's are "The patient's name, ..." for the first three and "The record" for the last.
// Creator: Tahseen Arnan
public class TC_UC6_65_YourNameInLists(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Each_list_that_named_the_patient_is_named()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Lists", StaffRoles.Clinician);
        var patient = await ProxyTests.PersonAsync(apps, "Synthetic", "Listed");
        var session = await SignInAsync(apps.OnPrem, clinician);
        await AssignNowAsync(apps, clinician, patient.Patient);
        var (without, _) = await VisitSummaryTests.ProxyForAsync(apps, patient, "Listswithout", sensitive: false);
        await DayAndInboxAsync(apps, clinician.Id, patient.Patient);

        // Step 1.
        foreach (var page in new[] { "/patients", "/patients?name=Listed", "/schedule", "/inbox" })
        {
            using var response = await session.Client.GetAsync(page, Cancel);
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        }

        // Step 2.
        Assert.Equal(
            new[] { "Your name, in their list of patients", "Your name, in a search for patients", "Your name, on their day's appointments", "Your name, on their list of tasks" }
                .Order(StringComparer.Ordinal),
            PartsOf(await PageAsync(patient.Client), "staff").Select(Uncounted).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(
            new[] { "The patient's name, in their list of patients", "The patient's name, in a search for patients", "The patient's name, on their day's appointments", "The record" }
                .Order(StringComparer.Ordinal),
            PartsOf(await PageAsync(without.Client), "staff").Select(Uncounted).Distinct().Order(StringComparer.Ordinal));
    }
}
