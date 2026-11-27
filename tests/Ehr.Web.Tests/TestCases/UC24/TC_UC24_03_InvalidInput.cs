using System.Net;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.EmergencyAccessFlows;

namespace Ehr.Web.Tests.TestCases.UC24;

// Test Case ID: TC-UC24-03
// Purpose / scenario: A review the database or the page refuses, with nothing saved (the expansion plan's step 18): no
//   outcome; a note blank, too short or too long; one's own emergency access, for an administrator who is also a
//   clinician; a second review, from a page opened before the first was saved; a review of an access still in force,
//   posted anyway; the page asked for by a clinician, by someone signed out, or for no access at all.
// Type: Functional (invalid input)
// Requirements: proposed FR-64, FR-29
// Use case: UC 24, Review an emergency access
// Preconditions: Dr. Admin Clinician (both roles) used emergency access; so did Dr. Twice; the administrators First
//   Reviewer and Second Reviewer, on the on-site app.
// Inputs and system conditions: POST /admin/emergency-access/{id} with the fields below; GET it as a clinician, signed
//   out, and for an unknown ID.
// Test data: Notes "", "Too short", 501 letters "x", and "Synthetic: checked the rota, the clinician was covering the
//   ward."
// Steps:
//   1. As First Reviewer, post Dr. Twice's ended access with no outcome, then with each bad note.
//   2. As Dr. Admin Clinician, open and post their own ended access.
//   3. Open Dr. Twice's access as both reviewers; Second Reviewer saves; First Reviewer saves from the page opened first.
//   4. Post a review of an access still in force.
//   5. GET the page as a clinician, signed out, and for an unknown ID.
// Expected result: Step 1: the page again, "Say whether the access was appropriate.", "Write a note…", "Use at least 10
//   characters.", "Use 500 characters or fewer."; nothing saved. Step 2: "Your own emergency access. Another
//   administrator reviews it." and no form; the post "Not saved. This is your own emergency access…"; nothing saved.
//   Step 3: "Not saved. Someone reviewed this access first…", Second Reviewer's review shown and kept. Step 4: "Not
//   saved. This access is still in force…". Step 5: /denied, /sign-in, and 404.
// Creator: Tahseen Arnan
public class TC_UC24_03_InvalidInput(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Refused_reviews_save_nothing_and_say_why()
    {
        var first = await SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("First Reviewer", StaffRoles.Admin));
        var secondAccount = await apps.CreateStaffAsync("Second Reviewer", StaffRoles.Admin);
        var second = await SignInAsync(apps.OnPrem, secondAccount);
        var twice = await GrantAsync(apps, "Dr. Twice");
        await EndAsync(apps, twice.Id);

        // 1.
        using (var noOutcome = await ReviewAsync(first.Client, twice.Id, null, Note))
        {
            Assert.Contains("Say whether the access was appropriate.", await ReadAsync(noOutcome));
        }

        foreach (var (note, said) in new[] { ("", "Write a note: what you checked, and what you found."), ("Too short", "Use at least 10 characters."), (new string('x', 501), "Use 500 characters or fewer.") })
        {
            using var refused = await ReviewAsync(first.Client, twice.Id, EmergencyAccessOutcomes.Appropriate, note);
            Assert.Contains(said, await ReadAsync(refused));
        }

        Assert.Empty(await ReviewsAsync(apps, twice.Id));

        // 2.
        var both = await apps.CreateStaffAsync("Dr. Admin Clinician", StaffRoles.Clinician, StaffRoles.Admin);
        var own = await GrantAsync(apps, "", clinician: both);
        await EndAsync(apps, own.Id);
        var ownPage = await own.Session.Client.GetStringAsync($"/admin/emergency-access/{own.Id}", Cancel);
        Assert.Contains("Your own emergency access.</span> Another administrator reviews it.", ownPage);
        Assert.DoesNotContain("data-review-form", ownPage);
        using (var forged = await own.Session.Client.PostAsync($"/admin/emergency-access/{own.Id}", Form(("Outcome", EmergencyAccessOutcomes.Appropriate), ("Note", Note), ("__RequestVerificationToken", Token(await own.Session.Client.GetStringAsync("/inbox", Cancel)))), TestContext.Current.CancellationToken))
        {
            Assert.Contains("Not saved.</span> This is your own emergency access.", await ReadAsync(forged));
        }

        Assert.Empty(await ReviewsAsync(apps, own.Id));

        // 3.
        var stale = await first.Client.GetAsync($"/admin/emergency-access/{twice.Id}", Cancel);
        using (var saved = await ReviewAsync(second.Client, twice.Id, EmergencyAccessOutcomes.Appropriate, Note))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }

        using (var late = await PostFormAsync(first.Client, $"/admin/emergency-access/{twice.Id}", stale,
            ("Outcome", EmergencyAccessOutcomes.NotAppropriate), ("Note", "Synthetic: a second opinion, too late.")))
        {
            var answer = await ReadAsync(late);
            Assert.Contains("Not saved.</span> Someone reviewed this access first.", answer);
            Assert.Contains("Reviewed by Second Reviewer", answer);
        }

        Assert.Equal([$"{secondAccount.Id} appropriate {Note}"], await ReviewsAsync(apps, twice.Id));

        // 4.
        var inForce = await GrantAsync(apps, "Dr. Still In Force");
        using (var early = await first.Client.PostAsync($"/admin/emergency-access/{inForce.Id}", Form(("Outcome", EmergencyAccessOutcomes.Appropriate), ("Note", Note), ("__RequestVerificationToken", Token(await first.Client.GetStringAsync("/inbox", Cancel)))), TestContext.Current.CancellationToken))
        {
            Assert.Contains("Not saved.</span> This access is still in force.", await ReadAsync(early));
        }

        Assert.Empty(await ReviewsAsync(apps, inForce.Id));

        // 5.
        using (var clinician = await Client(apps.OnPrem, twice.Session.Jar, followRedirects: false).GetAsync($"/admin/emergency-access/{twice.Id}", Cancel))
        {
            Assert.Equal("/denied", new Uri(Base, clinician.Headers.Location!).AbsolutePath);
        }

        using (var signedOut = await Client(apps.OnPrem, new CookieContainer(), followRedirects: false).GetAsync($"/admin/emergency-access/{twice.Id}", Cancel))
        {
            Assert.Equal("/sign-in", new Uri(Base, signedOut.Headers.Location!).AbsolutePath);
        }

        using var unknown = await first.Client.GetAsync($"/admin/emergency-access/{Guid.CreateVersion7()}", Cancel);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }
}
