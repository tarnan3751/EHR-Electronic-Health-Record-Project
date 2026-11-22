using Xunit;
using static Ehr.Web.Tests.RecordViewTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-69
// Purpose / scenario: Someone the patient shares their record with, with sensitive items, is told every part a
//   care-team member viewed, as the patient is, but a conversation only when their access includes messages: without
//   messages, writing to the patient reads as "The record", and opening a conversation isn't listed at all.
// Type: Functional (alternative flow: proxies)
// Requirements: proposed FR-57, FR-19
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: The patient has a confirmed portal account, signed in on the on-site app, and has given two other
//   patients of the clinic access with sensitive items, one with messages and one without. Dr. Test signed in, then
//   put the patient on their panel.
// Inputs and system conditions: The patient starts a conversation; Dr. Test GETs the allergies' form, a new message to
//   the patient, and the conversation; both proxies GET /portal/views; on the on-site app.
// Test data: The conversation "Synthetic question" with "Synthetic body".
// Steps:
//   1. The patient starts the conversation; Dr. Test opens the pages.
//   2. Each proxy opens Who viewed the record.
// Expected result: With messages: Dr. Test's lines are "The allergies, medications and diagnoses" and "The messages".
//   Without: "The allergies, medications and diagnoses" and "The record", and no line about messages.
// Creator: Tahseen Arnan
public class TC_UC6_69_ProxiesWithSensitiveItemsAndMessages(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_proxy_with_sensitive_items_is_told_every_part_and_a_conversation_only_with_messages()
    {
        var (_, session, patient) = await ViewedPatientAsync(apps, "Messaged");
        var (withMessages, _) = await VisitSummaryTests.ProxyForAsync(apps, patient, "Withmessages", sensitive: true, scope: "record-and-messages");
        var (withoutMessages, _) = await VisitSummaryTests.ProxyForAsync(apps, patient, "Withoutmessages", sensitive: true);

        // Step 1.
        var thread = await MessageTests.StartAsync(patient.Client, "Synthetic question", "Synthetic body");
        var chart = $"/patients/{patient.Patient}";
        foreach (var page in new[] { "/allergies", "/messages/new", $"/messages/{thread}" })
        {
            using var response = await session.Client.GetAsync(chart + page, Cancel);
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        }

        // Step 2.
        Assert.Equal(["The allergies, medications and diagnoses", "The messages"],
            PartsOf(await PageAsync(withMessages.Client), "staff").Select(Uncounted).Distinct().Order(StringComparer.Ordinal));
        var without = PartsOf(await PageAsync(withoutMessages.Client), "staff");
        Assert.Equal(["The allergies, medications and diagnoses", "The record"], without.Select(Uncounted).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(1, without.Count(part => Uncounted(part) == "The record"));
    }
}
