using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ehr.Data.Audit;
using Ehr.Web.Api.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// The cloud app serves PHI reads from its replica and ships their audit events to the on-prem app (CloudAudit), and
// the on-prem app takes them only with the shared key.
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
