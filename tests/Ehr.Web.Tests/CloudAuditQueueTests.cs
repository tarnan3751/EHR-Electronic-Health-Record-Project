using System.Net;
using System.Text.Json;
using Ehr.Data.Audit;
using Ehr.Web.CloudAudit;
using Ehr.Web.Forwarding;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ehr.Web.Tests;

// The cloud app's audit queue on disk: what it refuses to take, what survives a restart, what a crash leaves, and when
// the file starts over; and that neither a queue it can't read nor a try that throws stops its shipper.
public sealed class CloudAuditQueueTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), $"ehr-queue-{Guid.NewGuid():N}");

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public void Events_wait_until_shipped_and_survive_a_restart()
    {
        var first = Event();
        var second = Event();
        using (var queue = new CloudAuditQueue(directory))
        {
            queue.Enqueue(first);
            queue.Enqueue(second);
            var (batch, end) = queue.ReadPending(1);
            Assert.Equal([first.Id], batch.Select(e => e.Id));
            queue.MarkShipped(end);
        }

        using var restarted = new CloudAuditQueue(directory);
        var (pending, _) = restarted.ReadPending(10);
        Assert.Equal([second.Id], pending.Select(e => e.Id));
    }

    [Fact]
    public void A_batch_comes_back_in_order_and_survives_a_restart()
    {
        AuditEvent[] batch = [Event(), Event(), Event()];
        using (var queue = new CloudAuditQueue(directory))
        {
            queue.Enqueue(batch);
        }

        using var restarted = new CloudAuditQueue(directory);
        Assert.Equal(batch.Select(e => e.Id), restarted.ReadPending(10).Events.Select(e => e.Id));
    }

    // What the on-prem app would refuse fails the request that records it, rather than jamming the queue behind it.
    [Theory]
    [InlineData("a sign-in")]
    [InlineData("a read without its row")]
    [InlineData("a sign-out with a table")]
    [InlineData("one bad event among good ones")]
    public void An_event_the_on_prem_app_would_refuse_is_never_queued(string what)
    {
        using var queue = new CloudAuditQueue(directory);
        var earlier = Event();
        queue.Enqueue(earlier);
        var length = new FileInfo(queue.EventsPath).Length;
        var bad = Event();
        switch (what)
        {
            case "a sign-in":
                bad.Action = AuditActions.SignIn;
                bad.TableName = null;
                bad.RowId = null;
                bad.PatientId = null;
                break;
            case "a read without its row":
                bad.RowId = null;
                break;
            case "a sign-out with a table":
                bad.Action = AuditActions.SignOut;
                bad.RowId = null;
                break;
            case "one bad event among good ones":
                bad.Details = "{}";
                break;
        }

        AuditEvent[] batch = what == "one bad event among good ones" ? [Event(), bad, Event()] : [bad];
        Assert.Throws<InvalidOperationException>(() => queue.Enqueue(batch));

        Assert.Equal(length, new FileInfo(queue.EventsPath).Length);
        Assert.Equal([earlier.Id], queue.ReadPending(10).Events.Select(e => e.Id));
    }

    [Fact]
    public void Once_everything_is_shipped_the_file_starts_over()
    {
        using var queue = new CloudAuditQueue(directory);
        queue.Enqueue(Event());
        var (_, end) = queue.ReadPending(10);

        queue.MarkShipped(end);

        Assert.Equal(0, new FileInfo(queue.EventsPath).Length);
        Assert.Empty(queue.ReadPending(10).Events);
        var later = Event();
        queue.Enqueue(later);
        Assert.Equal([later.Id], queue.ReadPending(10).Events.Select(e => e.Id));
    }

    [Fact]
    public void A_line_cut_short_by_a_crash_is_dropped_at_startup()
    {
        var kept = Event();
        string path;
        using (var queue = new CloudAuditQueue(directory))
        {
            queue.Enqueue(kept);
            path = queue.EventsPath;
        }

        File.AppendAllText(path, "{\"id\":\"cut-sho");

        using var restarted = new CloudAuditQueue(directory);
        Assert.Equal([kept.Id], restarted.ReadPending(10).Events.Select(e => e.Id));
        var next = Event();
        restarted.Enqueue(next);
        Assert.Equal([kept.Id, next.Id], restarted.ReadPending(10).Events.Select(e => e.Id));
    }

    [Fact]
    public async Task A_queue_the_shipper_cant_read_still_lets_it_track_the_on_prem_app()
    {
        // A whole line that isn't an event, which only a damaged disk could leave.
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "audit-events.jsonl"), "not an event\n", TestContext.Current.CancellationToken);
        using var queue = new CloudAuditQueue(directory);
        var answer = HttpStatusCode.InternalServerError;
        var status = new OnPremStatus();
        var log = new ErrorLog();
        using var shipper = Shipper(queue, () => new HttpResponseMessage(answer), status, log);

        // The primary down, then back: the check-in still goes, and the status follows it.
        Assert.False(await shipper.ShipAsync(TestContext.Current.CancellationToken));
        Assert.False(status.IsReachable);
        answer = HttpStatusCode.NoContent;
        Assert.False(await shipper.ShipAsync(TestContext.Current.CancellationToken));
        Assert.True(status.IsReachable);

        // Someone has to repair the queue, and the log says so; nothing in it was marked shipped.
        Assert.True(log.Logged.Task.IsCompleted);
        Assert.Throws<JsonException>(() => queue.ReadPending(10));
    }

    [Fact]
    public async Task A_try_that_throws_doesnt_stop_the_shipper()
    {
        using var queue = new CloudAuditQueue(directory);
        var log = new ErrorLog();
        using var shipper = Shipper(queue, () => throw new InvalidOperationException("Not what the shipper expects."), new OnPremStatus(), log);

        await shipper.StartAsync(TestContext.Current.CancellationToken);
        await Task.WhenAny(log.Logged.Task, shipper.ExecuteTask!).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // The try failed and was logged, and the shipper is waiting for the next one rather than stopped.
        Assert.True(log.Logged.Task.IsCompleted);
        Assert.False(shipper.ExecuteTask!.IsCompleted);
        await shipper.StopAsync(TestContext.Current.CancellationToken);
    }

    // A shipper whose link to the on-prem app gives this answer to every request.
    static CloudAuditShipper Shipper(CloudAuditQueue queue, Func<HttpResponseMessage> answer, OnPremStatus status, ErrorLog log) =>
        new(queue, new OnPremApp("https://onprem.test", new HttpMessageInvoker(new Answers(answer))), status,
            new CloudAuditKey(WebApps.CloudAuditKey), log);

    // A read as the PHI reader records one, the kind the queue takes.
    static AuditEvent Event()
    {
        var patient = Guid.CreateVersion7();
        return new()
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
    }

    sealed class Answers(Func<HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer());
    }

    // Completes at the first error logged.
    sealed class ErrorLog : ILogger<CloudAuditShipper>
    {
        public TaskCompletionSource Logged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
            {
                Logged.TrySetResult();
            }
        }
    }
}
