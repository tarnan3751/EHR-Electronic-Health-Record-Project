using System.Net;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.MessageTests;

namespace Ehr.Web.Tests;

// The portal's side of secure messaging, polished in PR 3 (Simar Rekhi, UC 6): the home page says how many messages
// from the care team are new, and a result can start a message about it (A3's UC 6, step 3 alternative). Supporting
// tests, not numbered test cases.
public class PortalMessagingTests(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    static readonly DateTimeOffset Nov2 = new(2026, 11, 2, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_home_page_counts_new_messages_from_the_care_team_until_they_are_opened()
    {
        var (_, patient, staff, portal) = await CareAsync(apps, "Dr. Portal Home Count");
        Assert.Contains("No new messages.", Words(await portal.Client.GetStringAsync("/portal", Cancel)));

        var thread = await StartAsync(portal.Client, "Synthetic: a question", "Synthetic: is this normal?");
        // The patient's own message isn't new to them.
        Assert.Contains("No new messages.", Words(await portal.Client.GetStringAsync("/portal", Cancel)));

        using var reply = await PostFormAsync(staff.Client, $"/patients/{patient}/messages/{thread}", $"/patients/{patient}/messages/{thread}",
            ("Body", "Synthetic: yes, nothing to worry about."));
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        var home = Words(await portal.Client.GetStringAsync("/portal", Cancel));
        Assert.Contains("You have 1 new message from your care team.", home);

        await portal.Client.GetStringAsync($"/portal/messages/{thread}", Cancel);
        Assert.Contains("No new messages.", Words(await portal.Client.GetStringAsync("/portal", Cancel)));
    }

    [Fact]
    public async Task A_result_starts_a_message_about_it_with_the_subject_filled_in_and_nothing_in_the_address()
    {
        var (_, patient, _, portal) = await CareAsync(apps, "Dr. Portal Result Question");
        await apps.InsertResultAsync(patient, "Hemoglobin A1c", "4548-4", 6.9m, "%", 4.0m, 5.6m, Nov2, Nov2);
        const string subject = "About my Hemoglobin A1c result from Nov 2, 2026";

        var results = await portal.Client.GetStringAsync("/portal/results", Cancel);
        Assert.Contains("action=\"/portal/messages/new?handler=About\"", results);
        Assert.Contains(WebUtility.HtmlEncode(subject), results);
        Assert.Contains("Ask about this result", results);

        using var about = await PostFormAsync(portal.Client, "/portal/messages/new?handler=About", "/portal/results", ("Subject", subject));
        var form = await ReadAsync(about);
        Assert.True(about.Headers.CacheControl?.NoStore);
        Assert.Contains($"value=\"{WebUtility.HtmlEncode(subject)}\"", form);
        // The message box has the focus, the subject doesn't, and sending goes to the page itself, not back to the
        // handler that only fills the form in.
        Assert.Matches("<textarea id=\"message-body\"[^>]*autofocus", form);
        Assert.DoesNotMatch("<input id=\"message-subject\"[^>]*autofocus", form);
        Assert.Matches("<form(?=[^>]*class=\"sc-form\")[^>]*action=\"/portal/messages/new\"", form);
        // Nothing was saved by filling the form in.
        Assert.Contains("You have no messages.", await portal.Client.GetStringAsync("/portal/messages", Cancel));

        var thread = await StartAsync(portal.Client, subject, "Synthetic: should I change anything?");
        Assert.Contains(WebUtility.HtmlEncode(subject), await portal.Client.GetStringAsync($"/portal/messages/{thread}", Cancel));
    }

    [Fact]
    public async Task A_subject_longer_than_a_subject_may_be_is_cut_when_the_form_is_filled_in()
    {
        var (_, _, _, portal) = await CareAsync(apps, "Dr. Portal Long Subject");
        var subject = new string('a', Ehr.Domain.Messaging.SecureMessages.MaximumSubject + 50);

        using var about = await PostFormAsync(portal.Client, "/portal/messages/new?handler=About", "/portal/messages/new", ("Subject", subject));

        var form = await ReadAsync(about);
        Assert.Contains($"value=\"{new string('a', Ehr.Domain.Messaging.SecureMessages.MaximumSubject)}\"", form);
        Assert.DoesNotContain(new string('a', Ehr.Domain.Messaging.SecureMessages.MaximumSubject + 1), form);
    }
}
