using System.Net;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.NoteAddendumTests;
using static Ehr.Web.Tests.OrderTests;

namespace Ehr.Web.Tests.TestCases.UC14;

// Test Case ID: TC-UC14-09
// Purpose / scenario: An addendum with nothing in it, or too long, is not signed and the form says why; a clinician
//   without the patient on their panel can read the note under emergency access but is not offered the form and can't
//   sign an addendum.
// Type: Functional (invalid input; alternative flow)
// Requirements: FR-31, FR-02, proposed FR-49
// Use case: UC 14, Review and sign a clinical note
// Preconditions: The clinician is signed in on the on-site app with the patient on their panel and has signed a note.
//   A covering clinician, without the patient on their panel, has opened the chart with a reason.
// Inputs and system conditions: POST /patients/{id}/notes/{note}/addendum; GET and POST as the covering clinician.
// Test data: Only spaces; 16,001 characters.
// Steps:
//   1. As the clinician, sign an addendum of only spaces, then one of 16,001 characters.
//   2. As the covering clinician, open the note, open the addendum page, and post an addendum to it.
// Expected result: Step 1: each is answered 422 with "Not signed." and "Write the addendum before signing it." or
//   "An addendum can be up to 16,000 characters."; nothing is saved. Step 2: the note is shown with "To sign an addendum, add
//   this patient to My patients." and no link; the addendum page and the post both send them back to the note;
//   nothing is saved.
// Creator: Tahseen Arnan
public class TC_UC14_09_AddendumInvalidAndRefused(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_empty_or_overlong_addendum_is_not_signed_and_a_clinician_off_the_panel_signs_none()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        var note = await SignedNoteAsync(session.Client, patient);

        foreach (var (text, message) in new[] { ("   ", "Write the addendum before signing it."), (new string('x', 16001), "An addendum can be up to 16,000 characters.") })
        {
            using var refused = await AddAsync(session.Client, patient, note, text);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            var page = Words(await refused.Content.ReadAsStringAsync(Cancel));
            Assert.Contains("Not signed. " + message, page);
            Assert.Contains(message, page);
        }

        var covering = await SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("Dr. Covering Notes", Ehr.Data.Accounts.StaffRoles.Clinician));
        using var opened = await PostFormAsync(covering.Client, $"/patients/{patient}/reason", $"/patients/{patient}/reason", ("Reason", "Covering the night shift"));
        var token = Token(await ReadAsync(opened));
        var read = await covering.Client.GetStringAsync(NotePath(patient, note), Cancel);
        Assert.Contains(Subjective, read);
        Assert.Contains("To sign an addendum, add this patient to My patients.", read);
        Assert.DoesNotContain("data-add-addendum", read);
        var noRedirects = Client(apps.OnPrem, covering.Jar, followRedirects: false);
        using var form = await noRedirects.GetAsync(AddendumPath(patient, note), Cancel);
        Assert.Equal(HttpStatusCode.Redirect, form.StatusCode);
        Assert.Equal(NotePath(patient, note), form.Headers.Location?.OriginalString);
        using var posted = await noRedirects.PostAsync(AddendumPath(patient, note), Form(("__RequestVerificationToken", token), ("Text", "Synthetic addendum from off the panel.")), Cancel);
        Assert.Equal(HttpStatusCode.Redirect, posted.StatusCode);

        Assert.Empty(await AddendaAsync(apps, patient));
    }

    // A signature that reached the server and is sent again with "Sensitive" ticked can't change the signed note: the
    // answer says so instead of sending the clinician to a note that isn't what they asked for.
    [Fact]
    public async Task A_retried_signature_cannot_change_whether_the_signed_note_is_sensitive()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        using var created = await PostFormAsync(session.Client, $"/patients/{patient}?handler=NewNote", $"/patients/{patient}");
        var (path, token) = (PathOf(created), Token(await ReadAsync(created)));
        async Task<HttpResponseMessage> PostAsync(string target, long version, params (string Name, object Value)[] extra)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, target);
            request.Headers.Add("HX-Request", "true");
            request.Content = Form([("__RequestVerificationToken", token), ("Version", version), ("Subjective", Subjective), ("Objective", Objective),
                ("Assessment", "Synthetic assessment."), ("Plan", "Synthetic plan."), .. extra]);
            return await session.Client.SendAsync(request, Cancel);
        }

        using var review = await PostAsync(path + "?handler=Review", 1);
        using var signed = await PostAsync(path + "/confirm", 2, ("Confirmed", true));
        Assert.Equal(HttpStatusCode.OK, signed.StatusCode);
        using var same = await PostAsync(path + "/confirm", 2, ("Confirmed", true));
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        using var changed = await PostAsync(path + "/confirm", 2, ("Confirmed", true), ("Sensitive", true));
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Contains("already signed, and not as sensitive", await changed.Content.ReadAsStringAsync(Cancel));
        Assert.Equal(["false"], await ProxyTests.QueryAsync(apps, "SELECT sensitive::text FROM ehr.clinical_notes WHERE patient_id = @patient", patient));
    }
}
