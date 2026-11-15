using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ehr.Data.Audit;
using Ehr.Web.Api.Audit;
using Ehr.Web.CloudAudit;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
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
    public async Task An_off_site_search_queues_one_read_per_patient_listed_and_ships_them_from_the_cloud()
    {
        var clinician = await apps.ClinicianAsync();
        var family = "Batch" + Guid.NewGuid().ToString("N")[..8];
        Guid[] patients =
            [await apps.InsertPatientAsync("Ada", family), await apps.InsertPatientAsync("Bo", family), await apps.InsertPatientAsync("Cy", family)];
        var signedIn = await CookieHeaderAsync(apps.Cloud, clinician);
        // Held in the queue until the on-prem app can take them, so what was queued can be counted.
        apps.PrimaryDown = true;
        try
        {
            using var response = await SendAsync(apps.CloudPrimaryDrill, HttpMethod.Get, $"/patients?q={family}", signedIn);

            var html = await ReadAsync(response);
            Assert.All(patients, patient => Assert.Contains($"/patients/{patient}", html));
            var (queued, _) = apps.CloudPrimaryDrill.Services.GetRequiredService<CloudAuditQueue>().ReadPending(int.MaxValue);
            var reads = queued.Where(e => e.PatientId is { } id && patients.Contains(id)).ToList();
            Assert.Equal(patients.Order(), reads.Select(e => e.PatientId!.Value).Order());
            Assert.All(reads, e => Assert.Equal((AuditActions.Read, clinician.Id), (e.Action, e.ActorId)));

            apps.PrimaryDown = false;
            Assert.True(await ShipperOf(apps.CloudPrimaryDrill).ShipAsync(Cancel));
            foreach (var patient in patients)
            {
                Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient, AuditSources.Cloud));
            }
        }
        finally
        {
            apps.PrimaryDown = false;
        }
    }

    // UC 2, step 4.
    [Fact]
    public async Task The_read_only_notice_goes_once_the_on_prem_app_is_back_and_the_reads_arrive()
    {
        var clinician = await apps.ClinicianAsync();
        var patient = await apps.InsertPatientAsync("Sky", "Comeback");
        var signedIn = await CookieHeaderAsync(apps.Cloud, clinician);
        await apps.AssignAsync(clinician, patient);
        var shipper = ShipperOf(apps.CloudPrimaryDrill);
        try
        {
            apps.PrimaryDown = true;
            Assert.False(await shipper.ShipAsync(Cancel));
            using (var during = await SendAsync(apps.CloudPrimaryDrill, HttpMethod.Get, $"/patients/{patient}", signedIn))
            {
                var html = await ReadAsync(during);
                Assert.Contains("Read-only: the clinic's server can't be reached. You can look at records, but the newest changes may not appear yet", html);
                Assert.DoesNotContain("handler=Unassign", html);
            }

            apps.PrimaryDown = false;
            Assert.True(await shipper.ShipAsync(Cancel));
            // The read made during the outage is on-prem now, as from the cloud.
            Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient, AuditSources.Cloud));

            using var after = await SendAsync(apps.CloudPrimaryDrill, HttpMethod.Get, $"/patients/{patient}", signedIn);
            var back = await ReadAsync(after);
            Assert.DoesNotContain("Read-only", back);
            Assert.Contains("handler=Unassign", back);
            Assert.Contains("handler=Enroll", back);
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
        // The right key, where it used to go: no longer taken.
        using var bearer = await PostAsync(events, WebApps.CloudAuditKey, asBearer: true);

        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, bearer.StatusCode);
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.Read, patientId: patient));
    }

    // The cloud app doesn't pass the key on, so a leaked key can't be used from the internet, only from the cloud box or
    // the clinic's network.
    [Fact]
    public async Task The_cloud_app_does_not_forward_the_key()
    {
        var shipped = Event(Guid.CreateVersion7());

        using var response = await PostAsync([shipped], WebApps.CloudAuditKey, via: apps.Cloud);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(await RecordedAsync(shipped.Id));
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

    // Each is something the cloud app never records, and the audit log keeps whatever it is given.
    [Theory]
    [InlineData("a sign-in")]
    [InlineData("emergency access")]
    [InlineData("no actor")]
    [InlineData("no patient")]
    [InlineData("another row")]
    [InlineData("a reason")]
    [InlineData("details")]
    [InlineData("an hour ahead")]
    [InlineData("null")]
    [InlineData("a sign-out with a table")]
    [InlineData("a sign-out with a row")]
    [InlineData("a sign-out with details")]
    [InlineData("a sign-out with no actor")]
    [InlineData("a sign-out of an empty patient")]
    public async Task Only_reads_of_one_patient_by_a_signed_in_account_are_taken(string what)
    {
        var good = Event(Guid.CreateVersion7());
        var bad = Event(Guid.CreateVersion7());
        switch (what)
        {
            case "a sign-in":
                bad.Action = AuditActions.SignIn;
                break;
            case "emergency access":
                bad.Action = AuditActions.BreakGlass;
                break;
            case "no actor":
                bad.ActorId = null;
                break;
            case "no patient":
                bad.PatientId = null;
                break;
            case "another row":
                bad.RowId = Guid.CreateVersion7();
                break;
            case "a reason":
                bad.Reason = "Covering the night shift";
                break;
            case "details":
                bad.Details = "{}";
                break;
            case "an hour ahead":
                bad.OccurredAt = DateTimeOffset.UtcNow.AddHours(1);
                break;
            case "a sign-out with a table":
                bad = SignOutEvent();
                bad.TableName = "staff_users";
                break;
            case "a sign-out with a row":
                bad = SignOutEvent();
                bad.RowId = Guid.CreateVersion7();
                break;
            case "a sign-out with details":
                bad = SignOutEvent();
                bad.Details = "{}";
                break;
            case "a sign-out with no actor":
                bad = SignOutEvent();
                bad.ActorId = null;
                break;
            case "a sign-out of an empty patient":
                bad = SignOutEvent(Guid.Empty);
                break;
        }

        using var response = await PostAsync(what == "null" ? [good, null] : [good, bad], WebApps.CloudAuditKey);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(await RecordedAsync(good.Id));
        Assert.False(await RecordedAsync(bad.Id));
        // A clock that runs ahead is told apart, within what the cloud app's log keeps of the answer.
        var answer = await response.Content.ReadAsStringAsync(Cancel);
        Assert.Contains(
            what == "an hour ahead" ? "more than 5 minutes ahead of this server's clock" : "each a read of one patient",
            answer[..Math.Min(answer.Length, CloudAuditShipper.MaxAnswerLength)]);
    }

    [Fact]
    public async Task Sign_outs_as_the_cloud_app_records_them_are_taken()
    {
        var staff = SignOutEvent();
        var patient = SignOutEvent(Guid.CreateVersion7());

        using var response = await PostAsync([staff, patient], WebApps.CloudAuditKey);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(await RecordedAsync(staff.Id));
        Assert.True(await RecordedAsync(patient.Id));
    }

    [Fact]
    public async Task A_read_queued_through_a_long_outage_is_taken()
    {
        var old = Event(Guid.CreateVersion7());
        old.OccurredAt = DateTimeOffset.UtcNow.AddDays(-3);

        using var response = await PostAsync([old], WebApps.CloudAuditKey);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(await RecordedAsync(old.Id));
    }

    [Fact]
    public async Task Without_the_key_the_body_is_not_read()
    {
        using var client = CreateClient(apps.OnPrem, handleCookies: false);

        using var response = await client.PostAsync(CloudAuditApi.Path, new StringContent("[{\"id\":", Encoding.UTF8, "application/json"), Cancel);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task With_the_key_a_malformed_body_is_refused()
    {
        using var client = CreateClient(apps.OnPrem, handleCookies: false);
        using var request = new HttpRequestMessage(HttpMethod.Post, CloudAuditApi.Path)
        {
            Content = new StringContent("[{\"id\":", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(CloudAuditApi.KeyHeader, WebApps.CloudAuditKey);

        using var response = await client.SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // The server enforces it as it reads; the in-memory test server doesn't, so this checks what the endpoint declares.
    [Fact]
    public void The_import_takes_at_most_a_megabyte()
    {
        var endpoint = apps.OnPrem.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == CloudAuditApi.Path);

        Assert.Equal(1024 * 1024, endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize);
    }

    [Fact]
    public async Task On_prem_pages_never_say_read_only()
    {
        var session = await SignInAsync(apps.OnPrem, await apps.ClinicianAsync());

        Assert.DoesNotContain("Read-only", await session.Client.GetStringAsync(QuickTexts, Cancel));
    }

    // A sign-out as StaffSignIn records one; PatientSignIn's carries the patient too.
    static AuditEvent SignOutEvent(Guid? patient = null) => new()
    {
        Id = Guid.CreateVersion7(),
        OccurredAt = DateTimeOffset.UtcNow,
        Action = AuditActions.SignOut,
        Source = AuditSources.Cloud,
        ActorId = Guid.CreateVersion7(),
        PatientId = patient,
    };

    // A read as the PHI reader records one.
    static AuditEvent Event(Guid patient) => new()
    {
        Id = Guid.CreateVersion7(),
        OccurredAt = DateTimeOffset.UtcNow,
        Action = AuditActions.Read,
        Source = AuditSources.Cloud,
        ActorId = Guid.CreateVersion7(),
        PatientId = patient,
        TableName = "patients",
        RowId = patient,
    };

    // Whether the event with this ID is in the audit log, read as the superuser.
    async Task<bool> RecordedAsync(Guid id)
    {
        await using var connection = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM ehr.audit_events WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        return (long)(await command.ExecuteScalarAsync(Cancel))! > 0;
    }

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

    // To the on-prem app's import, with the key in its header or as a bearer token, straight or through another app.
    async Task<HttpResponseMessage> PostAsync(AuditEvent?[] events, string? key, bool asBearer = false, WebApplicationFactory<Program>? via = null)
    {
        using var client = CreateClient(via ?? apps.OnPrem, handleCookies: false);
        using var request = new HttpRequestMessage(HttpMethod.Post, CloudAuditApi.Path)
        {
            Content = JsonContent.Create(events, options: JsonSerializerOptions.Web),
        };
        if (key is not null && asBearer)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
        else if (key is not null)
        {
            request.Headers.Add(CloudAuditApi.KeyHeader, key);
        }

        return await client.SendAsync(request, Cancel);
    }
}
