using Ehr.Data.Audit;
using Ehr.Web.CloudAudit;
using Xunit;

namespace Ehr.Web.Tests;

// The cloud app's audit queue on disk: what survives a restart, what a crash leaves, and when the file starts over.
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

    static AuditEvent Event() => new()
    {
        Id = Guid.CreateVersion7(),
        OccurredAt = DateTimeOffset.UtcNow,
        Action = AuditActions.Read,
        Source = AuditSources.Cloud,
        ActorId = Guid.CreateVersion7(),
        PatientId = Guid.CreateVersion7(),
        TableName = "patients",
    };
}
