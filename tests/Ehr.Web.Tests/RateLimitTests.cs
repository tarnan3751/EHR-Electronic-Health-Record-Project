using System.Net;
using Ehr.Domain.Accounts;
using Ehr.Web.Email;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ehr.Web.Tests;

// Rate limits (issue #32): the anonymous account POSTs are limited per socket address, whatever X-Forwarded-For says,
// and one address gets at most so many account emails an hour; the lab's door has a window for the lab and one for
// everyone else (step 12).
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

    // Step 19's final review: adding a passkey checks an authenticator code, so the passkeys page's posts share the
    // account posts' window, the sign-in's two among them, and over it get the same page.
    [Fact]
    public async Task The_passkeys_pages_posts_are_counted_with_the_account_posts()
    {
        await using var app = apps.OnPrem.WithWebHostBuilder(builder => builder.UseSetting("RateLimits:PermitLimit", "3"));
        var account = await apps.CreateStaffAsync("Dr. Limited Passkeys", Ehr.Data.Accounts.StaffRoles.Clinician);
        var session = await Browser.SignInAsync(app, account);
        var token = Browser.Token(await session.Client.GetStringAsync(PasskeyFlows.PasskeysPage, Cancel));
        (string, object)[] fields = [("name", "This Mac"), ("code", Ehr.Testing.Totp.WrongCode(account.AuthenticatorKey)), ("__RequestVerificationToken", token)];

        using var third = await session.Client.PostAsync($"{PasskeyFlows.PasskeysPage}?handler=Options", Browser.Form(fields), Cancel);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, third.StatusCode);

        using var refused = await session.Client.PostAsync($"{PasskeyFlows.PasskeysPage}?handler=Options", Browser.Form(fields), Cancel);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Contains("Too many attempts. Wait a few minutes and try again.", await refused.Content.ReadAsStringAsync(Cancel));
    }

    // Step 12, the review's probe: nobody without the lab's key can make the lab's requests wait or fail. Keyless
    // requests to the door, from the clinic's network or the internet, share a small window of their own and earn only
    // 401s and then 429s; the lab's, with its key, are counted in their own. Through the cloud site nothing reaches the
    // on-prem app at all.
    [Fact]
    public async Task A_keyless_flood_never_touches_the_labs_own_window()
    {
        await using var onPrem = apps.OnPrem.WithWebHostBuilder(builder => builder
            .UseSetting("RateLimits:LabKeylessRequestsPerMinute", "5").UseSetting("RateLimits:LabRequestsPerMinute", "3"));
        var reached = new CountingLink(onPrem.Server.CreateHandler());
        await using var cloud = apps.Cloud.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton(new OnPremApp("https://onprem.test", new HttpMessageInvoker(reached)))));
        var order = Guid.CreateVersion7();
        var collected = DateTimeOffset.UtcNow.AddMinutes(-5);

        // From the internet: as many keyless posts as anyone likes, none of them forwarded.
        using var internet = LabTests.Client(cloud, key: null);
        for (var i = 0; i < 20; i++)
        {
            using var posted = await LabTests.PostAsync(internet, order, collected, ("718-7", 9.1m, "g/dL"));
            Assert.Equal(HttpStatusCode.Unauthorized, posted.StatusCode);
        }

        Assert.Equal(0, reached.Count);

        // On the clinic's network: the keyless window fills, and its callers get 401, then 429.
        using var stranger = LabTests.Client(onPrem, key: "not-the-lab-key-but-long-enough-to-look-real");
        for (var i = 0; i < 5; i++)
        {
            using var refused = await LabTests.PostAsync(stranger, order, collected, ("718-7", 9.1m, "g/dL"));
            Assert.Equal("unauthorized", await LabTests.RefusalAsync(refused));
        }

        using (var limited = await stranger.GetAsync(LabTests.Orders, Cancel))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.Equal("too-many", await LabTests.RefusalAsync(limited));
            Assert.Equal("no-store", limited.Headers.CacheControl?.ToString());
            Assert.Equal("60", limited.Headers.RetryAfter?.ToString());
        }

        // The lab, with its key, is untouched: its own window, which only it fills.
        using var lab = LabTests.Client(onPrem);
        for (var i = 0; i < 3; i++)
        {
            using var asked = await lab.GetAsync(LabTests.Orders, Cancel);
            Assert.Equal(HttpStatusCode.OK, asked.StatusCode);
        }

        using var over = await lab.GetAsync(LabTests.Orders, Cancel);
        Assert.Equal("too-many", await LabTests.RefusalAsync(over));
    }

    // Counts what reaches the on-prem app under the lab's door. The cloud app's audit shipper crosses the same link and
    // isn't counted.
    sealed class CountingLink(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        int count;

        public int Count => count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.StartsWith("/api/lab", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref count);
            }

            return base.SendAsync(request, cancellationToken);
        }
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

    // Step 6c: enrollment codes are short enough to type (12 characters, about 60 bits), so guessing is the risk. Wrong
    // codes at registration are counted per client address like every other anonymous account POST, and past the
    // limit the answer is the "Too many attempts" page, before the code is even looked at. There is no account to
    // count against: none exists until a code is accepted, and a wrong code names no patient.
    [Fact]
    public async Task Wrong_enrollment_codes_are_limited_per_client_address()
    {
        await using var app = apps.OnPrem.WithWebHostBuilder(builder => builder.UseSetting("RateLimits:PermitLimit", "3"));
        var client = app.CreateClient();
        var form = await client.GetStringAsync("/portal/register", Cancel);
        FormUrlEncodedContent Guess(int i) => Browser.Form(("__RequestVerificationToken", Browser.Token(form)), ("Input.Code", $"AAAA-BBBB-CC{i:00}"),
            ("Input.Email", WebApps.NewEmail()), ("Input.Password", "a-patient-portal-password"), ("Input.Confirm", "a-patient-portal-password"));

        for (var i = 0; i < 3; i++)
        {
            using var wrong = await client.PostAsync("/portal/register", Guess(i), Cancel);
            Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
            Assert.Contains("That enrollment code is not valid or has expired.", await wrong.Content.ReadAsStringAsync(Cancel));
        }

        using var refused = await client.PostAsync("/portal/register", Guess(3), Cancel);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Contains("Too many attempts. Wait a few minutes and try again.", await refused.Content.ReadAsStringAsync(Cancel));
        // The invitation page is limited the same way.
        using var invitation = await client.PostAsync("/portal/invitation", new FormUrlEncodedContent([]), Cancel);
        Assert.Equal(HttpStatusCode.TooManyRequests, invitation.StatusCode);
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
            await sender.SendAsync(to, "Subject", "Body", new EmailAction("Open", "https://portal.example.com/portal"), Cancel);
        }

        Assert.Equal(3, inner.Sent.Count);
    }
}
