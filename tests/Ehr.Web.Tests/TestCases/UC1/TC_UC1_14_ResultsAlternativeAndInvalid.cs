using System.Net;
using Ehr.Data.Accounts;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.ResultTests;

namespace Ehr.Web.Tests.TestCases.UC1;

// Test Case ID: TC-UC1-14
// Purpose / scenario: Results that arrive in parts, values that aren't numbers, and who may do what. A colleague on
//   the patient's care team can file and acknowledge; a clinician under emergency access can read the results but not
//   file or acknowledge; once acknowledged, nothing more is added.
// Type: Functional (alternative flow and invalid input)
// Requirements: FR-05, proposed FR-45
// Use case: UC 1, Place an order
// Preconditions: One clinician has signed a complete blood count for a patient on their panel. A second clinician
//   also has the patient on their panel; a third doesn't.
// Inputs and system conditions: GET and POST /patients/{id}/orders/{order}/results and ?handler=Acknowledge; GET
//   /patients/{other}/orders/{order}/results.
// Test data: Hemoglobin "abc", then "14.2"; nothing typed; a collection time tomorrow; platelets 90; hematocrit 41.
// Steps:
//   1. As the first clinician, post "abc" for hemoglobin; post with nothing typed; post 14.2 with tomorrow's time.
//   2. Post hemoglobin 14.2. Later, as the second clinician, post platelets 90. Then, as the first, choose
//      "Acknowledge results" on the page that still shows only hemoglobin.
//   3. As the third clinician, after giving a reason on the chart, open the results; post a value and an
//      acknowledgment.
//   4. Request the results under another patient's address.
//   5. As the second clinician, acknowledge. Then, as the first, post hematocrit 41 and an acknowledgment from a page
//      that was already open.
// Expected result: Step 1: each is answered 422 with "Not saved:" and "Enter a number, like 5.4.", "Enter at least one
//   result." and "The collection time can't be in the future."; what was typed stays; nothing is filed. Step 2: both
//   are saved; the form then offers only the tests still waiting; one inbox item exists, for the first clinician.
//   The acknowledgment from the older page is answered 409, the page comes back showing the low platelets, and
//   nothing is recorded.
//   Step 3: the results are shown with "Add this patient to your panel to acknowledge results." and no form; both
//   posts are redirected to the chart and nothing changes. Step 4: 404. Step 5: the acknowledgment is the second
//   clinician's and clears the first's inbox; the late posts are answered 409 with "Not saved: this order changed
//   while the page was open." and the page shows who acknowledged; hematocrit is not filed.
// Creator: Tahseen Arnan
public class TC_UC1_14_ResultsAlternativeAndInvalid(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Partial_results_bad_values_and_who_may_file_and_acknowledge()
    {
        var (clinician, patient, session) = await OnPanelAsync(apps);
        var order = await SignedAsync(session.Client, patient, "lab", "Complete blood count (CBC)", Cbc);
        var path = Path(patient, order);

        using var notANumber = await FileAsync(session.Client, patient, order, (Hemoglobin, "abc"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notANumber.StatusCode);
        var refused = await notANumber.Content.ReadAsStringAsync(Cancel);
        Assert.Contains("Not saved:", refused);
        Assert.Contains("Enter a number, like 5.4.", refused);
        Assert.Contains($"name=\"Values[{Hemoglobin}]\" value=\"abc\"", refused);
        using var nothing = await FileAsync(session.Client, patient, order);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, nothing.StatusCode);
        Assert.Contains("Enter at least one result.", await nothing.Content.ReadAsStringAsync(Cancel));
        using var tomorrow = await PostFormAsync(session.Client, path, path, ($"Values[{Hemoglobin}]", "14.2"), ("CollectedAt", DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-ddTHH:mm")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tomorrow.StatusCode);
        Assert.Contains("The collection time can&#x27;t be in the future.", await tomorrow.Content.ReadAsStringAsync(Cancel));
        Assert.Empty(await FiledAsync(apps, patient));
        Assert.Equal("active", await StatusAsync(apps, patient));

        using var first = await FileAsync(session.Client, patient, order, (Hemoglobin, "14.2"));
        var partial = await ReadAsync(first);
        Assert.Contains("Enter the remaining results", partial);
        Assert.DoesNotContain($"name=\"Values[{Hemoglobin}]\"", partial);
        Assert.Contains($"name=\"Values[{Platelets}]\"", partial);
        var second = await apps.CreateStaffAsync("Dr. Second Results", StaffRoles.Clinician);
        await apps.AssignAsync(second, patient);
        var colleague = await SignInAsync(apps.OnPrem, second);
        using var later = await FileAsync(colleague.Client, patient, order, (Platelets, "90"));
        Assert.Contains("Saved:", await ReadAsync(later));

        // The first clinician's page still shows one result. Acknowledging from it covers only what it showed: the
        // page comes back with the low platelets, and nothing is recorded.
        using var unseen = await session.Client.PostAsync(path + "?handler=Acknowledge",
            Form(("__RequestVerificationToken", Token(partial)), ("Seen", SeenOn(partial))), Cancel);
        Assert.Equal(HttpStatusCode.Conflict, unseen.StatusCode);
        var shownAgain = await unseen.Content.ReadAsStringAsync(Cancel);
        Assert.Contains("Not saved:", shownAgain);
        Assert.Contains("90 10*3/uL", shownAgain);
        Assert.Empty(await MessageTests.RowsAsync(apps, patient, "id::text", "result_acknowledgments", "id"));
        Assert.Equal([$"{Hemoglobin} 14.2 normal", $"{Platelets} 90 low"], await FiledAsync(apps, patient));
        Assert.Equal([clinician.Id.ToString()], await MessageTests.RowsAsync(apps, patient, "recipient_id::text", "inbox_items", "created_at", "done_at IS NULL"));
        var stale = Token(await session.Client.GetStringAsync(path, Cancel));

        var third = await apps.CreateStaffAsync("Dr. Covering Results", StaffRoles.Clinician);
        var covering = await SignInAsync(apps.OnPrem, third);
        using var reason = await PostFormAsync(covering.Client, $"/patients/{patient}/reason", $"/patients/{patient}/reason", ("Reason", "Covering the night shift"));
        var seen = await covering.Client.GetStringAsync(path, Cancel);
        Assert.Contains("14.2 g/dL", seen);
        Assert.Contains("Add this patient to your panel to acknowledge results.", seen);
        Assert.Contains("Add this patient to your panel to enter results.", seen);
        Assert.DoesNotContain("<form method=\"post\" class=\"sc-form\"", seen);
        var noRedirects = Client(apps.OnPrem, covering.Jar, followRedirects: false);
        foreach (var target in new[] { path, path + "?handler=Acknowledge" })
        {
            using var notTheirs = await noRedirects.PostAsync(target, Form(("__RequestVerificationToken", Token(seen)), ($"Values[{Hematocrit}]", "41"), ("CollectedAt", "2026-11-01T08:00")), Cancel);
            Assert.Equal($"/patients/{patient}", new Uri(Base, notTheirs.Headers.Location!).AbsolutePath);
        }

        var other = await apps.InsertPatientAsync("Synthetic", "Unrelated");
        await apps.AssignAsync(clinician, other);
        using var crossed = await session.Client.GetAsync(Path(other, order), Cancel);
        Assert.Equal(HttpStatusCode.NotFound, crossed.StatusCode);

        using var acknowledged = await AcknowledgeAsync(colleague.Client, patient, order);
        Assert.Contains($"Acknowledged by Dr. Second Results", await ReadAsync(acknowledged));
        Assert.Empty(await MessageTests.RowsAsync(apps, patient, "recipient_id::text", "inbox_items", "created_at", "done_at IS NULL"));
        foreach (var target in new[] { path, path + "?handler=Acknowledge" })
        {
            using var tooLate = await session.Client.PostAsync(target, Form(("__RequestVerificationToken", stale), ($"Values[{Hematocrit}]", "41"), ("CollectedAt", "2026-11-01T08:00")), Cancel);
            Assert.Equal(HttpStatusCode.Conflict, tooLate.StatusCode);
            var now = await tooLate.Content.ReadAsStringAsync(Cancel);
            Assert.Contains("Not saved:", now);
            Assert.Contains($"Acknowledged by Dr. Second Results", now);
        }

        Assert.Equal(2, (await FiledAsync(apps, patient)).Count);
        Assert.Equal([second.Id.ToString()], await MessageTests.RowsAsync(apps, patient, "acknowledged_by::text", "result_acknowledgments", "id"));
    }
}
