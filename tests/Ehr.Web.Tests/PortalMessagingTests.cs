using System.Net;
using Ehr.Data.Accounts;
using Ehr.Web.CloudAudit;
using Microsoft.Extensions.DependencyInjection;
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
        Assert.Contains("New 1 message from your care team.", home);

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
        Assert.Contains("No messages.", await portal.Client.GetStringAsync("/portal/messages", Cancel));

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

    // Under proxy access (FR-19; the expansion plan's steps 6 and 6b), which came after these pages were polished: a
    // proxy whose access leaves messages out is shown nothing of them, as the portal's own messages pages show nothing.
    [Fact]
    public async Task A_proxy_whose_access_leaves_messages_out_gets_no_messages_panel_count_or_question_about_a_result()
    {
        var (patient, proxy) = await ProxyWithUnreadMessageAsync("Dr. Portal Proxy Without Messages", "record");

        var html = await proxy.Client.GetStringAsync("/portal", Cancel);
        var home = Words(html);
        Assert.DoesNotContain("id=\"home-messages\"", html);
        Assert.DoesNotContain("href=\"/portal/messages", html);
        Assert.DoesNotContain("href=\"/portal/refills", html);
        Assert.DoesNotContain("new message", home);
        Assert.DoesNotContain("No new messages.", home);
        Assert.DoesNotContain("Ask for a refill", home);
        // The rest of the home is there, worded for someone else's record.
        Assert.Contains(">Test results</h2>", html);
        Assert.Contains("id=\"home-record\"", html);
        Assert.Contains("Visit summaries and this record", home);

        var results = await proxy.Client.GetStringAsync("/portal/results", Cancel);
        Assert.Contains("Hemoglobin A1c", results);
        Assert.DoesNotContain("Ask about this result", results);
        Assert.DoesNotContain("handler=About", results);

        // Posting the question anyway leads where the message form itself leads such a proxy, with nothing filled in.
        const string subject = "About the Hemoglobin A1c result from Nov 2, 2026";
        using var about = await PostFormAsync(proxy.Client, "/portal/messages/new?handler=About", "/portal/results", ("Subject", subject));
        Assert.Equal("/portal/messages", PathOf(about));
        var page = Words(await ReadAsync(about));
        Assert.Contains("Your access to this record doesn't include messages.", page);
        Assert.DoesNotContain(WebUtility.HtmlEncode(subject), page);
        Assert.Empty(await RowsAsync(apps, patient, "subject", "message_threads", "started_at, id", "subject LIKE 'About the%'"));
    }

    [Fact]
    public async Task A_proxy_whose_access_includes_messages_sees_the_count_and_asks_about_the_result_not_my_result()
    {
        var (_, proxy) = await ProxyWithUnreadMessageAsync("Dr. Portal Proxy With Messages", "record-and-messages");

        var html = await proxy.Client.GetStringAsync("/portal", Cancel);
        var home = Words(html);
        Assert.Contains("id=\"home-messages\"", html);
        Assert.Contains("Messages with the care team", home);
        Assert.Contains("New 1 message from the care team.", home);
        Assert.DoesNotContain("your care team", home);
        Assert.Contains("Open messages", home);

        const string subject = "About the Hemoglobin A1c result from Nov 2, 2026";
        var results = await proxy.Client.GetStringAsync("/portal/results", Cancel);
        Assert.Contains("Ask about this result", results);
        Assert.Contains(WebUtility.HtmlEncode(subject), results);
        Assert.DoesNotContain("About my ", results);

        using var about = await PostFormAsync(proxy.Client, "/portal/messages/new?handler=About", "/portal/results", ("Subject", subject));
        Assert.Equal("/portal/messages/new", PathOf(about));
        Assert.Contains($"value=\"{WebUtility.HtmlEncode(subject)}\"", await ReadAsync(about));
    }

    // UC 2: off-site, with the clinic's server out of reach, results are still read from the copy, and the question,
    // which would send something, isn't offered.
    [Fact]
    public async Task Off_site_with_the_clinics_server_out_of_reach_a_result_offers_no_question()
    {
        var patient = await apps.InsertPatientAsync("Synthetic", "Offsite Asker");
        await apps.InsertResultAsync(patient, "Hemoglobin A1c", "4548-4", 6.9m, "%", 4.0m, 5.6m, Nov2, Nov2);
        var (_, jar) = await PortalResultsTests.SignInPatientAsync(apps, apps.Cloud, patient);
        Assert.Contains("Ask about this result", await Client(apps.Cloud, jar).GetStringAsync("/portal/results", Cancel));
        Assert.False(await apps.CloudCutOff.Services.GetRequiredService<CloudAuditShipper>().ShipAsync(Cancel));

        var results = await Client(apps.CloudCutOff, jar).GetStringAsync("/portal/results", Cancel);

        Assert.Contains("Hemoglobin A1c", results);
        Assert.DoesNotContain("Ask about this result", results);
        Assert.DoesNotContain("handler=About", results);
    }

    // A patient with a released result and a message from their care team they haven't opened, and someone they gave
    // access of that scope to, signed in and viewing the patient's record.
    async Task<(Guid Patient, ProxyTests.Person Proxy)> ProxyWithUnreadMessageAsync(string clinicianName, string scope)
    {
        var patient = await ProxyTests.PersonAsync(apps, "Synthetic", "Shared Asker");
        var proxy = await ProxyTests.PersonAsync(apps, "Synthetic", "Asking Helper");
        var clinician = await apps.CreateStaffAsync(clinicianName, StaffRoles.Clinician);
        await apps.AssignAsync(clinician, patient.Patient);
        await apps.InsertResultAsync(patient.Patient, "Hemoglobin A1c", "4548-4", 6.9m, "%", 4.0m, 5.6m, Nov2, Nov2);
        var staff = await SignInAsync(apps.OnPrem, clinician);
        var page = $"/patients/{patient.Patient}/messages/new";
        using var sent = await PostFormAsync(staff.Client, page, page, ("Subject", "Synthetic: your results"), ("Body", "Synthetic: please read."));
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        Assert.Contains("New 1 message from your care team.", Words(await patient.Client.GetStringAsync("/portal", Cancel)));

        await ProxyTests.GiveAsync(patient, proxy.Email, scope);
        await ProxyTests.ViewAsync(proxy, await ProxyTests.GrantIdAsync(apps, patient.Patient));
        return (patient.Patient, proxy);
    }
}
