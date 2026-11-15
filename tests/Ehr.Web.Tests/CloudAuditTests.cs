using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ehr.Data.Audit;
using Ehr.Web.Api.Audit;
using Ehr.Web.CloudAudit;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// The cloud app serves PHI reads from its replica and ships their audit events to the on-prem app (CloudAudit), and
// the on-prem app takes them only with the shared key. While the on-prem app can't save, the cloud app's pages say so.
public class CloudAuditTests(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_chart_read_off_site_comes_from_the_replica_and_reaches_the_audit_log_from_the_cloud()
    {
        var clinician = await apps.ClinicianAsync();
        var patient = await apps.InsertPatientAsync("Gale", "Offsite");
        // Signed in first: the home lists the panel, and that would be a read too.
        var signedIn = await CookieHeaderAsync(apps.Cloud, clinician);
        await apps.AssignAsync(clinician, patient);
        var forwarded = apps.Forwarded.Count;

        using var client = CreateClient(apps.Cloud, handleCookies: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/patients/{patient}");
        request.Headers.Add("Cookie", signedIn);
        using var response = await client.SendAsync(request, Cancel);

        var html = await ReadAsync(response);
        Assert.Contains("Offsite, Gale", html);
        Assert.DoesNotContain("Read-only", html);
        Assert.Empty(apps.Forwarded.Skip(forwarded));

        await apps.ShipCloudAuditAsync();
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient, AuditSources.Cloud));

        // Shipping again sends nothing new.
        await apps.ShipCloudAuditAsync();
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient));
    }

    [Fact]
    public async Task With_the_primary_database_down_off_site_pages_say_read_only_and_reads_stay_queued()
    {
        var clinician = await apps.ClinicianAsync();
        var patient = await apps.InsertPatientAsync("Rowan", "Primarydown");
        // Signed in first: the home lists the panel, and that would be a read too.
        var signedIn = await CookieHeaderAsync(apps.Cloud, clinician);
        await apps.AssignAsync(clinician, patient);
        apps.PrimaryDown = true;
        try
        {
            // The on-prem app answers the cloud app's check, but with 500: it can't save.
            Assert.False(await ShipperOf(apps.CloudPrimaryDrill).ShipAsync(Cancel));

            using var response = await SendAsync(apps.CloudPrimaryDrill, HttpMethod.Get, $"/patients/{patient}", signedIn);

            var html = await ReadAsync(response);
            Assert.Contains("Primarydown, Rowan", html);
            Assert.Contains("Read-only: the clinic's server can't be reached", html);
            var (queued, _) = apps.CloudPrimaryDrill.Services.GetRequiredService<CloudAuditQueue>().ReadPending(int.MaxValue);
            Assert.Contains(queued, e => e.Action == AuditActions.Read && e.PatientId == patient && e.ActorId == clinician.Id);
            Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.Read, actorId: clinician.Id, patientId: patient));
        }
        finally
        {
            apps.PrimaryDown = false;
        }
    }

    [Fact]
    public async Task A_save_answered_with_a_server_error_leaves_the_read_only_notice_as_it_was()
    {
        var signedIn = await CookieHeaderAsync(apps.Cloud, await apps.ClinicianAsync());
        var shipper = ShipperOf(apps.CloudPrimaryDrill);
        try
        {
            // One page with a bug, on an on-prem app that can otherwise save: the site doesn't turn read-only.
            apps.BrokenOnPremPage = QuickTexts;
            Assert.True(await shipper.ShipAsync(Cancel));
            using (var broken = await SendAsync(apps.CloudPrimaryDrill, HttpMethod.Post, $"{QuickTexts}?handler=Add", signedIn))
            {
                Assert.Equal(HttpStatusCode.InternalServerError, broken.StatusCode);
            }

            Assert.True(apps.CloudPrimaryDrill.Services.GetRequiredService<OnPremStatus>().IsReachable);
            using (var page = await SendAsync(apps.CloudPrimaryDrill, HttpMethod.Get, QuickTexts, signedIn))
            {
                Assert.DoesNotContain("Read-only", await ReadAsync(page));
            }

            // The primary down: a save's 500 doesn't take the notice away.
            apps.PrimaryDown = true;
            Assert.False(await shipper.ShipAsync(Cancel));
            using (var down = await SendAsync(apps.CloudPrimaryDrill, HttpMethod.Post, $"{QuickTexts}?handler=Add", signedIn))
            {
                Assert.Equal(HttpStatusCode.InternalServerError, down.StatusCode);
            }

            using (var page = await SendAsync(apps.CloudPrimaryDrill, HttpMethod.Get, QuickTexts, signedIn))
            {
                Assert.Contains("Read-only: the clinic's server can't be reached", await ReadAsync(page));
            }
        }
        finally
        {
            apps.PrimaryDown = false;
            apps.BrokenOnPremPage = null;
        }
    }

    [Fact]
    public async Task During_an_outage_an_answer_the_on_prem_app_still_gives_leaves_the_site_read_only()
    {
        var signedIn = await CookieHeaderAsync(apps.Cloud, await apps.ClinicianAsync());
        apps.PrimaryDown = true;
        try
        {
            Assert.False(await ShipperOf(apps.CloudPrimaryDrill).ShipAsync(Cancel));

            // Anyone on the internet: a POST without the key, which the cloud app forwards and the on-prem app refuses
            // without its database.
            using (var client = CreateClient(apps.CloudPrimaryDrill, handleCookies: false))
            using (var refused = await client.PostAsync(CloudAuditApi.Path, new StringContent("[]", Encoding.UTF8, "application/json"), Cancel))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            }

            Assert.False(apps.CloudPrimaryDrill.Services.GetRequiredService<OnPremStatus>().IsReachable);
            using var page = await SendAsync(apps.CloudPrimaryDrill, HttpMethod.Get, QuickTexts, signedIn);
            Assert.Contains("Read-only: the clinic's server can't be reached", await ReadAsync(page));
        }
        finally
        {
            apps.PrimaryDown = false;
        }
    }

    [Fact]
    public async Task The_on_prem_app_takes_events_only_with_the_shared_key()
    {
        var patient = Guid.CreateVersion7();
        var events = new[] { Event(patient) };

        using var none = await PostAsync(events, key: null);
        using var wrong = await PostAsync(events, key: "not-the-key-not-the-key-not-the-key");

        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.Read, patientId: patient));
    }

    [Fact]
    public async Task An_event_shipped_twice_is_recorded_once_and_always_as_from_the_cloud()
    {
        var patient = Guid.CreateVersion7();
        var shipped = Event(patient);
        shipped.Source = AuditSources.OnPrem;

        using var first = await PostAsync([shipped], WebApps.CloudAuditKey);
        using var again = await PostAsync([shipped], WebApps.CloudAuditKey);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.Read, patientId: patient, source: AuditSources.Cloud));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.Read, patientId: patient));
    }

    [Fact]
    public async Task An_event_with_an_unknown_action_is_refused()
    {
        var patient = Guid.CreateVersion7();
        var bad = Event(patient);
        bad.Action = "made_up";

        using var response = await PostAsync([bad], WebApps.CloudAuditKey);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await apps.AuditEventsAsync("made_up", patientId: patient));
    }

    [Fact]
    public async Task On_prem_pages_never_say_read_only()
    {
        var session = await SignInAsync(apps.OnPrem, await apps.ClinicianAsync());

        Assert.DoesNotContain("Read-only", await session.Client.GetStringAsync(QuickTexts, Cancel));
    }

    static AuditEvent Event(Guid patient) => new()
    {
        Id = Guid.CreateVersion7(),
        OccurredAt = DateTimeOffset.UtcNow,
        Action = AuditActions.Read,
        Source = AuditSources.Cloud,
        PatientId = patient,
        TableName = "patients",
        RowId = patient,
    };

    static CloudAuditShipper ShipperOf(WebApplicationFactory<Program> app) =>
        app.Services.GetRequiredService<CloudAuditShipper>();

    // A request from a signed-in browser, with its Cookie header; a post sends an empty form.
    static async Task<HttpResponseMessage> SendAsync(WebApplicationFactory<Program> app, HttpMethod method, string path, string cookie)
    {
        using var client = CreateClient(app, handleCookies: false);
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", cookie);
        if (method == HttpMethod.Post)
        {
            request.Content = Form();
        }

        return await client.SendAsync(request, Cancel);
    }

    async Task<HttpResponseMessage> PostAsync(AuditEvent[] events, string? key)
    {
        using var client = CreateClient(apps.OnPrem, handleCookies: false);
        using var request = new HttpRequestMessage(HttpMethod.Post, CloudAuditApi.Path)
        {
            Content = JsonContent.Create(events, options: JsonSerializerOptions.Web),
        };
        if (key is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        return await client.SendAsync(request, Cancel);
    }
}
