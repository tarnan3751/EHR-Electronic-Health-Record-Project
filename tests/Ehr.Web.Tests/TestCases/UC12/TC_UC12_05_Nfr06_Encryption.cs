using System.Text.RegularExpressions;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Testing;
using Ehr.Web.CloudAudit;
using Ehr.Web.Forwarding;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests.TestCases.UC12;

// Test Case ID: TC-UC12-05
// Purpose / scenario: Emergency access from off-site keeps the reason and the chart protected. The reason is saved by
//   the on-prem app, reached only over HTTPS; the chart's reads reach the on-prem audit log from the cloud; the chart is
//   never cached by the browser, so it isn't left on a shared computer's disk; and every database connection the stack
//   is set up with is verified TLS.
// Type: Non-functional (security: encryption in transit and at rest)
// Requirements: NFR-06, with FR-06 and FR-32
// Use case: UC 12, Open a chart through emergency access
// Preconditions: A clinician who has finished setup, and patients off their panel.
// Inputs and system conditions: Off-site (the cloud app), with the on-prem app reachable for steps 1 to 3 and cut off
//   for step 4. The files infra/compose/compose.yaml and the cloud app's on-prem setting (Ehr:OnPrem:Url).
// Test data: Clinician "Dr. Remote Cover", patients "Wren Faraway" and "Wren Readonly". Reason: "Off-site consult for
//   the on-call physician." On-prem addresses "https://onprem.example.com:8443" and "http://onprem.example.com:8080".
// Steps:
//   1. Sign in off-site, give the reason for the first patient, and open the chart.
//   2. Ship the cloud app's audit events.
//   3. Read the database connection strings and the on-prem address in compose.yaml.
//   4. Grant emergency access to the second patient on-site, cut the cloud app off from the on-prem app, and open the
//      chart off-site.
//   5. Set up the cloud app's link with each on-prem address.
// Expected result: Step 1's reason was posted on to the on-prem app, which recorded the break_glass event (source
//   onprem); the chart shows the emergency notice and is sent with Cache-Control: no-store. After step 2 the chart's
//   read is in the log with source cloud. Step 3: every connection string has SSL Mode=VerifyFull and the on-prem
//   address is https://. Step 4's read-only chart is also sent with no-store. Step 5: the https address is accepted and
//   the http one refused. The certificates, the live TLS sessions and the disk's encryption are the manual part,
//   TC_UC12_05_Nfr06_Encryption.md in this folder.
// Creator: Luke Garcia
public partial class TC_UC12_05_Nfr06_Encryption(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Off_site_the_reason_is_saved_on_prem_the_read_reaches_the_log_and_the_chart_is_not_cached()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Remote Cover", StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Wren", "Faraway");
        var session = await SignInAsync(apps.Cloud, clinician);
        var forwarded = apps.Forwarded.Count;

        using var opened = await PostFormAsync(session.Client, $"/patients/{patient}/reason", $"/patients/{patient}/reason",
            ("Reason", "Off-site consult for the on-call physician."));

        Assert.Contains("Emergency access. This isn't one of your patients.", Words(await ReadAsync(opened)));
        Assert.True(opened.Headers.CacheControl?.NoStore);
        Assert.Contains($"POST /patients/{patient}/reason", apps.Forwarded.Skip(forwarded));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.BreakGlass, clinician.Id, patient, AuditSources.OnPrem));

        await apps.ShipCloudAuditAsync();
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient, AuditSources.Cloud));
    }

    [Fact]
    public async Task The_read_only_chart_under_emergency_access_is_not_cached_either()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Remote Readonly", StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Wren", "Readonly");
        var session = await SignInAsync(apps.OnPrem, clinician);
        using (var granted = await PostFormAsync(session.Client, $"/patients/{patient}/reason", $"/patients/{patient}/reason", ("Reason", "Granted before the outage.")))
        {
            await ReadAsync(granted);
        }

        // The cloud app finds out within a couple of seconds; this is that check, now.
        Assert.False(await apps.CloudCutOff.Services.GetRequiredService<CloudAuditShipper>().ShipAsync(Cancel));
        using var chart = await Client(apps.CloudCutOff, session.Jar).GetAsync($"/patients/{patient}", Cancel);

        var html = await ReadAsync(chart);
        Assert.Contains("Readonly, Wren", html);
        Assert.Contains("can't be changed until the clinic's server is back", html);
        Assert.True(chart.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public void The_stack_connects_to_its_databases_and_to_the_on_prem_app_only_over_verified_tls()
    {
        var compose = File.ReadAllText(RepositoryRoot.Combine("infra/compose/compose.yaml"));

        var connections = ConnectionString().Matches(compose).Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(connections);
        Assert.All(connections, connection => Assert.Contains("SSL Mode=VerifyFull", connection));
        Assert.All(OnPremUrl().Matches(compose).Select(m => m.Groups[1].Value), url => Assert.StartsWith("https://", url));
        Assert.Matches(OnPremUrl(), compose);
    }

    [Fact]
    public void The_cloud_app_refuses_an_on_prem_address_that_is_not_https()
    {
        Assert.Equal("https://onprem.example.com:8443", OnPremApp.FromConfiguration(OnPrem("https://onprem.example.com:8443")).Url);

        var refused = Assert.Throws<InvalidOperationException>(() => OnPremApp.FromConfiguration(OnPrem("http://onprem.example.com:8080")));
        Assert.Contains("must be an https:// address", refused.Message);
    }

    static IConfiguration OnPrem(string url) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Ehr:OnPrem:Url"] = url }).Build();

    [GeneratedRegex(@"ConnectionStrings__Ehr:\s*""([^""]+)""")]
    private static partial Regex ConnectionString();

    [GeneratedRegex(@"Ehr__OnPrem__Url:\s*(\S+)")]
    private static partial Regex OnPremUrl();
}
