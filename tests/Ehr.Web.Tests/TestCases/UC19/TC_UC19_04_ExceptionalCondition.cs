using System.Net;
using Ehr.Data.Audit;
using Ehr.Web.CloudAudit;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.FhirRequests;
using R4 = Hl7.Fhir.Model;

namespace Ehr.Web.Tests.TestCases.UC19;

// Test Case ID: TC-UC19-04
// Purpose / scenario: Off-site, during an outage of the clinic's server, the cloud app serves the same reads from its
//   copy, and records each in its audit queue before any of the answer is sent; when it can't, it sends none of it. A
//   write is never the cloud app's: it is forwarded to the clinic's server, which refuses it, or, with that server out of
//   reach, it is answered "Not saved".
// Type: Functional (exceptional condition)
// Requirements: proposed FR-53, with NFR-03 and FR-32
// Use case: UC 19, Read a patient's record through the FHIR API (and UC 2, Read records during an on-site outage)
// Preconditions: As TC-UC19-01, signed in on the on-site app; the same session's cookies used off-site. In one run
//   the cloud app's audit queue is the real one, watched; in another it fails every write, as a full disk would.
// Inputs and system conditions: GET /fhir/Patient/{id}, GET /fhir/Observation?patient={id} and GET
//   /fhir/Condition?patient={id}&show-sensitive=true on the cloud app cut off from the clinic's server; the same GET and
//   POST /fhir/Patient on the cloud app linked to it; POST /fhir/Patient on the cut-off cloud app.
// Test data: The patient of TC-UC19-01.
// Steps:
//   1. Off-site and cut off, read the patient, search the observations, and search the diagnoses asking for sensitive ones.
//   2. The same read with the failing queue.
//   3. With the link up, read the patient, and post a patient.
//   4. Cut off, post a patient.
// Expected result: Step 1: each answer is valid R4, as on-site; the queue gets one write per request, "read patients",
//   "read patients" and "read_sensitive patients", each before the response began, and holds them afterwards; nothing
//   is in the database. Step 2: 500, an OperationOutcome with none of the patient's details, and nothing more in the
//   queue. Step 3: the read isn't forwarded; the post is, and comes back as the clinic's server's 405 OperationOutcome.
//   Step 4: 503, the cloud app's plain "Not saved" answer, which isn't FHIR: the write wasn't the cloud app's to refuse.
// Creator: Tahseen Arnan
public class TC_UC19_04_ExceptionalCondition(WebApps apps) : IClassFixture<WebApps>
{
    sealed class Watched(IDurableAuditQueue? queue) : IDurableAuditQueue
    {
        public List<string[]> Writes { get; } = [];

        public List<bool> Started { get; } = [];

        public Func<bool> HasStarted { get; set; } = () => false;

        public void Enqueue(IReadOnlyCollection<AuditEvent> auditEvents)
        {
            if (queue is null)
            {
                throw new IOException("Synthetic: the disk is full.");
            }

            queue.Enqueue(auditEvents);
            Writes.Add([.. auditEvents.Select(e => $"{e.Action} {e.TableName}")]);
            Started.Add(HasStarted());
        }
    }

    // Before everything else in the app: lets the watched queue ask whether this request's response has begun.
    sealed class Outermost(Watched watched) : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
        {
            app.Use(following => async context =>
            {
                watched.HasStarted = () => context.Response.HasStarted;
                await following(context);
            });
            next(app);
        };
    }

    [Fact]
    public async Task Off_site_reads_are_recorded_before_they_are_sent_or_not_sent_and_writes_are_never_the_cloud_apps()
    {
        var chart = await ChartAsync(apps, "Dr. Fhir Off-site");
        var before = await ReadsAsync(apps, chart.Clinician.Id, chart.Patient);

        // 1. Cut off, through the watched queue.
        var real = apps.CloudCutOff.Services.GetRequiredService<CloudAuditQueue>();
        var watched = new Watched(real);
        await using var cloud = apps.CloudCutOff.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IDurableAuditQueue>(watched);
            services.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter>(_ => new Outermost(watched));
        }));
        var offSite = Client(cloud, chart.Session.Jar);
        var queued = real.ReadPending(int.MaxValue).Events.Count;

        var patient = FhirConformance.Valid<R4.Patient>(Expect(await GetAsync(offSite, $"/fhir/Patient/{chart.Patient}"), HttpStatusCode.OK).Json);
        Assert.Equal(chart.Mrn, patient.Identifier.Single().Value);
        var (_, observations, _) = Searched<R4.Observation>(await GetAsync(offSite, $"/fhir/Observation?patient={chart.Patient}"));
        Assert.Equal(10, observations.Count);
        var (_, conditions, _) = Searched<R4.Condition>(await GetAsync(offSite, $"/fhir/Condition?patient={chart.Patient}&show-sensitive=true"));
        Assert.Equal(2, conditions.Count);
        Assert.Equal([["read patients"], ["read patients"], ["read_sensitive patients"]], watched.Writes);
        Assert.Equal([false, false, false], watched.Started);
        Assert.Equal(queued + 3, real.ReadPending(int.MaxValue).Events.Count);
        Assert.Equal(before, await ReadsAsync(apps, chart.Clinician.Id, chart.Patient));

        // 2. A queue that can't be written: nothing of the patient is sent.
        await using var broken = apps.CloudCutOff.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IDurableAuditQueue>(new Watched(null))));
        var failed = await GetAsync(Client(broken, chart.Session.Jar), $"/fhir/Patient/{chart.Patient}");
        Refused(failed, HttpStatusCode.InternalServerError, "exception");
        Assert.DoesNotContain("Fhirchart", failed.Json);
        Assert.DoesNotContain(chart.Mrn, failed.Json);
        Assert.Equal(queued + 3, real.ReadPending(int.MaxValue).Events.Count);

        // 3. With the link up: reads stay on the cloud app; a write goes to the clinic's server, which refuses it.
        var linked = Client(apps.Cloud, chart.Session.Jar);
        Expect(await GetAsync(linked, $"/fhir/Patient/{chart.Patient}"), HttpStatusCode.OK);
        Assert.DoesNotContain(apps.Forwarded, forwarded => forwarded.StartsWith("GET /fhir", StringComparison.Ordinal));
        var write = await SendAsync(linked, HttpMethod.Post, "/fhir/Patient");
        Refused(write, HttpStatusCode.MethodNotAllowed, "not-supported");
        Assert.Contains("POST /fhir/Patient", apps.Forwarded);

        // 4. Cut off, a write is the cloud app's "Not saved", as for every API.
        using var notSaved = await offSite.PostAsync("/fhir/Patient", new StringContent("""{"resourceType":"Patient"}""", System.Text.Encoding.UTF8, "application/fhir+json"), Cancel);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, notSaved.StatusCode);
        Assert.Equal("Not saved. The clinic's server can't be reached.", await notSaved.Content.ReadAsStringAsync(Cancel));
    }
}
