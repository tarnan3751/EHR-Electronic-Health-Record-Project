using Ehr.Data.Audit;
using Ehr.Domain.Audit;

namespace Ehr.Web.CloudAudit;

// Where audit events are kept durably on the cloud site: CloudAuditQueue, which writes what it is given and returns
// only once it is on the disk.
public interface IDurableAuditQueue
{
    void Enqueue(IReadOnlyCollection<AuditEvent> auditEvents);
}

// One request's audit events on the cloud site (the expansion plan's step 10c). A page that reads a patient's chart
// and their signed note records two events; each used to be written to the queue and flushed to disk as it happened,
// and a flush is the most expensive thing an off-site page does (about 4 ms on the machine this was measured on,
// against well under 1 ms for a database statement). Now a request's events are held here as they are recorded and
// written together, with one flush, before any of the response is sent (CloudAuditFlushMiddleware).
//
// The rule is the one the on-prem app keeps by committing a read's audit event in the request's transaction: nothing
// a read found is shown without its durable record. If the write fails, the request fails and none of the page is
// sent. Outside a request there is nothing to wait for: the event is written at once, as before.
public sealed class RequestAuditEvents(IDurableAuditQueue queue) : IAuditQueue
{
    readonly List<AuditEvent> held = [];

    bool collecting;

    // From now until Flush, events are held. Called by the middleware at the start of a request.
    public void Collect() => collecting = true;

    public void Enqueue(IReadOnlyCollection<AuditEvent> auditEvents)
    {
        // Refused when recorded, as the queue itself refuses: the handler that recorded an event the on-prem app
        // wouldn't take fails there, not when the page is about to be sent.
        foreach (var auditEvent in auditEvents)
        {
            if (!CloudAuditEvents.IsAllowed(auditEvent))
            {
                throw new InvalidOperationException(
                    $"The cloud app may record only what the on-prem app takes (CloudAuditEvents); a '{auditEvent.Action}' event in this shape isn't.");
            }
        }

        if (collecting)
        {
            held.AddRange(auditEvents);
        }
        else
        {
            queue.Enqueue(auditEvents);
        }
    }

    // Writes what is held, once, and stops holding: whatever is recorded afterwards is written as it comes. Throws
    // if the write does; the events it couldn't write are dropped with the request that failed, which shows nothing.
    public void Flush()
    {
        collecting = false;
        if (held.Count == 0)
        {
            return;
        }

        AuditEvent[] events = [.. held];
        held.Clear();
        queue.Enqueue(events);
    }
}

// Holds a request's audit events and writes them together before any of the response is sent (RequestAuditEvents).
// The page a handler makes is kept in memory while it is made; when the handler has finished, the events are written
// and flushed to disk, and only then is the page sent. So the order is certain, whatever the handler and the view did:
// record, then show. If the write fails, the exception goes to the error pages, which come before this in the
// pipeline, and the page that was made is thrown away unsent.
//
// Only for requests routed to an endpoint: pages and the API. Static files record nothing and are not held. Forwarded
// requests never get here (CloudForwardingMiddleware answers them first).
public sealed class CloudAuditFlushMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, RequestAuditEvents events)
    {
        if (context.GetEndpoint() is null)
        {
            await next(context);
            return;
        }

        events.Collect();
        var response = context.Response.Body;
        await using var page = new MemoryStream();
        context.Response.Body = page;
        try
        {
            try
            {
                await next(context);
            }
            catch
            {
                // The handler failed after reading something: the reads it recorded still happened, and are still
                // written, as they were when each was written at once. The failure it raised is the one reported.
                try
                {
                    events.Flush();
                }
                catch (Exception)
                {
                }

                throw;
            }

            events.Flush();
            context.Response.Body = response;
            page.Position = 0;
            await page.CopyToAsync(response, context.RequestAborted);
        }
        finally
        {
            context.Response.Body = response;
        }
    }
}
