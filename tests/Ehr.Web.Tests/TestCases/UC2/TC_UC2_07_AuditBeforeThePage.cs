using System.Net;
using Ehr.Data.Audit;
using Ehr.Web.CloudAudit;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.NoteAddendumTests;
using static Ehr.Web.Tests.OrderTests;

namespace Ehr.Web.Tests.TestCases.UC2;

// Test Case ID: TC-UC2-07
// Purpose / scenario: Off-site, a page's audit events are written to the cloud app's queue together, once, before the
//   page is sent; and when they can't be written the page is not sent at all.
// Type: Functional (exceptional condition; and the successful flow's audit, continued)
// Requirements: FR-14, FR-27, NFR-03
// Use case: UC 2, Read records during an on-site outage
// Preconditions: The clinician is signed in with the patient on their panel and has signed a note for them. The
//   off-site app can't reach the clinic's server. In one run its audit queue is the real one, watched; in the other
//   it fails every write, as a full disk would.
// Inputs and system conditions: GET /patients/{id} and GET /patients/{id}/notes/{note} on the off-site app.
// Test data: A patient "Orderable, Synthetic" with one signed note.
// Steps:
//   1. Off-site, open the chart, then the signed note.
//   2. Off-site with the failing queue, open the chart, then the signed note.
// Expected result: Step 1: each page is shown; the chart's two events (the patient's read and the note's) reach the
//   queue in one write, and so do the note page's two, and both are in the queue afterwards; a HEAD request and the
//   search's partial make one write each; every write is made before the server has begun to send the response. Step 2: neither page is
//   shown: each is answered with the error page, with none of the patient's name or the note's text, and nothing is
//   in the queue.
// Creator: Tahseen Arnan
public class TC_UC2_07_AuditBeforeThePage(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    sealed class Watched(IDurableAuditQueue? queue) : IDurableAuditQueue
    {
        public List<string[]> Writes { get; } = [];

        // Whether the server had begun sending the response at each write, as the outermost step of the app sees it.
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

    // Before everything else in the app: tells the watched queue how to ask whether this request's response has begun.
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
    public async Task Off_site_a_pages_audit_events_are_written_together_before_it_is_sent_or_it_is_not_sent()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        var note = await SignedNoteAsync(session.Client, patient);

        var real = apps.CloudCutOff.Services.GetRequiredService<CloudAuditQueue>();
        var watched = new Watched(real);
        await using var cloud = apps.CloudCutOff.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IDurableAuditQueue>(watched);
            services.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter>(_ => new Outermost(watched));
        }));
        var client = Client(cloud, session.Jar);
        var before = real.ReadPending(int.MaxValue).Events.Count;

        var chart = await client.GetStringAsync($"/patients/{patient}", Cancel);
        Assert.Contains("Orderable, Synthetic", chart);
        Assert.Equal([["read patients", "read clinical_notes"]], watched.Writes);
        watched.Writes.Clear();
        var page = await client.GetStringAsync(NotePath(patient, note), Cancel);
        Assert.Contains(History, page);
        Assert.Equal([["read patients", "read clinical_notes"]], watched.Writes);
        Assert.Equal(before + 4, real.ReadPending(int.MaxValue).Events.Count);

        // A request for the headers alone, and the search's partial as the page's script asks for it: one write each.
        watched.Writes.Clear();
        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/patients/{patient}"), Cancel);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        using var typed = new HttpRequestMessage(HttpMethod.Get, "/patients?handler=Results&name=Orderable");
        typed.Headers.Add("HX-Request", "true");
        using var partial = await client.SendAsync(typed, Cancel);
        Assert.Contains("Orderable", await partial.Content.ReadAsStringAsync(Cancel));
        Assert.Equal(2, watched.Writes.Count);
        // Every write of the four requests was made before the server had begun to send that request's response.
        Assert.Equal(4, watched.Started.Count);
        Assert.All(watched.Started, started => Assert.False(started));
        var written = real.ReadPending(int.MaxValue).Events.Count;

        // The same two pages when the queue can't be written.
        await using var broken = apps.CloudCutOff.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IDurableAuditQueue>(new Watched(null))));
        var unlucky = Client(broken, session.Jar);
        foreach (var address in new[] { $"/patients/{patient}", NotePath(patient, note) })
        {
            using var refused = await unlucky.GetAsync(address, Cancel);
            var body = await refused.Content.ReadAsStringAsync(Cancel);
            Assert.Equal(HttpStatusCode.InternalServerError, refused.StatusCode);
            Assert.DoesNotContain("Orderable", body);
            Assert.DoesNotContain("cough for two days", body);
        }

        Assert.Equal(written, real.ReadPending(int.MaxValue).Events.Count);
    }
}
