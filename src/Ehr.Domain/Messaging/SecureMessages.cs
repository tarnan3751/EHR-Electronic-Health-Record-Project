using Ehr.Data;
using Ehr.Data.Messaging;
using Ehr.Domain.Accounts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ehr.Domain.Messaging;

// Sending secure messages (FR-18; the expansion plan's step 5). Called inside the page's database transaction. Threads
// and messages are read through PhiReader. Who may write is the page's rule and the database's (a patient in their own
// threads, a clinician for a patient on their panel: db/policies/30-rls.sql); the database also sets who sent a
// message and when, puts a patient's message in the care team's inboxes, and records the write (90-messages.sql).
public sealed class SecureMessages(EhrDbContext db, IEmailSender email)
{
    public const int MaximumSubject = MessageThreadConfiguration.MaximumSubject;

    public const int MaximumBody = MessageConfiguration.MaximumBody;

    // What is wrong with a message before it goes to the database, by field. A reply has no subject.
    public static Dictionary<string, string> Check(string? subject, string? body, bool reply)
    {
        var errors = new Dictionary<string, string>();
        if (!reply && string.IsNullOrWhiteSpace(subject))
        {
            errors["Subject"] = "Enter a subject.";
        }
        else if (subject?.Trim().Length > MaximumSubject)
        {
            errors["Subject"] = $"Shorten the subject to {MaximumSubject} characters or fewer.";
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            errors["Body"] = "Write your message.";
        }
        else if (body.Trim().Length > MaximumBody)
        {
            errors["Body"] = $"Shorten the message to {MaximumBody} characters or fewer.";
        }

        return errors;
    }

    // Starts a thread with its first message: the new thread's ID when it was sent.
    public async Task<(SendResult Result, Guid Thread)> StartAsync(Guid patient, string sender, string subject, string body, CancellationToken cancellationToken)
    {
        var thread = new MessageThread { Id = Guid.CreateVersion7(), PatientId = patient, Subject = subject.Trim() };
        return (await SaveAsync(cancellationToken, thread, NewMessage(thread.Id, patient, sender, body)), thread.Id);
    }

    // Adds a message to a thread of this patient's.
    public Task<SendResult> ReplyAsync(Guid thread, Guid patient, string sender, string body, CancellationToken cancellationToken) =>
        SaveAsync(cancellationToken, NewMessage(thread, patient, sender, body));

    // What the portal says when a patient has reached the day's or the hour's limit.
    public const string TooMany = "You've sent a lot of messages today. Please call the clinic, or try again later.";

    // Records that the signed-in side has now read the other side's messages in a thread: the patient, the staff's; a
    // clinician with the patient on their panel, the patient's. The database marks each once and refuses anyone else,
    // so for a reader who may not mark them, nothing happens.
    public async Task MarkReadAsync(IEnumerable<Guid> messages, CancellationToken cancellationToken)
    {
        const string savepoint = "messages_read";
        var transaction = db.Database.CurrentTransaction!;
        foreach (var id in messages)
        {
            await transaction.CreateSavepointAsync(savepoint, cancellationToken);
            // A stand-in for the row, with its key: only read_at is sent, and the database sets its value.
            var message = new Message { Id = id, SenderKind = "", Body = "", ReadAt = DateTimeOffset.UtcNow };
            var entry = db.Attach((object)message);
            entry.Property(nameof(Message.ReadAt)).IsModified = true;
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                await transaction.ReleaseSavepointAsync(savepoint, cancellationToken);
            }
            catch (Exception e) when (e is DbUpdateConcurrencyException
                || e is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.CheckViolation or PostgresErrorCodes.InsufficientPrivilege } })
            {
                await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
            }
            finally
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    // Tells the patient, at their portal account's address, that the clinic sent a message: that and a link to the
    // portal, and nothing else. No subject, no text, no names: email isn't a place for health details. A patient with
    // no portal account gets nothing. And one notice is enough: unreadFromStaff counts the staff messages the patient
    // hasn't opened, the one just sent included, and when there were others already they have been told. So a run of
    // replies doesn't use up the few emails an address may be sent in an hour (CappedEmailSender), which a password
    // reset also draws on.
    public async Task NotifyPatientAsync(Guid patient, int unreadFromStaff, string portalLink, CancellationToken cancellationToken)
    {
        if (unreadFromStaff > 1)
        {
            return;
        }

        var address = await db.PatientUsers.AsNoTracking().Where(u => u.PatientId == patient && u.EmailConfirmed)
            .Select(u => u.Email).FirstOrDefaultAsync(cancellationToken);
        if (address is not null)
        {
            await email.SendAsync(address, "You have a new message from your clinic",
                "Your clinic sent you a message. For your privacy it isn't included in this email. Sign in to the patient portal to read it.",
                new EmailAction("Read your message", portalLink), cancellationToken);
        }
    }

    static Message NewMessage(Guid thread, Guid patient, string sender, string body) =>
        new() { Id = Guid.CreateVersion7(), ThreadId = thread, PatientId = patient, SenderKind = sender, Body = body.Trim() };

    // Inserts the rows, or none of them. Reads nothing: row-level security and the triggers decide.
    async Task<SendResult> SaveAsync(CancellationToken cancellationToken, params object[] rows)
    {
        const string savepoint = "message";
        var transaction = db.Database.CurrentTransaction!;
        await transaction.CreateSavepointAsync(savepoint, cancellationToken);
        var entries = rows.Select(row => db.Add(row)).ToList();
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.ReleaseSavepointAsync(savepoint, cancellationToken);
            return SendResult.Sent;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException
        {
            // Refused by row-level security, or the thread isn't this patient's; or the patient has reached the limit.
            SqlState: PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.CheckViolation or PostgresErrorCodes.ForeignKeyViolation
                or PostgresErrorCodes.ProgramLimitExceeded,
        } refused)
        {
            await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
            return refused.SqlState == PostgresErrorCodes.ProgramLimitExceeded ? SendResult.TooMany : SendResult.Refused;
        }
        finally
        {
            foreach (var entry in entries)
            {
                entry.State = EntityState.Detached;
            }
        }
    }
}

public enum SendResult
{
    Sent,

    // Not this account's to send.
    Refused,

    // A patient has started too many conversations today, or sent too many messages this hour.
    TooMany,
}

// A thread as the lists show it: its subject, when its latest message was sent, and how many messages from the other
// side the reader hasn't opened.
public sealed record ThreadEntry(Guid Id, string Subject, DateTimeOffset LastMessageAt, int Unread);

// One message as a thread's page shows it. From is "You", "Your care team" or a staff member's name, as the reader
// should see it.
public sealed record MessageEntry(Guid Id, string SenderKind, string From, string Body, DateTimeOffset SentAt, DateTimeOffset? ReadAt);
