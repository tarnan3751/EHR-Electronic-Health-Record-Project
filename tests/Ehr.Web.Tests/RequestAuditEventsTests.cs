using System.Net;
using Ehr.Data.Audit;
using Ehr.Web.CloudAudit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ehr.Web.Tests;

// The expansion plan's step 10c: on the cloud site a request's audit events are written to the queue together, with
// one disk flush, before any of the response is sent, where each used to be written and flushed as it was recorded.
// The rule that must survive: nothing a read found is shown without its durable record, and a write that fails shows
// nothing. A small app with the real middleware and holder, and a queue that records what it was given and when.
public sealed class RequestAuditEventsTests
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    const string Found = "what the read found";

    sealed class RecordingQueue(bool fails = false) : IDurableAuditQueue
    {
        // Each write: how many events, and whether any of the response had started on its way by then.
        public List<(int Events, bool AfterPage)> Writes { get; } = [];

        // Whether the response has started on its way: set by the test app's outermost step.
        public Func<bool> Started { get; set; } = () => false;

        public void Enqueue(IReadOnlyCollection<AuditEvent> auditEvents)
        {
            if (fails)
            {
                throw new IOException("Synthetic: the disk is full.");
            }

            Writes.Add((auditEvents.Count, Started()));
        }
    }

    static AuditEvent Read(string table = "patients")
    {
        var patient = Guid.NewGuid();
        return new AuditEvent
        {
            Id = Guid.CreateVersion7(),
            OccurredAt = DateTimeOffset.UtcNow,
            ActorId = Guid.NewGuid(),
            Action = AuditActions.Read,
            Source = AuditSources.Cloud,
            PatientId = patient,
            TableName = table,
            RowId = table == "patients" ? patient : Guid.NewGuid(),
        };
    }

    // The middleware as the cloud app runs it: after something that answers a failure with an error page.
    static async Task<(HttpResponseMessage Response, string Body)> RequestAsync(RecordingQueue queue, Func<HttpContext, RequestAuditEvents, Task> page)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IDurableAuditQueue>(queue);
        builder.Services.AddScoped<RequestAuditEvents>();
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            queue.Started = () => context.Response.HasStarted;
            try
            {
                await next(context);
            }
            catch (Exception) when (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsync("The error page.");
            }
        });
        app.UseRouting();
        app.UseMiddleware<CloudAuditFlushMiddleware>();
        app.MapGet("/", context => page(context, context.RequestServices.GetRequiredService<RequestAuditEvents>()));
        await app.StartAsync(Cancel);
        using var client = app.GetTestClient();
        var response = await client.GetAsync("/", Cancel);
        return (response, await response.Content.ReadAsStringAsync(Cancel));
    }

    // The handler writes its page and flushes it, as far as it can tell.
    static async Task WritePageAsync(HttpContext context)
    {
        await context.Response.WriteAsync(Found);
        await context.Response.Body.FlushAsync();
    }

    [Fact]
    public async Task A_requests_events_are_written_once_together_before_any_of_the_page_is_sent()
    {
        var queue = new RecordingQueue();

        var (response, body) = await RequestAsync(queue, async (context, events) =>
        {
            // A chart with a signed note: the patient's read, then the note's, recorded apart.
            events.Enqueue([Read()]);
            events.Enqueue([Read("clinical_notes"), Read("clinical_notes")]);
            Assert.Empty(queue.Writes);
            await WritePageAsync(context);
            // The handler has written and flushed its page; none of it has left, and no event is written yet.
            Assert.False(context.Response.HasStarted);
            Assert.Empty(queue.Writes);
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Found, body);
        Assert.Equal([(3, false)], queue.Writes);
    }

    [Fact]
    public async Task A_page_whose_events_cannot_be_written_is_not_sent()
    {
        var queue = new RecordingQueue(fails: true);

        var (response, body) = await RequestAsync(queue, async (context, events) =>
        {
            events.Enqueue([Read()]);
            await WritePageAsync(context);
        });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("The error page.", body);
        Assert.DoesNotContain(Found, body);
    }

    // A handler that answers without writing a body (a redirect, an empty answer): its events are written all the same,
    // and a failure is still a failure.
    [Fact]
    public async Task A_response_with_no_body_still_writes_its_events_first_and_fails_if_it_cannot()
    {
        var queue = new RecordingQueue();
        var (response, _) = await RequestAsync(queue, (context, events) =>
        {
            events.Enqueue([Read()]);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal([(1, false)], queue.Writes);

        var (failed, body) = await RequestAsync(new RecordingQueue(fails: true), (context, events) =>
        {
            events.Enqueue([Read()]);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal("The error page.", body);
    }

    // A handler that fails after reading something: what it read was read, and is recorded, as it was when each
    // event was written as it happened. Nothing of a page is sent.
    [Fact]
    public async Task A_handler_that_fails_still_has_its_reads_recorded_and_sends_no_page()
    {
        var queue = new RecordingQueue();

        var (response, body) = await RequestAsync(queue, async (context, events) =>
        {
            events.Enqueue([Read()]);
            await context.Response.WriteAsync(Found);
            throw new InvalidOperationException("Synthetic: the handler failed.");
        });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("The error page.", body);
        Assert.Equal([(1, false)], queue.Writes);
    }

    // The page is held while the handler makes it, however much it writes and flushes, and arrives whole, with its
    // headers, after its events are written.
    [Fact]
    public async Task A_page_is_sent_whole_and_in_order_after_its_events()
    {
        var queue = new RecordingQueue();
        var large = new string('x', 300_000);

        var (response, body) = await RequestAsync(queue, async (context, events) =>
        {
            events.Enqueue([Read()]);
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(Found);
            await context.Response.Body.FlushAsync();
            // Written and flushed by the handler, and still not on its way: the events haven't been written yet.
            Assert.False(context.Response.HasStarted);
            Assert.Empty(queue.Writes);
            await context.Response.WriteAsync(large);
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(Found + large, body);
        Assert.Equal([(1, false)], queue.Writes);
    }

    [Fact]
    public async Task An_event_the_on_prem_app_would_not_take_fails_the_request_where_it_is_recorded_and_the_page_is_not_sent()
    {
        var queue = new RecordingQueue();
        var refused = Read();
        refused.Action = AuditActions.SignNote;

        var (response, body) = await RequestAsync(queue, async (context, events) =>
        {
            events.Enqueue([Read()]);
            events.Enqueue([refused]);
            await WritePageAsync(context);
        });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain(Found, body);
        // The request failed before anything was shown; the read it had recorded is still written, since it happened.
        Assert.Equal([(1, false)], queue.Writes);
    }

    // Outside a request (a scope of its own, as a background service has), there is nothing to hold an event for.
    [Fact]
    public void Outside_a_request_an_event_is_written_at_once()
    {
        var queue = new RecordingQueue();
        var events = new RequestAuditEvents(queue);

        events.Enqueue([Read()]);
        events.Enqueue([Read(), Read()]);

        Assert.Equal([(1, false), (2, false)], queue.Writes);
    }
}
