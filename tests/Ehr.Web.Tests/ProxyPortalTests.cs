using System.Net;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.ProxyTests;

namespace Ehr.Web.Tests;

// Proxy access (FR-19, step 6) on the portal pages PR 3 polished, as merged in PR 4: the home page's panels, a result's
// "Ask about this result" and the messages pages, for a proxy whose grant does or doesn't include messages. Supporting
// tests, not numbered test cases.
public class ProxyPortalTests(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    static readonly DateTimeOffset Nov2 = new(2026, 11, 2, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_proxy_without_messages_gets_no_messages_panel_and_cannot_ask_about_a_result()
    {
        var patient = await PersonAsync(apps, "Synthetic", "Quiet");
        var proxy = await PersonAsync(apps, "Synthetic", "Onlooker");
        await apps.InsertResultAsync(patient.Patient, "Hemoglobin A1c", "4548-4", 6.9m, "%", 4.0m, 5.6m, Nov2, Nov2);
        await GiveAsync(patient, proxy.Email);

        var home = Words(await ViewAsync(proxy, await GrantIdAsync(apps, patient.Patient)));
        Assert.Contains("Test results", home);
        Assert.DoesNotContain("Your test results", home);
        Assert.DoesNotContain("Messages with", home);

        var results = await proxy.Client.GetStringAsync("/portal/results", Cancel);
        Assert.Contains("Hemoglobin A1c", results);
        Assert.DoesNotContain("Ask about this result", results);

        // Posted anyway, the result's question leads to the page that says messages aren't included.
        using var about = await PostFormAsync(proxy.Client, "/portal/messages/new?handler=About", "/portal/results",
            ("Subject", "About the Hemoglobin A1c result from Nov 2, 2026"));
        Assert.Equal("/portal/messages", PathOf(about));
        Assert.Contains("Your access to this record doesn't include messages.", Words(await ReadAsync(about)));
    }

    [Fact]
    public async Task A_proxy_with_messages_asks_about_the_result_and_the_pages_say_the_care_team()
    {
        var patient = await PersonAsync(apps, "Synthetic", "Sharing");
        var proxy = await PersonAsync(apps, "Synthetic", "Helper");
        await apps.InsertResultAsync(patient.Patient, "Ferritin", "2276-4", 12m, "ng/mL", 15m, 150m, Nov2, Nov2, note: "Synthetic: take the iron tablets.");
        await GiveAsync(patient, proxy.Email, "record-and-messages");

        var home = Words(await ViewAsync(proxy, await GrantIdAsync(apps, patient.Patient)));
        Assert.Contains("Messages with the care team", home);
        Assert.Contains("No new messages.", home);

        var results = await proxy.Client.GetStringAsync("/portal/results", Cancel);
        Assert.Contains("Ask about this result", results);
        Assert.Contains(WebUtility.HtmlEncode("About the Ferritin result from Nov 2, 2026"), results);
        Assert.DoesNotContain("About my ", results);
        Assert.Contains("Note from the care team:", Words(results));

        var messages = Words(await proxy.Client.GetStringAsync("/portal/messages", Cancel));
        Assert.Contains("New message", messages);
        Assert.Contains("No messages.", messages);
        Assert.DoesNotContain("your care team", messages);
    }
}
