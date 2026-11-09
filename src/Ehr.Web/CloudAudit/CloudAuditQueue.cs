using System.Globalization;
using System.Text.Json;
using Ehr.Data.Audit;
using Ehr.Domain.Audit;

namespace Ehr.Web.CloudAudit;

// The cloud app's audit events, kept on its own disk until the on-prem app has them. The cloud app reads a replica
// that takes no writes, so this is where a PHI read it serves is recorded; CloudAuditShipper sends the events on.
//
// Two files in Ehr:CloudAudit:Directory: audit-events.jsonl, one event per line, appended and flushed to disk before
// Enqueue returns; and audit-events.shipped, the byte offset up to which the on-prem app has confirmed them. Once
// everything is shipped the offset goes back to 0 first and the events file is emptied after, so a crash between the
// two only ships some events twice, which the on-prem app ignores (AuditEventImport). A line cut short by a crash was
// never confirmed to anyone, so it is dropped at startup. The files are as encrypted as the disk they're on, like the
// databases (see "Encryption" in infra/compose/README.md).
public sealed class CloudAuditQueue : IAuditQueue, IDisposable
{
    const byte NewLine = (byte)'\n';

    static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    readonly Lock gate = new();

    readonly string shippedPath;

    readonly FileStream events;

    public CloudAuditQueue(string directory)
    {
        Directory.CreateDirectory(directory);
        EventsPath = Path.Combine(directory, "audit-events.jsonl");
        shippedPath = Path.Combine(directory, "audit-events.shipped");
        events = new FileStream(EventsPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        events.SetLength(EndOfLastLine());
    }

    public string EventsPath { get; }

    public void Enqueue(AuditEvent auditEvent)
    {
        var line = JsonSerializer.SerializeToUtf8Bytes(auditEvent, Json);
        lock (gate)
        {
            var start = events.Seek(0, SeekOrigin.End);
            try
            {
                events.Write(line);
                events.WriteByte(NewLine);
                events.Flush(flushToDisk: true);
            }
            catch
            {
                // A half-written line would run into the next one.
                events.SetLength(start);
                throw;
            }
        }
    }

    // Up to max events the on-prem app hasn't confirmed, oldest first, and the offset to confirm once it has them.
    public (List<AuditEvent> Events, long End) ReadPending(int max)
    {
        lock (gate)
        {
            var position = Math.Min(ReadShipped(), events.Length);
            var pending = new List<AuditEvent>();
            using var reader = new FileStream(EventsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            reader.Position = position;
            using var buffered = new BufferedStream(reader);
            var line = new MemoryStream();
            var end = position;
            int next;
            while (pending.Count < max && (next = buffered.ReadByte()) != -1)
            {
                position++;
                if (next != NewLine)
                {
                    line.WriteByte((byte)next);
                    continue;
                }

                pending.Add(JsonSerializer.Deserialize<AuditEvent>(line.GetBuffer().AsSpan(0, (int)line.Length), Json)!);
                line.SetLength(0);
                end = position;
            }

            return (pending, end);
        }
    }

    // The on-prem app has every event before this offset.
    public void MarkShipped(long end)
    {
        lock (gate)
        {
            if (end < events.Length)
            {
                WriteShipped(end);
                return;
            }

            // Nothing left: start both files over, in this order (see above).
            WriteShipped(0);
            events.SetLength(0);
            events.Flush(flushToDisk: true);
        }
    }

    public void Dispose() => events.Dispose();

    long ReadShipped() =>
        File.Exists(shippedPath) ? long.Parse(File.ReadAllText(shippedPath), CultureInfo.InvariantCulture) : 0;

    void WriteShipped(long offset)
    {
        var temporary = shippedPath + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write))
        {
            file.Write(System.Text.Encoding.ASCII.GetBytes(offset.ToString(CultureInfo.InvariantCulture)));
            file.Flush(flushToDisk: true);
        }

        File.Move(temporary, shippedPath, overwrite: true);
    }

    // Where the last complete line ends: the file's length, unless a crash cut the last line short.
    long EndOfLastLine()
    {
        for (var position = events.Length - 1; position >= 0; position--)
        {
            events.Position = position;
            if (events.ReadByte() == NewLine)
            {
                return position + 1;
            }
        }

        return 0;
    }
}
