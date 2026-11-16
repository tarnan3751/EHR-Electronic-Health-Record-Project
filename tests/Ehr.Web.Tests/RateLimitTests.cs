using System.Net;
using Ehr.Web.Email;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Ehr.Web.Tests;

// Rate limits (issue #32): the anonymous account POSTs are limited per socket address, whatever X-Forwarded-For says,
// and one address gets at most so many account emails an hour.
public class RateLimitTests(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Account_posts_over_the_limit_get_a_page_saying_so()
    {
        await using var app = apps.OnPrem.WithWebHostBuilder(builder => builder.UseSetting("RateLimits:PermitLimit", "2"));
        var client = app.CreateClient();

        for (var i = 0; i < 2; i++)
        {
            using var allowed = await client.PostAsync("/portal/sign-in", new FormUrlEncodedContent([]), Cancel);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
        }

        using var refused = await client.PostAsync("/portal/sign-in", new FormUrlEncodedContent([]), Cancel);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("text/html", refused.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Too many attempts. Wait a few minutes and try again.", await refused.Content.ReadAsStringAsync(Cancel));

        // Reading the page isn't limited.
        using var page = await client.GetAsync("/portal/sign-in", Cancel);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
    }

    [Fact]
    public void X_forwarded_for_does_not_change_the_partition()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/portal/sign-in";
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.7";

        Assert.Equal("192.0.2.10", AccountRateLimits.Partition(context, new RateLimitOptions()).PartitionKey);
    }

    [Fact]
    public void Two_clients_are_limited_separately()
    {
        static string KeyFor(string address)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = "/portal/register";
            context.Connection.RemoteIpAddress = IPAddress.Parse(address);
            return AccountRateLimits.Partition(context, new RateLimitOptions()).PartitionKey;
        }

        Assert.NotEqual(KeyFor("192.0.2.10"), KeyFor("192.0.2.11"));
    }

    // The on-prem app doesn't count what the cloud app forwards, which the cloud app has limited already; anyone
    // else sending the header, or X-Forwarded-For, is counted by their own address.
    [Fact]
    public async Task Requests_the_cloud_app_forwards_are_not_limited_on_prem_and_a_forged_header_is()
    {
        await using var app = apps.OnPrem.WithWebHostBuilder(builder => builder.UseSetting("RateLimits:PermitLimit", "1"));
        var forwarded = app.CreateClient();
        forwarded.DefaultRequestHeaders.Add(AccountRateLimits.ForwardedHeader, AccountRateLimits.ForwardedProof(WebApps.CloudAuditKey));
        for (var i = 0; i < 5; i++)
        {
            using var allowed = await forwarded.PostAsync("/portal/sign-in", new FormUrlEncodedContent([]), Cancel);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
        }

        var forged = app.CreateClient();
        forged.DefaultRequestHeaders.Add(AccountRateLimits.ForwardedHeader, "not-the-proof");
        forged.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.7");
        using var first = await forged.PostAsync("/portal/sign-in", new FormUrlEncodedContent([]), Cancel);
        using var second = await forged.PostAsync("/portal/sign-in", new FormUrlEncodedContent([]), Cancel);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    [Fact]
    public async Task An_address_gets_three_emails_an_hour_then_none()
    {
        var inner = new RecordingEmailSender();
        var sender = new CappedEmailSender(inner, 3, TimeProvider.System);

        foreach (var to in new[] { "capped@example.test", "Capped@example.test", "CAPPED@example.test", "capped@example.test" })
        {
            await sender.SendAsync(to, "Subject", "Body", Cancel);
        }

        Assert.Equal(3, inner.Sent.Count);
    }
}
