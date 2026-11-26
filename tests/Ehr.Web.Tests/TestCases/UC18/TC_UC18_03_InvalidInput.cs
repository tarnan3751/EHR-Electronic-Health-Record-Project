using System.Net;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.ProxyTests;

namespace Ehr.Web.Tests.TestCases.UC18;

// Test Case ID: TC-UC18-03
// Purpose / scenario: Access isn't given to something that isn't an email address, to oneself, for a scope or a length
//   of time the form doesn't offer; giving it again to the same person replaces what they had; and a patient can't
//   change or remove a grant that isn't on their own record.
// Type: Functional (invalid input)
// Requirements: FR-19, proposed FR-44
// Use case: UC 18, Give someone proxy access to a record
// Preconditions: Three patients with portal accounts, signed in on the on-site app. The third has given the second
//   access.
// Inputs and system conditions: POST /portal/access, ?handler=Revoke and ?handler=Sensitive, as plain form posts.
// Test data: Email "not an email"; the patient's own email; scope "everything"; 3,650 days.
// Steps:
//   1. Post the form with no email; with "not an email"; with the patient's own email; with the made-up scope; with
//      3,650 days.
//   2. Give access to the second patient for the record only, then again with messages.
//   3. Post the third patient's grant ID to Remove and to Show sensitive items.
// Expected result: Step 1: each is answered 422 with "Not saved:" and, in turn, "Enter the person's email address.",
//   "Enter an email address, like name@example.com.", "That's your own email address. Enter the other person's.",
//   "Choose what they can see." and "Choose how long the access lasts."; what was typed stays in the field; nothing is
//   saved and no email is sent. Step 2: one grant is in force, with messages; the first is ended. Step 3: both are
//   answered 404, and the third patient's grant is unchanged.
// Creator: Tahseen Arnan
public class TC_UC18_03_InvalidInput(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Access_is_not_given_for_bad_input_is_replaced_when_given_again_and_is_only_the_patients_to_change()
    {
        var patient = await PersonAsync(apps, "Synthetic", "Careful");
        var proxy = await PersonAsync(apps, "Synthetic", "Second");
        var third = await PersonAsync(apps, "Synthetic", "Third");
        await GiveAsync(third, proxy.Email);
        var theirs = await GrantIdAsync(apps, third.Patient);
        var emails = apps.Emails.Sent.Count;

        foreach (var (email, scope, days, message) in new[]
        {
            ("", "record", 90, "Enter the person's email address."),
            ("not an email", "record", 90, "Enter an email address, like name@example.com."),
            (patient.Email, "record", 90, "That's your own email address. Enter the other person's."),
            (proxy.Email, "everything", 90, "Choose what they can see."),
            (proxy.Email, "record", 3650, "Choose how long the access lasts."),
        })
        {
            using var refused = await PostFormAsync(patient.Client, "/portal/access", "/portal/access", ("Email", email), ("Scope", scope), ("Days", days));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            var page = await refused.Content.ReadAsStringAsync(Cancel);
            Assert.Contains("Not saved:", page);
            Assert.Contains(message, Words(page));
            if (email.Length > 0)
            {
                Assert.Contains($"name=\"Email\" value=\"{email}\"", page);
            }
        }

        // An address a portal account couldn't have: refused here, so access is never given to one that can't accept it.
        using var apostrophe = await PostFormAsync(patient.Client, "/portal/access", "/portal/access", ("Email", "o'brien@example.com"), ("Scope", "record"), ("Days", 90));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, apostrophe.StatusCode);
        Assert.Contains("Enter an email address, like name@example.com.", await apostrophe.Content.ReadAsStringAsync(Cancel));

        Assert.Empty(await GrantsAsync(apps, patient.Patient));
        Assert.Equal(emails, apps.Emails.Sent.Count);

        await GiveAsync(patient, proxy.Email);
        await GiveAsync(patient, proxy.Email, "record-and-messages");
        Assert.Equal([$"{proxy.Email} record false patient revoked", $"{proxy.Email} record-and-messages false patient in-force"], await GrantsAsync(apps, patient.Patient));

        foreach (var handler in new[] { "Revoke", "Sensitive" })
        {
            using var notMine = await PostFormAsync(patient.Client, $"/portal/access?handler={handler}", "/portal/access", ("grantId", theirs), ("allow", "true"));
            Assert.Equal(HttpStatusCode.NotFound, notMine.StatusCode);
        }

        Assert.Equal([$"{proxy.Email} record false patient in-force"], await GrantsAsync(apps, third.Patient));
    }
}
